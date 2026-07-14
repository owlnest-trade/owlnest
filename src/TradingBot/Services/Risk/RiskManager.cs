using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.Broker;

namespace TradingBot.Services.Risk;

/// <summary>
/// Pre-trade gates. Every order proposed by the bot must pass through Evaluate() first.
/// Rejections are still returned as TradeDecision objects (Approved=false) so the worker can log the audit trail.
/// </summary>
public sealed class RiskManager
{
    private readonly TradingOptions _opts;
    private readonly ILogger<RiskManager> _log;
    private readonly object _gate = new();

    private DateOnly _sessionDateEt;
    private int _tradesToday;
    private decimal _sessionStartEquity;

    public RiskManager(IOptions<TradingOptions> opts, ILogger<RiskManager> log)
    {
        _opts = opts.Value;
        _log = log;
        _sessionDateEt = TodayEt();
    }

    /// <summary>
    /// Called once per tick after fetching the account snapshot. Resets daily counters on a new trading day
    /// and records the session-open equity so we can enforce the daily loss cap.
    /// </summary>
    public void OnAccountSnapshot(AccountSnapshot account)
    {
        lock (_gate)
        {
            var today = TodayEt();
            if (today != _sessionDateEt)
            {
                _log.LogInformation("New trading day {Date}; resetting risk counters", today);
                _sessionDateEt = today;
                _tradesToday = 0;
                _sessionStartEquity = account.LastEquityAtSessionOpen > 0
                    ? account.LastEquityAtSessionOpen
                    : account.Equity;
            }
            else if (_sessionStartEquity == 0m)
            {
                _sessionStartEquity = account.LastEquityAtSessionOpen > 0
                    ? account.LastEquityAtSessionOpen
                    : account.Equity;
            }
        }
    }

    public TradeDecision Evaluate(
        NewsItem news,
        SentimentResult sentiment,
        AccountSnapshot account,
        PositionSnapshot? existing,
        decimal price,
        decimal pendingBuyNotional = 0m,
        long pendingSellQty = 0L)
    {
        var side = sentiment.Sentiment == TradingBot.Models.Sentiment.Bullish ? TradeSide.Buy : TradeSide.Sell;

        TradeDecision Reject(string reason, int qty = 0) =>
            new(news.Ticker, side, qty, Approved: false, Reason: reason, Sentiment: sentiment, DecidedAt: DateTimeOffset.UtcNow);

        // --- Gate 1: master kill switch ---------------------------------------------------
        if (!_opts.TradingEnabled)
            return Reject("Trading disabled by config (Trading:TradingEnabled=false)");

        // --- Gate 2: sentiment must be actionable + confident ----------------------------
        if (!sentiment.IsActionable)
            return Reject($"Sentiment not actionable ({sentiment.Sentiment}, conf {sentiment.Confidence:P0})");
        if (sentiment.Confidence < _opts.MinConfidence)
            return Reject($"Confidence {sentiment.Confidence:P0} below threshold {_opts.MinConfidence:P0}");
        if (sentiment.Sentiment == TradingBot.Models.Sentiment.Neutral)
            return Reject("Neutral sentiment is not tradable");

        // --- Gate 3: price sanity --------------------------------------------------------
        if (price <= 0m)
            return Reject("No valid market price available");

        // --- Gate 4: daily-loss kill switch ----------------------------------------------
        decimal startEquity, dayDrawdown;
        int tradesToday;
        lock (_gate)
        {
            startEquity = _sessionStartEquity;
            tradesToday = _tradesToday;
        }
        if (startEquity > 0m)
        {
            dayDrawdown = (startEquity - account.Equity) / startEquity;
            if (dayDrawdown >= (decimal)_opts.MaxDailyLossFraction)
                return Reject($"Daily loss cap hit ({dayDrawdown:P2} >= {_opts.MaxDailyLossFraction:P2})");
        }

        // --- Gate 5: trades-per-day cap --------------------------------------------------
        if (tradesToday >= _opts.MaxTradesPerDay)
            return Reject($"Daily trade count cap hit ({tradesToday}/{_opts.MaxTradesPerDay})");

        // --- Gate 6: sells only against an existing long --------------------------------
        if (side == TradeSide.Sell)
        {
            var ownedQty = existing?.Quantity ?? 0;
            // Subtract qty we're already in the process of selling (pending sell orders).
            var sellableQty = ownedQty - pendingSellQty;
            if (sellableQty <= 0)
                return Reject($"Bearish but no long left to close (own {ownedQty}, pending sells {pendingSellQty})");
            return Approve(side, (int)sellableQty, "Closing remaining long on bearish signal");
        }

        // --- Gate 7: buy sizing ----------------------------------------------------------
        var maxDollarsPerPosition = account.Equity * (decimal)_opts.MaxPositionFraction;
        // Include pending (unfilled) buys in existing exposure so we don't stack 7 orders
        // on the same ticker just because the first 6 haven't filled yet.
        var positionValue = existing?.MarketValue ?? 0m;
        var existingValue = positionValue + pendingBuyNotional;
        var headroomDollars = maxDollarsPerPosition - existingValue;
        if (headroomDollars <= 0m)
            return Reject($"Position cap reached for {news.Ticker} (position {positionValue:C} + pending {pendingBuyNotional:C} >= cap {maxDollarsPerPosition:C})");

        // Don't exceed available buying power.
        var spendable = Math.Min(headroomDollars, account.BuyingPower);
        if (spendable <= 0m)
            return Reject("No buying power available");

        var qty = (int)Math.Floor(spendable / price);
        if (qty < 1)
            return Reject($"Sizing yielded < 1 share (spendable {spendable:C} at {price:C})");

        return Approve(side, qty, $"Buy sized by {_opts.MaxPositionFraction:P0} cap");

        TradeDecision Approve(TradeSide s, int qty, string reason) =>
            new(news.Ticker, s, qty, Approved: true, Reason: reason, Sentiment: sentiment, DecidedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>Increment the daily trade counter. Worker calls this only when the order actually submitted.</summary>
    public void RecordOrderSubmitted()
    {
        lock (_gate) { _tradesToday++; }
    }

    /// <summary>Read-only view of how many orders have been submitted today (for dashboard/telemetry).</summary>
    public int TradesToday
    {
        get { lock (_gate) return _tradesToday; }
    }

    private static DateOnly TodayEt()
    {
        var et = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et));
    }
}
