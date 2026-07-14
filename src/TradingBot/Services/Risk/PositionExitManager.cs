using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Risk;

/// <summary>
/// Mechanical exits. For each open position, check three triggers (stop-loss, take-profit,
/// time-based) and return the ones that should close. Pure function — caller dedupes against
/// pending sells and submits the actual orders.
/// </summary>
public sealed class PositionExitManager
{
    private readonly ExitOptions _opts;
    private readonly PositionOpenTimeTracker _tracker;
    private readonly PositionPeakTracker _peaks;
    private readonly ILogger<PositionExitManager> _log;

    public PositionExitManager(
        IOptions<ExitOptions> opts,
        PositionOpenTimeTracker tracker,
        PositionPeakTracker peaks,
        ILogger<PositionExitManager> log)
    {
        _opts = opts.Value;
        _tracker = tracker;
        _peaks = peaks;
        _log = log;
    }

    public IReadOnlyList<ExitCandidate> EvaluateExits(
        IReadOnlyList<PositionSnapshot> positions,
        DateTimeOffset now)
    {
        if (!_opts.Enabled || positions.Count == 0) return Array.Empty<ExitCandidate>();

        var exits = new List<ExitCandidate>();
        foreach (var p in positions)
        {
            if (p.Quantity <= 0 || p.AverageEntryPrice <= 0m) continue;

            // Current price = market value per share. No extra API call needed.
            var currentPrice = p.MarketValue / p.Quantity;
            var pnlPct = (double)((currentPrice - p.AverageEntryPrice) / p.AverageEntryPrice);
            var openedAt = _tracker.Get(p.Ticker);

            // --- Stop loss (worst trigger, check first) ----------------------------------
            if (_opts.StopLossPercent > 0 && pnlPct <= -_opts.StopLossPercent)
            {
                exits.Add(new ExitCandidate(
                    Ticker: p.Ticker,
                    Quantity: p.Quantity,
                    Trigger: ExitTrigger.StopLoss,
                    Reason: $"Stop loss: {pnlPct:P2} (entry {p.AverageEntryPrice:C}, now {currentPrice:C})",
                    PnLPercent: pnlPct,
                    OpenedAt: openedAt));
                continue;
            }

            // --- Take profit (hard ceiling, usually disabled in favor of trailing stop) --
            if (_opts.TakeProfitPercent > 0 && pnlPct >= _opts.TakeProfitPercent)
            {
                exits.Add(new ExitCandidate(
                    Ticker: p.Ticker,
                    Quantity: p.Quantity,
                    Trigger: ExitTrigger.TakeProfit,
                    Reason: $"Take profit: +{pnlPct:P2} (entry {p.AverageEntryPrice:C}, now {currentPrice:C})",
                    PnLPercent: pnlPct,
                    OpenedAt: openedAt));
                continue;
            }

            // --- Trailing stop ----------------------------------------------------------
            // Only armed once the position is up at least ActivationPercent from entry. Sells
            // when current price drops TrailingStopPercent below the peak seen since open.
            if (_opts.TrailingStopPercent > 0
                && pnlPct >= _opts.TrailingStopActivationPercent)
            {
                var peak = _peaks.GetPeak(p.Ticker) ?? currentPrice;
                var dropFromPeak = peak > 0m ? (double)((currentPrice - peak) / peak) : 0.0;

                if (dropFromPeak <= -_opts.TrailingStopPercent)
                {
                    exits.Add(new ExitCandidate(
                        Ticker: p.Ticker,
                        Quantity: p.Quantity,
                        Trigger: ExitTrigger.TrailingStop,
                        Reason: $"Trailing stop: peak {peak:C}, now {currentPrice:C} ({dropFromPeak:P2} off peak; banking +{pnlPct:P2} from entry)",
                        PnLPercent: pnlPct,
                        OpenedAt: openedAt));
                    continue;
                }
            }

            // --- Time-based -------------------------------------------------------------
            if (_opts.MaxHoldDays > 0 && openedAt is not null)
            {
                var held = now - openedAt.Value;
                if (held.TotalDays >= _opts.MaxHoldDays)
                {
                    exits.Add(new ExitCandidate(
                        Ticker: p.Ticker,
                        Quantity: p.Quantity,
                        Trigger: ExitTrigger.TimeLimit,
                        Reason: $"Held {held.TotalDays:F1}d (>= {_opts.MaxHoldDays}d), {pnlPct:P2}",
                        PnLPercent: pnlPct,
                        OpenedAt: openedAt));
                }
            }
        }

        return exits;
    }

    /// <summary>Per-position "distance to each trigger" for the dashboard.</summary>
    public IReadOnlyList<object> DistanceSnapshot(IReadOnlyList<PositionSnapshot> positions, DateTimeOffset now)
    {
        if (positions.Count == 0) return Array.Empty<object>();

        var rows = new List<object>(positions.Count);
        foreach (var p in positions)
        {
            if (p.Quantity <= 0 || p.AverageEntryPrice <= 0m) continue;

            var currentPrice = p.MarketValue / p.Quantity;
            var pnlPct = (double)((currentPrice - p.AverageEntryPrice) / p.AverageEntryPrice);
            var openedAt = _tracker.Get(p.Ticker);
            var heldDays = openedAt is null ? 0.0 : (now - openedAt.Value).TotalDays;

            var peak = _peaks.GetPeak(p.Ticker);
            var peakPctFromEntry = peak is not null && p.AverageEntryPrice > 0m
                ? (double)((peak.Value - p.AverageEntryPrice) / p.AverageEntryPrice)
                : (double?)null;
            var dropFromPeak = peak is not null && peak.Value > 0m
                ? (double)((currentPrice - peak.Value) / peak.Value)
                : (double?)null;
            var trailArmed = _opts.TrailingStopPercent > 0
                && pnlPct >= _opts.TrailingStopActivationPercent;

            rows.Add(new
            {
                ticker = p.Ticker,
                quantity = p.Quantity,
                pnlPercent = pnlPct,
                stopLossAt = _opts.StopLossPercent > 0 ? -_opts.StopLossPercent : (double?)null,
                takeProfitAt = _opts.TakeProfitPercent > 0 ? _opts.TakeProfitPercent : (double?)null,
                peakPctFromEntry,
                dropFromPeak,
                trailArmed,
                trailingStopPercent = _opts.TrailingStopPercent,
                trailingStopActivationPercent = _opts.TrailingStopActivationPercent,
                daysHeld = heldDays,
                maxHoldDays = _opts.MaxHoldDays,
                openedAt
            });
        }
        return rows;
    }

    public bool ExitsEnabled => _opts.Enabled;
}
