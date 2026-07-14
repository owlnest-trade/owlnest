using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.Broker;
using TradingBot.Services.Dashboard;
using TradingBot.Services.Discovery;
using TradingBot.Services.Macro;
using TradingBot.Services.News;
using TradingBot.Services.Risk;
using TradingBot.Services.Sentiment;
using TradingBot.Services.State;

namespace TradingBot;

public sealed class TradingWorker : BackgroundService
{
    private readonly TradingOptions _opts;
    private readonly FinnhubOptions _finnhubOpts;
    private readonly DiscoveryOptions _discoveryOpts;
    private readonly INewsProvider _news;
    private readonly FinnhubMarketNewsProvider _marketNews;
    private readonly ITickerExtractor _tickerExtractor;
    private readonly BuzzTracker _buzz;
    private readonly WatchlistManager _watchlist;
    private DateTimeOffset _lastExtractorRun = DateTimeOffset.MinValue;
    private readonly ISentimentAnalyzer _sentiment;
    private readonly IBroker _broker;
    private readonly RiskManager _risk;
    private readonly PositionExitManager _exits;
    private readonly PositionOpenTimeTracker _openTimes;
    private readonly PositionPeakTracker _peaks;
    private readonly ActionableSignalTracker _signalTracker;
    private readonly EarningsCalendar _earnings;
    private readonly EntryOptions _entryOpts;
    private readonly ProcessedNewsStore _seen;
    private readonly DecisionHistoryService _history;
    private readonly MacroStore _macroStore;
    private readonly ILogger<TradingWorker> _log;

