using Microsoft.EntityFrameworkCore;
using TradingBot.Web.Data;

namespace TradingBot.Web.Services.Backtesting;

public sealed class BacktestReplayService
{
    private readonly OwlNestDbContext _db;

    public BacktestReplayService(OwlNestDbContext db)
    {
        _db = db;
    }

    public async Task<BacktestReplayReport> ReplayAsync(
        string userId,
        int limit,
        decimal initialCapital,
        decimal tradeNotional,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 50, 5_000);
        initialCapital = Math.Clamp(initialCapital, 100m, 1_000_000m);
        tradeNotional = Math.Clamp(tradeNotional, 10m, initialCapital);

        var settings = await _db.UserSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var minConfidence = settings?.MinConfidence ?? 0.85;
        var bearishMinConfidence = settings?.BearishNewsMinConfidence ?? 0.80;

        var decisions = await _db.UserDecisions.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.Id)
            .Take(limit)
            .ToListAsync(ct);
        decisions.Reverse();

        var fromUtc = decisions.Count == 0 ? (DateTimeOffset?)null : decisions[0].AtUtc;
        var toUtc = decisions.Count == 0 ? (DateTimeOffset?)null : decisions[^1].AtUtc;

        var ordersQuery = _db.UserOrders.AsNoTracking()
            .Where(o => o.UserId == userId);
        if (fromUtc is not null) ordersQuery = ordersQuery.Where(o => o.SubmittedAtUtc >= fromUtc);
        if (toUtc is not null) ordersQuery = ordersQuery.Where(o => o.SubmittedAtUtc <= toUtc.Value.AddDays(7));
        var orders = await ordersQuery
            .OrderBy(o => o.SubmittedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

        var watchEvents = await _db.UserWatchlistEvents.AsNoTracking()
            .Where(e => e.UserId == userId && e.PriceUsd != null)
            .OrderByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(ct);

        var lastPrices = BuildLastPrices(decisions, orders, watchEvents);
        var priceSnapshots = decisions.Count(d => d.PriceUsd is > 0)
                            + orders.Count(o => o.PriceAtSubmitUsd is > 0 || o.FilledAvgPrice is > 0)
                            + watchEvents.Count(e => e.PriceUsd is > 0);

        var actualOrders = ReplayOrders("actual-orders", "Actual orders", orders, lastPrices, initialCapital, tradeNotional);
        var baseline = ReplayDecisions(
            "baseline",
            "Baseline sentiment",
            "Trades every actionable bullish or bearish sentiment event with enough confidence. Ignores confirmation, Grok, Claude, and macro context.",
            decisions, lastPrices, initialCapital, tradeNotional,
            d => IsBullishCandidate(d, minConfidence),
            d => IsBearishCandidate(d, bearishMinConfidence));
        var noConfirmation = ReplayDecisions(
            "no-confirmation",
            "No confirmation gate",
            "Production-style replay plus candidates that were stopped only by the confirmation gate.",
            decisions, lastPrices, initialCapital, tradeNotional,
            d => IsSubmittedBuy(d) || IsConfirmationRejected(d),
            d => IsSubmittedSell(d));
        var noGrok = ReplayDecisions(
            "no-grok",
            "No Grok gate",
            "Production-style replay plus candidates that Grok vetoed or errored.",
            decisions, lastPrices, initialCapital, tradeNotional,
            d => IsSubmittedBuy(d) || IsGrokRejected(d),
            d => IsSubmittedSell(d));
        var noClaude = ReplayDecisions(
            "no-claude",
            "No Claude gate",
            "Production-style replay plus candidates that Claude vetoed, cautioned, or errored.",
            decisions, lastPrices, initialCapital, tradeNotional,
            d => IsSubmittedBuy(d) || IsClaudeRejected(d),
            d => IsSubmittedSell(d));
        var macroTagged = ReplayDecisions(
            "macro",
            "Macro-tagged signals",
            "Trades sentiment-qualified candidates whose stored text looks macro-influenced. Older rows may undercount this until MacroSummary is populated consistently.",
            decisions, lastPrices, initialCapital, tradeNotional,
            d => IsBullishCandidate(d, minConfidence) && IsMacroTagged(d),
            d => IsBearishCandidate(d, bearishMinConfidence) && IsMacroTagged(d));

        var portfolios = new[] { actualOrders, baseline, noConfirmation, noGrok, noClaude, macroTagged };
        var actualPnl = actualOrders.Pnl;
        portfolios = portfolios
            .Select(p => p with { DeltaVsActual = p.Id == actualOrders.Id ? null : p.Pnl - actualPnl })
            .ToArray();

        return new BacktestReplayReport(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            InitialCapital: initialCapital,
            TradeNotional: tradeNotional,
            DecisionsAnalyzed: decisions.Count,
            OrdersAnalyzed: orders.Count,
            PriceSnapshots: priceSnapshots,
            Execution: BuildExecutionSummary(orders),
            Portfolios: portfolios);
    }

    private static Dictionary<string, decimal> BuildLastPrices(
        IEnumerable<UserDecision> decisions,
        IEnumerable<UserOrder> orders,
        IEnumerable<UserWatchlistEvent> watchEvents)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in watchEvents.OrderBy(e => e.AtUtc))
            if (e.PriceUsd is > 0) prices[e.Ticker] = e.PriceUsd.Value;
        foreach (var d in decisions)
            if (d.PriceUsd is > 0) prices[d.Ticker] = d.PriceUsd.Value;
        foreach (var o in orders)
        {
            var p = o.FilledAvgPrice is > 0 ? o.FilledAvgPrice : o.PriceAtSubmitUsd;
            if (p is > 0) prices[o.Ticker] = p.Value;
        }
        return prices;
    }

    private static ShadowPortfolioResult ReplayOrders(
        string id,
        string name,
        IReadOnlyList<UserOrder> orders,
        IReadOnlyDictionary<string, decimal> lastPrices,
        decimal initialCapital,
        decimal tradeNotional)
    {
        var sim = new PortfolioSimulator(initialCapital, tradeNotional);
        foreach (var o in orders)
        {
            var price = o.FilledAvgPrice is > 0 ? o.FilledAvgPrice : o.PriceAtSubmitUsd;
            if (price is not > 0) continue;

            if (o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase))
                sim.Buy(o.Ticker, price.Value);
            else if (o.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase))
                sim.Sell(o.Ticker, price.Value);
        }

        return sim.ToResult(id, name,
            "Normalizes real submitted orders to the same fixed notional size as the shadows, using fill price when available and submit price otherwise.",
            lastPrices);
    }

    private static ShadowPortfolioResult ReplayDecisions(
        string id,
        string name,
        string note,
        IReadOnlyList<UserDecision> decisions,
        IReadOnlyDictionary<string, decimal> lastPrices,
        decimal initialCapital,
        decimal tradeNotional,
        Func<UserDecision, bool> shouldBuy,
        Func<UserDecision, bool> shouldSell)
    {
        var sim = new PortfolioSimulator(initialCapital, tradeNotional);
        foreach (var d in decisions)
        {
            if (d.PriceUsd is not > 0) continue;
            if (shouldSell(d))
            {
                sim.Sell(d.Ticker, d.PriceUsd.Value);
                continue;
            }
            if (shouldBuy(d))
                sim.Buy(d.Ticker, d.PriceUsd.Value);
        }

        return sim.ToResult(id, name, note, lastPrices);
    }

    private static ExecutionSummary BuildExecutionSummary(IReadOnlyList<UserOrder> orders)
    {
        var filled = orders.Where(o => o.Status.Equals("filled", StringComparison.OrdinalIgnoreCase)).ToList();
        var slip = filled
            .Where(o => o.PriceAtSubmitUsd is > 0 && o.FilledAvgPrice is > 0)
            .Select(o =>
            {
                var signed = o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                    ? (o.FilledAvgPrice!.Value - o.PriceAtSubmitUsd!.Value) / o.PriceAtSubmitUsd.Value
                    : (o.PriceAtSubmitUsd!.Value - o.FilledAvgPrice!.Value) / o.PriceAtSubmitUsd.Value;
                return signed * 10_000m;
            })
            .ToList();

        return new ExecutionSummary(
            SubmittedOrders: orders.Count,
            FilledOrders: filled.Count,
            FillRate: orders.Count == 0 ? null : (double)filled.Count / orders.Count,
            AverageSlippageBps: slip.Count == 0 ? null : slip.Average());
    }

    private static bool IsBullishCandidate(UserDecision d, double minConfidence) =>
        d.Sentiment?.Equals("bullish", StringComparison.OrdinalIgnoreCase) == true
        && d.Actionable == true
        && (d.Confidence ?? 0) >= minConfidence;

    private static bool IsBearishCandidate(UserDecision d, double minConfidence) =>
        d.Sentiment?.Equals("bearish", StringComparison.OrdinalIgnoreCase) == true
        && d.Actionable == true
        && (d.Confidence ?? 0) >= minConfidence;

    private static bool IsSubmittedBuy(UserDecision d) =>
        d.Outcome.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
        && d.Side?.Equals("Buy", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSubmittedSell(UserDecision d) =>
        d.Outcome.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
        && d.Side?.Equals("Sell", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsConfirmationRejected(UserDecision d) =>
        IsBullishCandidate(d, 0)
        && d.OutcomeReason.StartsWith("Confirmation gate:", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrokRejected(UserDecision d) =>
        IsBullishCandidate(d, 0)
        && d.OutcomeReason.StartsWith("Grok ", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeRejected(UserDecision d) =>
        IsBullishCandidate(d, 0)
        && d.OutcomeReason.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase);

    private static bool IsMacroTagged(UserDecision d)
    {
        var haystack = string.Join(' ', d.MacroSummary, d.Reasoning, d.OutcomeReason, d.Headline).ToLowerInvariant();
        return haystack.Contains("macro")
            || haystack.Contains("fed")
            || haystack.Contains("fomc")
            || haystack.Contains("rate")
            || haystack.Contains("inflation")
            || haystack.Contains("cpi")
            || haystack.Contains("recession")
            || haystack.Contains("oil")
            || haystack.Contains("gold");
    }

    private sealed class PortfolioSimulator
    {
        private readonly decimal _tradeNotional;
        private readonly Dictionary<string, SimPosition> _positions = new(StringComparer.OrdinalIgnoreCase);
        private decimal _cash;
        private int _entries;
        private int _exits;
        private int _wins;
        private int _losses;
        private decimal _realizedPnl;

        public PortfolioSimulator(decimal initialCapital, decimal tradeNotional)
        {
            _cash = initialCapital;
            _tradeNotional = tradeNotional;
        }

        public void Buy(string ticker, decimal price)
        {
            if (price <= 0 || _cash <= 0 || _positions.ContainsKey(ticker)) return;
            var spend = Math.Min(_tradeNotional, _cash);
            if (spend < 1m) return;
            _positions[ticker] = new SimPosition(spend / price, price);
            _cash -= spend;
            _entries++;
        }

        public void Sell(string ticker, decimal price)
        {
            if (price <= 0 || !_positions.Remove(ticker, out var pos)) return;
            var proceeds = pos.Quantity * price;
            var pnl = proceeds - (pos.Quantity * pos.EntryPrice);
            _cash += proceeds;
            _realizedPnl += pnl;
            _exits++;
            if (pnl >= 0) _wins++; else _losses++;
        }

        public ShadowPortfolioResult ToResult(
            string id,
            string name,
            string note,
            IReadOnlyDictionary<string, decimal> lastPrices)
        {
            var openValue = 0m;
            var unrealized = 0m;
            foreach (var (ticker, pos) in _positions)
            {
                var mark = lastPrices.TryGetValue(ticker, out var p) && p > 0 ? p : pos.EntryPrice;
                openValue += pos.Quantity * mark;
                unrealized += pos.Quantity * (mark - pos.EntryPrice);
            }

            var equity = _cash + openValue;
            var initial = _cash + openValue - _realizedPnl - unrealized;
            var pnl = equity - initial;
            return new ShadowPortfolioResult(
                Id: id,
                Name: name,
                Note: note,
                EndingEquity: equity,
                Pnl: pnl,
                PnlPct: initial > 0 ? (double)(pnl / initial) : 0,
                RealizedPnl: _realizedPnl,
                UnrealizedPnl: unrealized,
                Entries: _entries,
                Exits: _exits,
                OpenPositions: _positions.Count,
                Wins: _wins,
                Losses: _losses,
                WinRate: _exits == 0 ? null : (double)_wins / _exits,
                DeltaVsActual: null);
        }

        private readonly record struct SimPosition(decimal Quantity, decimal EntryPrice);
    }
}

public sealed record BacktestReplayReport(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    decimal InitialCapital,
    decimal TradeNotional,
    int DecisionsAnalyzed,
    int OrdersAnalyzed,
    int PriceSnapshots,
    ExecutionSummary Execution,
    IReadOnlyList<ShadowPortfolioResult> Portfolios);

public sealed record ExecutionSummary(
    int SubmittedOrders,
    int FilledOrders,
    double? FillRate,
    decimal? AverageSlippageBps);

public sealed record ShadowPortfolioResult(
    string Id,
    string Name,
    string Note,
    decimal EndingEquity,
    decimal Pnl,
    double PnlPct,
    decimal RealizedPnl,
    decimal UnrealizedPnl,
    int Entries,
    int Exits,
    int OpenPositions,
    int Wins,
    int Losses,
    double? WinRate,
    decimal? DeltaVsActual);