    // Per-ticker high-water mark so we only pull genuinely new articles each tick.
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public TradingWorker(
        IOptions<TradingOptions> opts,
        IOptions<FinnhubOptions> finnhubOpts,
        IOptions<DiscoveryOptions> discoveryOpts,
        INewsProvider news,
        FinnhubMarketNewsProvider marketNews,
        ITickerExtractor tickerExtractor,
        BuzzTracker buzz,
        WatchlistManager watchlist,
        ISentimentAnalyzer sentiment,
        IBroker broker,
        RiskManager risk,
        PositionExitManager exits,
        PositionOpenTimeTracker openTimes,
        PositionPeakTracker peaks,
        ActionableSignalTracker signalTracker,
        EarningsCalendar earnings,
        IOptions<EntryOptions> entryOpts,
        ProcessedNewsStore seen,
        DecisionHistoryService history,
        MacroStore macroStore,
        ILogger<TradingWorker> log)
    {
        _opts = opts.Value;
        _finnhubOpts = finnhubOpts.Value;
        _discoveryOpts = discoveryOpts.Value;
        _news = news;
        _marketNews = marketNews;
        _tickerExtractor = tickerExtractor;
        _buzz = buzz;
        _watchlist = watchlist;
        _sentiment = sentiment;
        _broker = broker;
        _risk = risk;
        _exits = exits;
        _openTimes = openTimes;
        _peaks = peaks;
        _signalTracker = signalTracker;
        _earnings = earnings;
        _entryOpts = entryOpts.Value;
        _seen = seen;
        _history = history;
        _macroStore = macroStore;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("=== TradingWorker starting ===");
        _log.LogInformation("Universe: {Universe}", string.Join(", ", _opts.Universe));
        _log.LogInformation("Poll interval: {Interval}s | Min confidence: {Conf:P0} | Max position: {Pos:P0} | Max daily loss: {Loss:P0} | Max trades/day: {Trades}",
            _opts.PollIntervalSeconds, _opts.MinConfidence, _opts.MaxPositionFraction, _opts.MaxDailyLossFraction, _opts.MaxTradesPerDay);
        _log.LogWarning("TRADING ENABLED: {Enabled} — set Trading:TradingEnabled=true in appsettings.json to allow real (paper) orders", _opts.TradingEnabled);

        // Smoke-test the broker connection right away so we fail fast on bad credentials.
        try
        {
            var acct = await _broker.GetAccountAsync(stoppingToken);
            _log.LogInformation("Connected to broker. Equity ${Equity:N2}, cash ${Cash:N2}, buying power ${BP:N2}",
                acct.Equity, acct.Cash, acct.BuyingPower);
            // Seed the dashboard so the header isn't blank until the first tick completes.
            _history.RecordTick(acct, marketOpen: false, tradesToday: 0);
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Broker handshake failed — bot will idle. Fix credentials and restart.");
            return;
        }

        // Initial high-water mark — first poll looks back FinnhubOptions.InitialLookbackMinutes per ticker.
        var lookback = Math.Max(1, _finnhubOpts.InitialLookbackMinutes);
        var initialFloor = DateTimeOffset.UtcNow.AddMinutes(-lookback);
        _log.LogInformation("Initial news lookback: {Minutes} min (since {Since:u})", lookback, initialFloor);
        foreach (var ticker in _opts.Universe)
            _lastSeen[ticker] = initialFloor;

        var poll = TimeSpan.FromSeconds(Math.Max(15, _opts.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _log.LogError(ex, "Tick failed; will retry next interval");
            }

            try { await Task.Delay(poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("=== TradingWorker stopping ===");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var account = await _broker.GetAccountAsync(ct);
        _risk.OnAccountSnapshot(account);

        bool marketOpen = true;
        if (_opts.RegularHoursOnly)
        {
            marketOpen = await _broker.IsMarketOpenAsync(ct);
        }

        _history.RecordTick(account, marketOpen, _risk.TradesToday);

        // Refresh positions cheaply (one round trip) so the dashboard can show them between trades.
        var positions = await _broker.ListPositionsAsync(ct);
        _history.RecordPositions(positions);

        // Update the open-time tracker: stamp new positions, forget closed ones.
        _openTimes.Reconcile(positions, DateTimeOffset.UtcNow);

        // Ratchet per-position peak prices upward (trailing-stop high-water marks).
        _peaks.UpdateFromPositions(positions);

        // Discovery runs even when the market is closed so the watchlist is warm by the next open
        // (buzz that builds Sunday evening matters Monday morning). It only reads news — no orders.
        if (_discoveryOpts.Enabled)
        {
            await RunDiscoveryAsync(ct);
        }

        if (_opts.RegularHoursOnly && !marketOpen)
        {
            _log.LogDebug("Market closed — skipping per-ticker trading (discovery still ran)");
            return;
        }

        // Snapshot in-flight (unfilled) orders so the risk manager can count them toward per-ticker
        // exposure. Without this, multiple bullish articles on the same ticker in a single off-hours
        // window each see the FILLED position as 0 and approve a full-cap buy → 7× concentration.
        var pendingOrders = await _broker.ListRecentOrdersAsync(100, ct);
        var pendingByTicker = pendingOrders
            .Where(o => IsPendingStatus(o.Status))
            .GroupBy(o => o.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (
                    BuyQty:  g.Where(o => o.Side == "Buy" ).Sum(o => Math.Max(0, o.RequestedQuantity - o.FilledQuantity)),
                    SellQty: g.Where(o => o.Side == "Sell").Sum(o => Math.Max(0, o.RequestedQuantity - o.FilledQuantity))
                ),
                StringComparer.OrdinalIgnoreCase);

        // --- Mechanical exits (stop-loss / take-profit / time-based) -----------------------
        // Runs BEFORE the per-ticker news scan so a stop-out closes immediately even if a
        // bearish article hasn't arrived yet.
        await EvaluateExitsAsync(positions, pendingByTicker, ct);

        var effectiveUniverse = BuildEffectiveUniverse(positions);
        foreach (var ticker in effectiveUniverse)
        {
            ct.ThrowIfCancellationRequested();
            var pending = pendingByTicker.TryGetValue(ticker, out var p) ? p : (BuyQty: 0L, SellQty: 0L);
            await ProcessTickerAsync(ticker, account, pending.BuyQty, pending.SellQty, ct);
            // Polite spacing between tickers so we don't sprint past free-tier rate limits.
            await Task.Delay(250, ct);
        }
    }

    private static bool IsPendingStatus(string status) => status switch
    {
        "new" or "accepted" or "pending_new" or "held" or "accepted_for_bidding" or
        "partially_filled" or "pending_replace" or "pending_cancel" => true,
        _ => false
    };

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        IReadOnlyList<MarketNewsItem> articles;
        try
        {
            articles = await _marketNews.GetGeneralNewsAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discovery: market-news fetch failed");
            return;
        }

        // Only spend on the Claude batch extractor at most every ExtractorMinIntervalSeconds.
        // Between calls we still ingest whatever tickers Finnhub already tagged.
        var now = DateTimeOffset.UtcNow;
        var extractorDue = (now - _lastExtractorRun).TotalSeconds >= _discoveryOpts.ExtractorMinIntervalSeconds;
        if (extractorDue && articles.Count > 0)
        {
            articles = await _tickerExtractor.ExtractAsync(articles, ct);
            _lastExtractorRun = now;
        }

        foreach (var a in articles)
            _buzz.Ingest(a);

        _buzz.Prune(now);
        _watchlist.Expire(now);

        var buzzy = _buzz.GetBuzzyTickers();
        _watchlist.PromoteMany(buzzy, _opts.Universe);

        _log.LogInformation("Discovery: {Articles} articles seen → {Buzzy} crossed buzz threshold → {Watchlist} on dynamic watchlist",
            articles.Count, buzzy.Count, _watchlist.ActiveEntries().Count);
    }

    private IReadOnlyList<string> BuildEffectiveUniverse(IReadOnlyList<PositionSnapshot> positions)
    {
        // Combine: fixed universe + dynamic watchlist + currently-held positions.
        // The positions part is critical: a Grok-promoted ticker can drop off the dynamic
        // watchlist after its TTL, but if we still own it we MUST keep watching its news so
        // we can react to bearish catalysts.
        var dyn = _watchlist.ActiveTickers();
        var combined = new List<string>(_opts.Universe.Length + dyn.Count + positions.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _opts.Universe)
            if (seen.Add(t)) combined.Add(t);
        foreach (var t in dyn)
            if (seen.Add(t)) combined.Add(t);
        foreach (var p in positions)
            if (seen.Add(p.Ticker)) combined.Add(p.Ticker);
        return combined;
    }

    /// <summary>
    /// Run mechanical exits and submit any sells. Skips tickers that already have a pending
    /// sell of >= the exit quantity (don't double-sell).
    /// </summary>
    private async Task EvaluateExitsAsync(
        IReadOnlyList<PositionSnapshot> positions,
        IReadOnlyDictionary<string, (long BuyQty, long SellQty)> pendingByTicker,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = _exits.EvaluateExits(positions, now);
        if (candidates.Count == 0) return;

        foreach (var exit in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var pendingSell = pendingByTicker.TryGetValue(exit.Ticker, out var p) ? p.SellQty : 0L;
            if (pendingSell >= exit.Quantity)
            {
                _log.LogDebug("[{Ticker}] exit suppressed — pending sell {Pending} already covers {Qty}",
                    exit.Ticker, pendingSell, exit.Quantity);
                continue;
            }

            var qtyToSell = exit.Quantity - pendingSell;
            _log.LogInformation("[{Ticker}] EXIT {Trigger} — selling {Qty}: {Reason}",
                exit.Ticker, exit.Trigger, qtyToSell, exit.Reason);

            var orderId = await _broker.SubmitMarketOrderAsync(exit.Ticker, TradeSide.Sell, qtyToSell, ct);
            if (orderId is not null)
            {
                _risk.RecordOrderSubmitted();
                _openTimes.Forget(exit.Ticker);   // fresh start if we ever re-enter
                _peaks.Forget(exit.Ticker);       // reset peak so re-entry starts a new high-water mark
                _log.LogInformation("[{Ticker}] Exit order submitted: {OrderId}", exit.Ticker, orderId);
            }
            else
            {
                _log.LogWarning("[{Ticker}] Exit order failed to submit", exit.Ticker);
            }
        }
    }

    private async Task ProcessTickerAsync(string ticker, AccountSnapshot account, long pendingBuyQty, long pendingSellQty, CancellationToken ct)
    {
        var since = _lastSeen.TryGetValue(ticker, out var ts)
            ? ts
            : DateTimeOffset.UtcNow.AddMinutes(-60);

        IReadOnlyList<NewsItem> news;
        try
        {
            news = await _news.GetRecentNewsAsync(ticker, since, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "News fetch failed for {Ticker}", ticker);
            return;
        }

        if (news.Count == 0) return;

        // Advance the high-water mark even if every article was already seen.
        var newestPublished = news.Max(n => n.PublishedAt);
        if (newestPublished > since) _lastSeen[ticker] = newestPublished;

        foreach (var item in news.OrderBy(n => n.PublishedAt))
        {
            if (!_seen.TryMarkProcessed(item.Id))
                continue; // already evaluated

            _log.LogInformation("[{Ticker}] New article {PubAt:HH:mm:ssZ} ({Source}): {Headline}",
                ticker, item.PublishedAt, item.Source, Truncate(item.Headline, 120));

            // Read the latest macro snapshot RIGHT before the Claude call so we pick up data
            // even if Manifold finished polling mid-tick. Reads are atomic on the singleton store.
            var macroSnap = _macroStore.Latest;
            var macroPreamble = MacroSummarizer.BuildPreamble(macroSnap);
            var macroShortSummary = MacroSummarizer.BuildShortSummary(macroSnap);

            var sentiment = await _sentiment.AnalyzeAsync(item, macroPreamble, ct);
            if (sentiment is null)
            {
                _log.LogWarning("[{Ticker}] Sentiment unavailable — skipping article {Id}", ticker, item.Id);
                _history.Record(new DecisionRecord(
                    At: DateTimeOffset.UtcNow,
                    Ticker: ticker,
                    Source: item.Source,
                    Headline: item.Headline,
                    Url: item.Url,
                    PublishedAt: item.PublishedAt,
                    Sentiment: null, Confidence: null, Actionable: null, Reasoning: null,
                    Outcome: DecisionOutcome.SentimentSkipped,
                    OutcomeReason: "Claude analyzer returned null (network/parse/rate-limit)",
                    Side: null, Quantity: null, OrderId: null,
                    MacroSummary: macroShortSummary));
                continue;
            }

            _log.LogInformation("[{Ticker}] Sentiment: {Sentiment} conf={Conf:P0} actionable={Actionable} — {Reason}",
                ticker, sentiment.Sentiment, sentiment.Confidence, sentiment.IsActionable, sentiment.Reasoning);

            // Pull price + position only when sentiment looks interesting; saves API quota otherwise.
            if (!sentiment.IsActionable || sentiment.Confidence < _opts.MinConfidence || sentiment.Sentiment == TradingBot.Models.Sentiment.Neutral)
            {
                _log.LogInformation("[{Ticker}] No-trade (sentiment gate)", ticker);
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.NoTradeGate,
                    $"Sentiment did not clear actionable+confidence gate ({sentiment.Sentiment}, {sentiment.Confidence:P0}, actionable={sentiment.IsActionable})"));
                continue;
            }

            // --- Gate: earnings blackout (BUYS only — don't block exits) -------------------
            var nowUtc = DateTimeOffset.UtcNow;
            if (_entryOpts.EarningsBlackoutEnabled
                && sentiment.Sentiment == TradingBot.Models.Sentiment.Bullish
                && _earnings.HasUpcomingEarnings(ticker, nowUtc, _entryOpts.EarningsBlackoutHours))
            {
                var earningsAt = _earnings.NextEarnings(ticker);
                var reason = $"Earnings blackout — next earnings {earningsAt:yyyy-MM-dd HH:mm}Z (within {_entryOpts.EarningsBlackoutHours}h gap-risk window)";
                _log.LogInformation("[{Ticker}] REJECTED Buy — {Reason}", ticker, reason);
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.Rejected, reason));
                continue;
            }

            // --- Gate: signal confirmation (need 2+ same-direction actionable in window) ---
            var direction = sentiment.Sentiment.ToString();
            var signalCount = _signalTracker.RecordAndCount(ticker, direction, nowUtc);
            if (_entryOpts.ConfirmationRequired && signalCount < _entryOpts.RequiredSignalCount)
            {
                var reason = $"Awaiting confirmation: {signalCount}/{_entryOpts.RequiredSignalCount} {direction} signals in {_entryOpts.ConfirmationWindowMinutes}m window";
                _log.LogInformation("[{Ticker}] NO-TRADE — {Reason}", ticker, reason);
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.NoTradeGate, reason));
                continue;
            }

            var price = await _broker.GetLatestPriceAsync(ticker, ct);
            var position = await _broker.GetPositionAsync(ticker, ct);
            var pendingBuyNotional = pendingBuyQty * (price ?? 0m);
            var decision = _risk.Evaluate(item, sentiment, account, position, price ?? 0m,
                pendingBuyNotional: pendingBuyNotional,
                pendingSellQty: pendingSellQty);

            if (!decision.Approved)
            {
                _log.LogInformation("[{Ticker}] REJECTED {Side} — {Reason}", ticker, decision.Side, decision.Reason);
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.Rejected, decision.Reason,
                    side: decision.Side.ToString(), qty: decision.Quantity));
                continue;
            }

            _log.LogInformation("[{Ticker}] APPROVED {Side} {Qty} @ ~{Price:C} — {Reason}",
                ticker, decision.Side, decision.Quantity, price, decision.Reason);

            var orderId = await _broker.SubmitMarketOrderAsync(ticker, decision.Side, decision.Quantity, ct);
            if (orderId is not null)
            {
                _risk.RecordOrderSubmitted();
                // Stamp the open-time for this ticker so time-based exits use the right anchor.
                // (For SELL orders the tracker naturally drops the entry once the position closes.)
                if (decision.Side == TradeSide.Buy)
                    _openTimes.RecordOpen(ticker, DateTimeOffset.UtcNow);
                _log.LogInformation("[{Ticker}] Order submitted: {OrderId}", ticker, orderId);
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.Submitted, $"Order accepted by broker; {decision.Reason}",
                    side: decision.Side.ToString(), qty: decision.Quantity, orderId: orderId));
            }
            else
            {
                _history.Record(BuildRecord(ticker, item, sentiment, macroShortSummary,
                    DecisionOutcome.Approved, $"Risk approved but broker submit failed; {decision.Reason}",
                    side: decision.Side.ToString(), qty: decision.Quantity));
            }
        }
    }

    private static DecisionRecord BuildRecord(
        string ticker,
        NewsItem item,
        SentimentResult sentiment,
        string? macroShortSummary,
        DecisionOutcome outcome,
        string outcomeReason,
        string? side = null,
        int? qty = null,
        string? orderId = null) =>
        new(
            At: DateTimeOffset.UtcNow,
            Ticker: ticker,
            Source: item.Source,
            Headline: item.Headline,
            Url: item.Url,
            PublishedAt: item.PublishedAt,
            Sentiment: sentiment.Sentiment.ToString(),
            Confidence: sentiment.Confidence,
            Actionable: sentiment.IsActionable,
            Reasoning: sentiment.Reasoning,
            Outcome: outcome,
            OutcomeReason: outcomeReason,
            Side: side,
            Quantity: qty,
            OrderId: orderId,
            MacroSummary: macroShortSummary);

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
