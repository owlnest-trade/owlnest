using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alpaca.Markets;
using Microsoft.EntityFrameworkCore;
using TradingBot.Web.Data;
using TradingBot.Web.Services.Shared;
using TradingBot.Web.Services.UserBot;

namespace TradingBot.Web.Services;

/// <summary>
/// One user's bot. Pulls news per ticker (Finnhub + SEC), asks the LLM for sentiment with optional
/// macro context, runs through risk gates (confirmation + earnings blackout + position cap +
/// daily-loss kill), places real (paper) orders via Alpaca, evaluates mechanical exits each tick,
/// and persists positions/orders/decisions/equity snapshots into the DB for the dashboard.
///
/// Additionally runs per-user discovery (Finnhub firehose + optional Gemini extractor + optional
/// Grok trending) on its own cadence to surface dynamic watchlist tickers beyond the fixed universe.
///
/// Everything scoped to this user — keys, settings, broker client, state. Multiple users run
/// simultaneously via the host map in UserBotHost.
/// </summary>
internal sealed class UserBotInstance : IAsyncDisposable
{
    private readonly string _userId;
    private readonly UserSettings _settings;
    private readonly string _finnhubKey;
    private readonly string _llmProvider;
    private readonly string? _geminiKey;
    private readonly string? _anthropicKey;
    private readonly string? _grokKey;
    private readonly string? _llamaKey;
    private readonly string _geminiModel;
    private readonly string _anthropicModel;
    private readonly string _llamaModel;
    private readonly IServiceProvider _rootSp;
    private readonly ILogger _log;
    private readonly SecFilingsFeed _sec;
    private readonly ManifoldFeed _macro;
    private readonly FedFeed _fed;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    private readonly HttpClient _finnhub = new();
    private readonly HttpClient _llmHttp = new();
    private readonly HttpClient _grokHttp = new();
    /// <summary>
    /// Optional second HttpClient pre-configured for Groq/Llama. Created ONLY when the primary
    /// provider is Gemini AND a Llama server key is available — used as a fallback when Gemini's
    /// safety filter refuses to classify a headline. Null otherwise.
    /// </summary>
    private readonly HttpClient? _llamaFallbackHttp;
    /// <summary>
    /// Dedicated HttpClient for the Claude verification gate. We can't share <see cref="_llmHttp"/>
    /// because its BaseAddress is pinned to whichever sentiment provider the user picked (Gemini
    /// by default → google.com), and Claude calls need api.anthropic.com. Sharing previously
    /// caused every Claude verification call to 404 in ~89ms.
    /// </summary>
    private readonly HttpClient? _claudeHttp;
    private readonly IAlpacaTradingClient _trading;
    private readonly IAlpacaDataClient _data;
    /// <summary>Alpaca's crypto data client lives on a different endpoint from equity data.
    /// Only created when the tier allows crypto trading.</summary>
    private readonly IAlpacaCryptoDataClient? _cryptoData;

    // Per-user helpers (lazy)
    private UserEarningsCalendar? _earnings;
    private UserSignalTracker? _signals;
    private UserBuzzTracker? _buzz;
    private UserWatchlist? _watchlist;
    private UserFinnhubFirehose? _firehose;
    private UserGeminiExtractor? _extractor;
    private UserGrokTrending? _grok;
    private UserGrokConfirmation? _grokConfirm;
    private UserClaudeVerification? _claudeVerify;
    private UserInsiderFeed? _insider;
    private UserGoogleNewsFeed? _gnews;
    /// <summary>Per-coin Google News feed (BTC/USD → "Bitcoin").</summary>
    private UserCryptoNewsFeed? _cryptoNews;

    // Per-user in-memory state
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ingestedNewsIds = new(StringComparer.OrdinalIgnoreCase);
    private int _tradesToday;
    private DateOnly _tradeCounterDay;
    private decimal _sessionStartEquity;
    private DateTimeOffset _botStartedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastDiscoveryRun = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGrokRun = DateTimeOffset.MinValue;

    // Snapshot for dashboard
    public IReadOnlyList<WatchEntry> Watchlist => _watchlist?.ActiveEntries() ?? Array.Empty<WatchEntry>();
    public IReadOnlyList<TrendingTicker> LastTrending { get; private set; } = Array.Empty<TrendingTicker>();
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public string LastStatusLine { get; private set; } = "Starting…";
    /// <summary>True if this instance was started against Alpaca's LIVE environment (real money).</summary>
    public bool IsLiveMode { get; }

    public UserBotInstance(
        string userId,
        UserSettings settings,
        string alpacaKey, string alpacaSecret, bool alpacaPaper,
        string finnhubKey,
        string llmProvider,
        string? geminiKey, string? anthropicKey, string? grokKey, string? llamaKey,
        string geminiModel, string anthropicModel, string llamaModel,
        SecFilingsFeed sec,
        ManifoldFeed macro,
        FedFeed fed,
        IServiceProvider rootSp,
        ILogger log)
    {
        _userId = userId;
        _settings = settings;
        _finnhubKey = finnhubKey;
        _llmProvider = llmProvider;
        _geminiKey = geminiKey;
        _anthropicKey = anthropicKey;
        _grokKey = grokKey;
        _llamaKey = llamaKey;
        _geminiModel = geminiModel;
        _anthropicModel = anthropicModel;
        _llamaModel = llamaModel;
        _sec = sec;
        _macro = macro;
        _fed = fed;
        _rootSp = rootSp;
        _log = log;
        _tradeCounterDay = DateOnly.FromDateTime(DateTime.UtcNow);

        _finnhub.BaseAddress = new Uri("https://finnhub.io/api/v1/");
        _finnhub.Timeout = TimeSpan.FromSeconds(15);

        // _llmHttp is reused for every sentiment call, so we pin its BaseAddress + auth headers
        // ONCE at construction based on the chosen provider. NOTE: this means switching providers
        // requires restarting the bot — UserSettings changes take effect at the next Start.
        if (_llmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            _llmHttp.BaseAddress = new Uri("https://api.anthropic.com/");
            _llmHttp.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(_anthropicKey))
            {
                _llmHttp.DefaultRequestHeaders.Add("x-api-key", _anthropicKey);
                _llmHttp.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
        }
        else if (_llmProvider.Equals("Llama", StringComparison.OrdinalIgnoreCase))
        {
            // Groq Cloud — OpenAI-compatible chat/completions endpoint at /openai/v1/chat/completions.
            // ~$0.05/M input + $0.08/M output for llama-3.1-8b-instant (5× cheaper than Gemini Flash).
            // No financial-content safety filter — won't refuse crypto pump/dump headlines.
            _llmHttp.BaseAddress = new Uri("https://api.groq.com/");
            _llmHttp.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(_llamaKey))
                _llmHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _llamaKey);
        }
        else
        {
            _llmHttp.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            _llmHttp.Timeout = TimeSpan.FromSeconds(30);

            // Auto-wired Llama fallback: when running on Gemini, set up a second HttpClient pointed
            // at Groq so we can retry safety-blocked headlines with an open-weights model. Costs
            // ~$0.00003 per retry (negligible). If no Llama key is configured the field stays null
            // and AnalyzeAsync just surfaces the original block reason like before.
            if (!string.IsNullOrWhiteSpace(_llamaKey))
            {
                _llamaFallbackHttp = new HttpClient
                {
                    BaseAddress = new Uri("https://api.groq.com/"),
                    Timeout = TimeSpan.FromSeconds(30),
                };
                _llamaFallbackHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _llamaKey);
                _llamaFallbackHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
        }
        _llmHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _grokHttp.BaseAddress = new Uri("https://api.x.ai/");
        _grokHttp.Timeout = TimeSpan.FromSeconds(60);
        _grokHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var creds = new SecretKey(alpacaKey, alpacaSecret);
        // Pick the Alpaca environment based on the user's choice. Paper is the default; Live
        // requires the user to have gone through the extra friction on the Keys page (consent +
        // typing LIVE), enforced in KeysModel.OnPostAsync.
        var env = alpacaPaper ? Alpaca.Markets.Environments.Paper : Alpaca.Markets.Environments.Live;
        IsLiveMode = !alpacaPaper;
        _trading = env.GetAlpacaTradingClient(creds);
        _data = env.GetAlpacaDataClient(creds);
        // Crypto market data lives on its own endpoint. Initialize unconditionally — the cost is
        // a tiny constant-time HttpClient init; we'll branch on tier when actually using it.
        _cryptoData = env.GetAlpacaCryptoDataClient(creds);

        // Per-user helpers wired now that we have the keys + settings.
        _earnings = new UserEarningsCalendar(_finnhub, _finnhubKey, _log);
        _signals = new UserSignalTracker(_settings.ConfirmationWindowMinutes);

        // ── Tier-gated features ──
        var tier = TierPolicy.Normalize(_settings.Tier);

        var discoveryAllowed = TierPolicy.AllowsDiscovery(tier) && _settings.DiscoveryEnabled;
        if (discoveryAllowed)
        {
            var maxDyn = TierPolicy.MaxDynamicTickers(tier);
            _buzz = new UserBuzzTracker(_settings.DiscoveryBuzzWindowMinutes, _settings.DiscoveryBuzzThreshold);
            _watchlist = new UserWatchlist(_settings.DiscoveryWatchlistTtlHours, Math.Max(5, maxDyn));
            _firehose = new UserFinnhubFirehose(_finnhub, _finnhubKey, _log);
            if (!string.IsNullOrWhiteSpace(_geminiKey))
                _extractor = new UserGeminiExtractor(_llmHttp, _geminiKey!, _geminiModel, _log);
        }
        var grokTrendingAllowed = TierPolicy.AllowsGrokTrending(tier) && _settings.UseGrokTrending && !string.IsNullOrWhiteSpace(_grokKey);
        if (grokTrendingAllowed)
        {
            _grok = new UserGrokTrending(_grokHttp, _grokKey!, _log);
            _watchlist ??= new UserWatchlist(_settings.DiscoveryWatchlistTtlHours, Math.Max(5, TierPolicy.MaxDynamicTickers(tier)));
        }
        var grokConfirmAllowed = TierPolicy.AllowsGrokConfirmation(tier) && _settings.GrokConfirmationEnabled && !string.IsNullOrWhiteSpace(_grokKey);
        if (grokConfirmAllowed)
        {
            _grokConfirm = new UserGrokConfirmation(_grokHttp, _grokKey!, _log);
        }

        // Claude verification gate — Pro tier, server's Anthropic key, user toggle.
        // When BOTH Grok and Claude are enabled, both must approve before the buy fires.
        var claudeVerifyAllowed = TierPolicy.AllowsClaudeConfirmation(tier)
                                  && _settings.ClaudeConfirmationEnabled
                                  && !string.IsNullOrWhiteSpace(_anthropicKey);
        if (claudeVerifyAllowed)
        {
            // Dedicated Anthropic HttpClient — must NOT share _llmHttp because that's pinned to
            // the sentiment-provider base URL (Google when LlmProvider=Gemini). Sharing made
            // every Claude call hit generativelanguage.googleapis.com/v1/messages → 404.
            _claudeHttp = new HttpClient
            {
                BaseAddress = new Uri("https://api.anthropic.com/"),
                Timeout = TimeSpan.FromSeconds(60),
            };
            _claudeHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _claudeVerify = new UserClaudeVerification(_claudeHttp, _anthropicKey!, _anthropicModel, _log);
        }

        // Additional per-ticker news producers (no tier restriction; Finnhub key is server-provided)
        if (_settings.UseInsiderTransactions)
            _insider = new UserInsiderFeed(_finnhub, _finnhubKey, _log);
        if (_settings.UseGoogleNews)
            _gnews = new UserGoogleNewsFeed(_log);

        // Crypto news feed. Uses Google News with translated coin keywords
        // (BTC/USD → "Bitcoin") since crypto has no Finnhub/SEC/Form-4 analogue.
        if (TierPolicy.AllowsCrypto(tier))
            _cryptoNews = new UserCryptoNewsFeed(_log);
    }

    public void Start() => _runTask = Task.Run(() => LoopAsync(_cts.Token));

    // ════════════════════════════════════════════════════════════════════════
    //  Main loop
    // ════════════════════════════════════════════════════════════════════════
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            var acct = await _trading.GetAccountAsync(ct);
            _sessionStartEquity = acct.LastEquity > 0 ? acct.LastEquity : (acct.Equity ?? 100_000m);
            await SnapshotEquityAsync(acct, ct);
            LastStatusLine = $"Connected. Equity ${acct.Equity:N2}";
        }
        catch (Exception ex)
        {
            LastStatusLine = "Broker handshake failed: " + ex.Message;
            _log.LogError(ex, "User {U} broker handshake failed", _userId);
            return;
        }

        var initialFloor = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(15, _settings.FinnhubLookbackMinutes));
        var tier = TierPolicy.Normalize(_settings.Tier);
        var allowedUniverse = TierPolicy.FilterUniverse(tier, _settings.Universe());
        foreach (var t in allowedUniverse) _lastSeen[t] = initialFloor;

        // Tier floors the poll interval — Free can't poll faster than 1h, Plus 10m, Pro 1m.
        var requested = Math.Max(15, _settings.PollIntervalSeconds);
        var floor = TierPolicy.MinPollIntervalSeconds(tier);
        var interval = TimeSpan.FromSeconds(Math.Max(requested, floor));
        var lastEquitySnapshot = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastStatusLine = "Tick failed: " + ex.Message;
                _log.LogWarning(ex, "User {U} tick failed", _userId);
            }

            // Hourly equity snapshot for the P&L chart.
            if (DateTimeOffset.UtcNow - lastEquitySnapshot > TimeSpan.FromMinutes(60))
            {
                try
                {
                    var a = await _trading.GetAccountAsync(ct);
                    await SnapshotEquityAsync(a, ct);
                    lastEquitySnapshot = DateTimeOffset.UtcNow;
                }
                catch { /* ignored */ }
            }

            try { await Task.Delay(interval, ct); } catch (OperationCanceledException) { break; }
        }
        LastStatusLine = "Stopped.";
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 1. Fresh account + clock snapshot
        var acct = await _trading.GetAccountAsync(ct);
        var clock = await _trading.GetClockAsync(ct);

        // Daily counter reset
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _tradeCounterDay)
        {
            _tradeCounterDay = today;
            _tradesToday = 0;
            _sessionStartEquity = acct.LastEquity > 0 ? acct.LastEquity : (acct.Equity ?? 100_000m);
        }

        // 2. Refresh earnings cache if stale (no-op if <6h since last)
        if (_settings.EarningsBlackoutEnabled && _earnings is not null)
            await _earnings.RefreshIfStaleAsync(_settings.Universe(), ct);

        // 3. Discovery refresh (firehose → buzz → watchlist, Grok → watchlist)
        await RunDiscoveryIfDueAsync(ct);

        // 4. Refresh positions + orders into DB
        var positions = await _trading.ListPositionsAsync(ct);
        await SyncPositionsAsync(positions, ct);

        var orders = await _trading.ListOrdersAsync(new ListOrdersRequest
        {
            OrderStatusFilter = OrderStatusFilter.All,
            LimitOrderNumber = 100,
            OrderListSorting = SortDirection.Descending
        }, ct);
        await SyncOrdersAsync(orders, ct);

        var pendingByTicker = orders
            .Where(o => IsPendingStatus(o.OrderStatus))
            .GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (
                    BuyQty:  g.Where(o => o.OrderSide == OrderSide.Buy ).Sum(o => Math.Max(0L, (long)o.IntegerQuantity - (long)o.IntegerFilledQuantity)),
                    SellQty: g.Where(o => o.OrderSide == OrderSide.Sell).Sum(o => Math.Max(0L, (long)o.IntegerQuantity - (long)o.IntegerFilledQuantity))
                ),
                StringComparer.OrdinalIgnoreCase);

        // 5. Daily loss kill switch
        var dayDrawdown = _sessionStartEquity > 0
            ? (_sessionStartEquity - (acct.Equity ?? 0m)) / _sessionStartEquity
            : 0m;
        var dailyLossTripped = dayDrawdown >= (decimal)_settings.MaxDailyLossFraction;

        // 6. Mechanical exits FIRST
        await EvaluateExitsAsync(positions, pendingByTicker, ct);

        if (dailyLossTripped) { LastStatusLine = $"Daily loss cap hit ({dayDrawdown:P2}) — paused. Exits still active."; return; }
        if (!_settings.TradingEnabled) { LastStatusLine = $"Trading disabled. Equity ${acct.Equity:N2}"; return; }

        // 7. Effective scan universe = tier-filtered fixed + held positions + dynamic watchlist.
        // RegularHoursOnly only blocks EQUITY scans — crypto trades 24/7 so it stays enabled.
        var tier = TierPolicy.Normalize(_settings.Tier);
        var equityHaltedByHours = _settings.RegularHoursOnly && !clock.IsOpen;

        var tieredFixed = equityHaltedByHours
            ? new List<string>()
            : TierPolicy.FilterUniverse(tier, _settings.Universe()).ToList();
        // Held positions: include EQUITY only when the equity market is open; held CRYPTO always.
        var heldEquity = positions.Where(p => !TierPolicy.IsCryptoTicker(p.Symbol)).Select(p => p.Symbol);
        var heldCrypto = positions.Where(p =>  TierPolicy.IsCryptoTicker(p.Symbol)).Select(p => p.Symbol);
        var heldTickers = (equityHaltedByHours ? heldCrypto : heldEquity.Concat(heldCrypto)).ToList();
        var dyn = (equityHaltedByHours
                    ? Array.Empty<string>()
                    : (_watchlist?.ActiveTickers() ?? Array.Empty<string>()))
            .Where(t => TierPolicy.AllowsCommodities(tier) || !TierPolicy.IsCommodityTicker(t))
            .Take(TierPolicy.MaxDynamicTickers(tier))
            .ToList();
        var cryptoFixed = TierPolicy.FilterCryptoUniverse(tier, _settings.CryptoUniverse()).ToList();

        var effective = tieredFixed.Concat(heldTickers).Concat(dyn).Concat(cryptoFixed)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (effective.Count == 0)
        {
            // No work this tick — give the user a helpful status line based on why.
            LastStatusLine = equityHaltedByHours
                ? $"Market closed (no crypto enabled). Equity ${acct.Equity:N2}"
                : $"No tickers in universe. Equity ${acct.Equity:N2}";
            return;
        }

        // Personal rule: skip any ticker on the user's blacklist before doing any work
        var blacklist = new HashSet<string>(_settings.Blacklist(), StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in effective)
        {
            ct.ThrowIfCancellationRequested();
            if (_tradesToday >= _settings.MaxTradesPerDay) break;
            if (blacklist.Contains(ticker)) continue;   // user said never trade this

            var pending = pendingByTicker.TryGetValue(ticker, out var p) ? p : (BuyQty: 0L, SellQty: 0L);
            if (TierPolicy.IsCryptoTicker(ticker))
                await ProcessCryptoTickerAsync(ticker, acct, positions, pending, ct);
            else
                await ProcessTickerAsync(ticker, acct, positions, pending, clock, ct);
            await Task.Delay(250, ct);
        }

        var hoursNote = equityHaltedByHours ? " (crypto-only — equity market closed)" : "";
        LastStatusLine = $"Tick OK{hoursNote}. Equity ${acct.Equity:N2}, {_tradesToday}/{_settings.MaxTradesPerDay} trades today, {positions.Count} positions, {dyn.Count} on watchlist";
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Per-ticker: pull news (Finnhub + SEC) → sentiment → gates → trade
    // ════════════════════════════════════════════════════════════════════════
    private async Task ProcessTickerAsync(
        string ticker,
        IAccount acct,
        IReadOnlyList<IPosition> positions,
        (long BuyQty, long SellQty) pending,
        IClock clock,
        CancellationToken ct)
    {
        var since = _lastSeen.TryGetValue(ticker, out var ts)
            ? ts
            : DateTimeOffset.UtcNow.AddMinutes(-_settings.FinnhubLookbackMinutes);

        // Pull all sources in parallel — Finnhub + SEC + Insider + GoogleNews
        var finnhubT = FetchFinnhubAsync(ticker, since, ct);
        var secT = _settings.UseSecEdgar
            ? _sec.GetFilingsAsync(ticker, since,
                _settings.SecEdgarContactEmail ?? "",
                _settings.SecEdgarForm8K, _settings.SecEdgarForm10Q, _settings.SecEdgarForm10K, ct)
            : Task.FromResult<IReadOnlyList<SecFiling>>(Array.Empty<SecFiling>());
        var insiderT = _insider is not null
            ? _insider.GetAsync(ticker, since, ct)
            : Task.FromResult<IReadOnlyList<InsiderTxn>>(Array.Empty<InsiderTxn>());
        var gnewsT = _gnews is not null
            ? _gnews.GetAsync(ticker, since, ct)
            : Task.FromResult<IReadOnlyList<RssArticle>>(Array.Empty<RssArticle>());
        await Task.WhenAll(finnhubT, secT, insiderT, gnewsT);

        var finnhubArticles = finnhubT.Result;
        var secFilings = secT.Result.Select(s => new Article(
            Id: s.Id, Source: "SEC EDGAR",
            Headline: s.Headline, Summary: s.Summary, Url: s.Url,
            PublishedAt: s.AcceptedAt));
        var insiderArticles = insiderT.Result.Select(i =>
        {
            var (h, s) = UserInsiderFeed.FormatHeadline(i);
            return new Article(
                Id: i.Id, Source: "SEC Form 4",
                Headline: h, Summary: s,
                Url: $"https://www.sec.gov/cgi-bin/browse-edgar?action=getcompany&CIK={i.Ticker}&type=4",
                PublishedAt: i.At);
        });
        var gnewsArticles = gnewsT.Result.Select(g => new Article(
            Id: g.Id, Source: "Google News: " + g.Source,
            Headline: g.Headline, Summary: g.Summary, Url: g.Url,
            PublishedAt: g.PublishedAt));

        var articles = finnhubArticles
            .Concat(secFilings)
            .Concat(insiderArticles)
            .Concat(gnewsArticles)
            .OrderBy(a => a.PublishedAt).ToList();
        if (articles.Count == 0) return;

        var newest = articles.Max(a => a.PublishedAt);
        if (newest > since) _lastSeen[ticker] = newest;

        // Pre-compute personal-rule sets (cheap, do once per ticker)
        var blockedWords = _settings.BlockedKeywords();
        var boostWords = _settings.BoostKeywords();

        foreach (var a in articles)
        {
            if (!_ingestedNewsIds.Add(a.Id)) continue;

            // One price fetch per article — used as the priceUsd snapshot on every UserDecision
            // row written below, regardless of which gate the article exits through. Fetched here
            // (after dedup, before the gates) so dups don't waste a market data call.
            var priceSnapshot = await GetLatestPriceAsync(ticker, ct);

            // ── Personal rule: block headlines containing any user-blocked keyword ──
            if (blockedWords.Length > 0)
            {
                var lowerHead = a.Headline.ToLowerInvariant();
                var hitWord = blockedWords.FirstOrDefault(w => !string.IsNullOrEmpty(w) && lowerHead.Contains(w));
                if (hitWord is not null)
                {
                    await using var scopeBlk = _rootSp.CreateAsyncScope();
                    var dbBlk = scopeBlk.ServiceProvider.GetRequiredService<OwlNestDbContext>();
                    dbBlk.UserDecisions.Add(MakeDecision(ticker, a, null, "NoTradeGate",
                        $"Headline contains blocked keyword '{hitWord}'", priceUsd: priceSnapshot));
                    await dbBlk.SaveChangesAsync(ct);
                    continue;
                }
            }

            var (verdict, failure) = await AnalyzeAsync(ticker, a.Headline, a.Summary, ct);
            await using var scope = _rootSp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();

            if (verdict is null)
            {
                // Failure reason comes from AnalyzeAsync — surfaces HTTP code, safety block, parse
                // error, timeout, etc. into the user-visible decision row instead of the old
                // generic stand-in.
                db.UserDecisions.Add(MakeDecision(ticker, a, null, "SentimentSkipped",
                    failure ?? "LLM call failed", priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // ── Personal rule: boost confidence if headline matches user-boost keyword ──
            var effectiveConfidence = verdict.Confidence;
            if (boostWords.Length > 0)
            {
                var lowerHead = a.Headline.ToLowerInvariant();
                if (boostWords.Any(w => !string.IsNullOrEmpty(w) && lowerHead.Contains(w)))
                    effectiveConfidence = Math.Min(1.0, effectiveConfidence + 0.05);
            }

            // ── Gate 1: sentiment direction + actionable + (boosted) confidence ──
            var bullishOK = verdict.Sentiment.Equals("bullish", StringComparison.OrdinalIgnoreCase)
                && verdict.Actionable
                && effectiveConfidence >= _settings.MinConfidence;
            var bearishOK = _settings.BearishNewsExitsEnabled
                && verdict.Sentiment.Equals("bearish", StringComparison.OrdinalIgnoreCase)
                && verdict.Actionable
                && verdict.Confidence >= _settings.BearishNewsMinConfidence;

            if (!bullishOK && !bearishOK)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
                    $"Sentiment gate ({verdict.Sentiment}, {verdict.Confidence:P0}, actionable={verdict.Actionable})",
                    priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // ── Gate 2 (bullish only): confirmation window — require N matching signals ──
            if (bullishOK && _settings.RequiredSignalCount > 1 && _signals is not null)
            {
                var count = _signals.RecordAndCount(ticker, "bullish", DateTimeOffset.UtcNow);
                if (count < _settings.RequiredSignalCount)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
                        $"Confirmation gate: {count}/{_settings.RequiredSignalCount} bullish signals in last {_settings.ConfirmationWindowMinutes}m",
                        priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            // ── Bearish: exit existing position if held ──
            if (bearishOK)
            {
                var heldPos = positions.FirstOrDefault(x => x.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase));
                if (heldPos is null || heldPos.IntegerQuantity <= 0)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                        "Bearish signal but no long position to close (no shorting)",
                        priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                var sellableQty = (long)heldPos.IntegerQuantity - pending.SellQty;
                if (sellableQty <= 0)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                        $"Bearish but already-pending sells cover the position",
                        priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                var ok = await SubmitOrderAsync(db, ticker, OrderSide.Sell, sellableQty,
                    "Bearish news exit: " + verdict.Reasoning, ct, priceAtSubmit: priceSnapshot);
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict,
                    ok ? "Submitted" : "Rejected",
                    ok ? "Bearish news exit submitted" : "Broker rejected the sell",
                    side: "Sell", qty: (int)sellableQty, priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                if (ok) _tradesToday++;
                continue;
            }

            // ── Gate 3: earnings blackout ──
            if (_settings.EarningsBlackoutEnabled && _earnings is not null
                && _earnings.HasUpcomingEarnings(ticker, DateTimeOffset.UtcNow, _settings.EarningsBlackoutHours))
            {
                var next = _earnings.NextEarnings(ticker);
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                    $"Earnings blackout: next earnings {next:u} (±{_settings.EarningsBlackoutHours}h)",
                    priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // ── Personal rule: skip if inside the user's no-trade window around open/close ──
            if (_settings.NoTradeMinutesAfterOpen > 0 || _settings.NoTradeMinutesBeforeClose > 0)
            {
                var now = DateTimeOffset.UtcNow;
                if (clock.NextOpenUtc > clock.NextCloseUtc)
                {
                    // Market is currently open (next close < next open)
                    var sinceOpen = (now - (clock.NextCloseUtc - TimeSpan.FromHours(6.5))).TotalMinutes;
                    var untilClose = (clock.NextCloseUtc - now).TotalMinutes;
                    if (_settings.NoTradeMinutesAfterOpen > 0 && sinceOpen < _settings.NoTradeMinutesAfterOpen)
                    {
                        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                            $"Inside no-trade window: {sinceOpen:F0}m since open (require ≥{_settings.NoTradeMinutesAfterOpen}m)",
                            priceUsd: priceSnapshot));
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                    if (_settings.NoTradeMinutesBeforeClose > 0 && untilClose < _settings.NoTradeMinutesBeforeClose)
                    {
                        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                            $"Inside no-trade window: {untilClose:F0}m until close (require ≥{_settings.NoTradeMinutesBeforeClose}m)",
                            priceUsd: priceSnapshot));
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }
            }

            // ── Gate 4: Grok second-opinion (live X + web search, ~$0.01, ~5-10s) ──
            // Conservative: only "approve" lets the buy through; anything else skips.
            // Every call (success or failure) is persisted to UserGateCalls for audit.
            if (_grokConfirm is not null)
            {
                GrokConfirmation gc;
                try
                {
                    gc = await _grokConfirm.CheckAsync(ticker, a.Headline, a.Source,
                        verdict.Confidence, verdict.Reasoning, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    gc = new GrokConfirmation(GrokVerdict.Error, "Grok exception: " + ex.Message,
                        "grok-3-mini", "", "", 0);
                }
                await SaveGateCallAsync("Grok", gc.ModelName, ticker, a, gc.Verdict.ToString(),
                    gc.Reason, gc.Prompt, gc.RawResponse, gc.LatencyMs, ct);

                if (gc.Verdict != GrokVerdict.Approve)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                        $"Grok {gc.Verdict}: {gc.Reason}", priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                _log.LogInformation("User {U} {Ticker} Grok APPROVED: {Reason}", _userId, ticker, gc.Reason);
            }

            // ── Gate 5: Claude verification with Anthropic's web_search (~$0.01-0.05, ~10-15s) ──
            // Asks Claude "does this trade make sense given X, Y, Z?" with full web search.
            // Runs AFTER Grok so if both are enabled both must approve before the order fires.
            // ADVISOR MODE: when ClaudeAdvisorMode=true, Claude still runs (and its verdict is
            // still saved in UserGateCalls), but a Veto/Caution does NOT block the trade — the
            // shadow-veto reason is appended to the buy's reason string so Reports can compute
            // "P&L of trades Claude would have stopped" vs "Grok-only baseline".
            string? claudeShadowVetoNote = null;
            if (_claudeVerify is not null)
            {
                ClaudeVerification cv;
                try
                {
                    cv = await _claudeVerify.CheckAsync(ticker, a.Headline, a.Source,
                        verdict.Confidence, verdict.Reasoning, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    cv = new ClaudeVerification(ClaudeVerdict.Error, "Claude exception: " + ex.Message,
                        _anthropicModel, "", "", 0);
                }
                await SaveGateCallAsync("Claude", cv.ModelName, ticker, a, cv.Verdict.ToString(),
                    cv.Reason, cv.Prompt, cv.RawResponse, cv.LatencyMs, ct);

                if (cv.Verdict != ClaudeVerdict.Approve)
                {
                    if (_settings.ClaudeAdvisorMode)
                    {
                        claudeShadowVetoNote = $"[Claude shadow {cv.Verdict}: {Truncate(cv.Reason, 200)}]";
                        _log.LogInformation("User {U} {Ticker} Claude {V} OVERRIDDEN (advisor mode): {Reason}",
                            _userId, ticker, cv.Verdict, cv.Reason);
                    }
                    else
                    {
                        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                            $"Claude {cv.Verdict}: {cv.Reason}", priceUsd: priceSnapshot));
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }
                else _log.LogInformation("User {U} {Ticker} Claude APPROVED: {Reason}", _userId, ticker, cv.Reason);
            }

            // ── Bullish buy path ──
            // Refresh the price here (could be 10-30s after priceSnapshot now that we've gone
            // through Grok+Claude). This fresh price is what we use for sizing and what we
            // store on the eventual UserOrder.PriceAtSubmitUsd, so slippage analysis sees the
            // truest "snapshot the bot used when it pulled the trigger".
            var price = await GetLatestPriceAsync(ticker, ct) ?? 0m;
            if (price <= 0m)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected", "No valid price",
                    priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Position cap: existing market value + pending buy notional vs cap
            var maxPositionDollars = (acct.Equity ?? 0m) * (decimal)_settings.MaxPositionFraction;
            var existingValue = positions
                .Where(x => x.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.MarketValue ?? 0m);
            var pendingBuyNotional = pending.BuyQty * price;
            var headroom = maxPositionDollars - existingValue - pendingBuyNotional;
            if (headroom <= 0m)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                    $"Position cap reached ({existingValue:C} owned + {pendingBuyNotional:C} pending ≥ {maxPositionDollars:C} cap)",
                    priceUsd: price));
                await db.SaveChangesAsync(ct);
                continue;
            }
            var spendable = Math.Min(headroom, acct.BuyingPower ?? 0m);
            var qty = (int)Math.Floor(spendable / price);
            if (qty < 1)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                    $"Sizing < 1 share (spendable {spendable:C} at {price:C})", priceUsd: price));
                await db.SaveChangesAsync(ct);
                continue;
            }

            var entryReason = $"Bullish entry: {verdict.Reasoning}";
            if (claudeShadowVetoNote is not null) entryReason += " " + claudeShadowVetoNote;
            var bought = await SubmitOrderAsync(db, ticker, OrderSide.Buy, qty,
                entryReason, ct, priceAtSubmit: price);
            var outcomeReason = bought ? $"Buy {qty} @ ~{price:C}" : "Broker rejected the buy";
            if (claudeShadowVetoNote is not null) outcomeReason += " " + claudeShadowVetoNote;
            db.UserDecisions.Add(MakeDecision(ticker, a, verdict,
                bought ? "Submitted" : "Rejected",
                outcomeReason,
                side: "Buy", qty: qty, priceUsd: price));
            await db.SaveChangesAsync(ct);
            if (bought) _tradesToday++;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Crypto: parallel to ProcessTickerAsync but with crypto-only news, no SEC/Form 4/
    //  earnings calendar (none exist for crypto), no hours/no-trade-window gates (24/7),
    //  and notional-dollar order sizing (crypto positions are fractional, so integer share
    //  counts don't work for high-priced coins like BTC).
    //
    //  The verification gates (sentiment → confirmation → Grok → Claude) are intentionally
    //  duplicated rather than extracted — they're tightly coupled with the decision-record
    //  logging and the equity path is already too long to safely refactor inline. Cleaning
    //  this up into a shared helper is a separate task.
    // ════════════════════════════════════════════════════════════════════════
    private async Task ProcessCryptoTickerAsync(
        string ticker,
        IAccount acct,
        IReadOnlyList<IPosition> positions,
        (long BuyQty, long SellQty) pending,
        CancellationToken ct)
    {
        if (_cryptoNews is null) return;   // tier didn't allow crypto

        var since = _lastSeen.TryGetValue(ticker, out var ts)
            ? ts
            : DateTimeOffset.UtcNow.AddMinutes(-_settings.FinnhubLookbackMinutes);

        var raw = await _cryptoNews.GetAsync(ticker, since, ct);
        if (raw.Count == 0) return;

        var articles = raw.Select(g => new Article(
            Id: g.Id, Source: "Crypto news: " + g.Source,
            Headline: g.Headline, Summary: g.Summary, Url: g.Url,
            PublishedAt: g.PublishedAt)).OrderBy(a => a.PublishedAt).ToList();

        var newest = articles.Max(a => a.PublishedAt);
        if (newest > since) _lastSeen[ticker] = newest;

        var blockedWords = _settings.BlockedKeywords();
        var boostWords = _settings.BoostKeywords();

        foreach (var a in articles)
        {
            if (!_ingestedNewsIds.Add(a.Id)) continue;

            // One price fetch per article (crypto data feed). Used as the priceUsd snapshot on
            // every UserDecision row written below, regardless of which gate the article exits
            // through. Fetched after dedup so dups don't waste a data call.
            var priceSnapshot = await GetLatestCryptoPriceAsync(ticker, ct);

            // ── Personal rule: blocked-keyword filter ──
            if (blockedWords.Length > 0)
            {
                var lowerHead = a.Headline.ToLowerInvariant();
                var hit = blockedWords.FirstOrDefault(w => !string.IsNullOrEmpty(w) && lowerHead.Contains(w));
                if (hit is not null)
                {
                    await using var scopeBlk = _rootSp.CreateAsyncScope();
                    var dbBlk = scopeBlk.ServiceProvider.GetRequiredService<OwlNestDbContext>();
                    dbBlk.UserDecisions.Add(MakeDecision(ticker, a, null, "NoTradeGate",
                        $"Headline contains blocked keyword '{hit}'", priceUsd: priceSnapshot));
                    await dbBlk.SaveChangesAsync(ct);
                    continue;
                }
            }

            var (verdict, failure) = await AnalyzeAsync(ticker, a.Headline, a.Summary, ct);
            await using var scope = _rootSp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();

            if (verdict is null)
            {
                // Failure reason comes from AnalyzeAsync — surfaces HTTP code, safety block, parse
                // error, timeout, etc. into the user-visible decision row instead of the old
                // generic stand-in.
                db.UserDecisions.Add(MakeDecision(ticker, a, null, "SentimentSkipped",
                    failure ?? "LLM call failed", priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            var effectiveConfidence = verdict.Confidence;
            if (boostWords.Length > 0)
            {
                var lowerHead = a.Headline.ToLowerInvariant();
                if (boostWords.Any(w => !string.IsNullOrEmpty(w) && lowerHead.Contains(w)))
                    effectiveConfidence = Math.Min(1.0, effectiveConfidence + 0.05);
            }

            // ── Gate 1: sentiment + actionable + confidence ──
            var bullishOK = verdict.Sentiment.Equals("bullish", StringComparison.OrdinalIgnoreCase)
                && verdict.Actionable
                && effectiveConfidence >= _settings.MinConfidence;
            var bearishOK = _settings.BearishNewsExitsEnabled
                && verdict.Sentiment.Equals("bearish", StringComparison.OrdinalIgnoreCase)
                && verdict.Actionable
                && verdict.Confidence >= _settings.BearishNewsMinConfidence;

            if (!bullishOK && !bearishOK)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
                    $"Sentiment gate ({verdict.Sentiment}, {verdict.Confidence:P0}, actionable={verdict.Actionable})",
                    priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // ── Gate 2 (bullish only): confirmation window — require N matching signals ──
            if (bullishOK && _settings.RequiredSignalCount > 1 && _signals is not null)
            {
                var count = _signals.RecordAndCount(ticker, "bullish", DateTimeOffset.UtcNow);
                if (count < _settings.RequiredSignalCount)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "NoTradeGate",
                        $"Confirmation gate: {count}/{_settings.RequiredSignalCount} bullish signals in last {_settings.ConfirmationWindowMinutes}m",
                        priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            // ── Bearish: sell entire crypto position (fractional) ──
            if (bearishOK)
            {
                var heldPos = positions.FirstOrDefault(x => x.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase));
                var heldQty = heldPos?.Quantity ?? 0m;
                if (heldQty <= 0m)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                        "Bearish signal but no long crypto position to close (no shorting)",
                        priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                var ok = await SubmitCryptoOrderAsync(db, ticker, OrderSide.Sell,
                    OrderQuantity.Fractional(heldQty), heldQty,
                    "Bearish news exit: " + verdict.Reasoning, ct, priceAtSubmit: priceSnapshot);
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict,
                    ok ? "Submitted" : "Rejected",
                    ok ? $"Bearish exit: sell {heldQty} {ticker}" : "Broker rejected the sell",
                    side: "Sell", qty: (int)Math.Floor(heldQty), priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                if (ok) _tradesToday++;
                continue;
            }

            // Earnings blackout / no-trade windows: SKIPPED for crypto (no analogue / 24/7).

            // ── Gate 4: Grok 2nd-opinion (same as equity) ──
            if (_grokConfirm is not null)
            {
                GrokConfirmation gc;
                try
                {
                    gc = await _grokConfirm.CheckAsync(ticker, a.Headline, a.Source,
                        verdict.Confidence, verdict.Reasoning, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { gc = new GrokConfirmation(GrokVerdict.Error,
                    "Grok exception: " + ex.Message, "grok-3-mini", "", "", 0); }
                await SaveGateCallAsync("Grok", gc.ModelName, ticker, a, gc.Verdict.ToString(),
                    gc.Reason, gc.Prompt, gc.RawResponse, gc.LatencyMs, ct);

                if (gc.Verdict != GrokVerdict.Approve)
                {
                    db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                        $"Grok {gc.Verdict}: {gc.Reason}", priceUsd: priceSnapshot));
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            // ── Gate 5: Claude verification (same as equity, advisor-mode aware) ──
            string? claudeShadowVetoNote = null;
            if (_claudeVerify is not null)
            {
                ClaudeVerification cv;
                try
                {
                    cv = await _claudeVerify.CheckAsync(ticker, a.Headline, a.Source,
                        verdict.Confidence, verdict.Reasoning, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { cv = new ClaudeVerification(ClaudeVerdict.Error,
                    "Claude exception: " + ex.Message, _anthropicModel, "", "", 0); }
                await SaveGateCallAsync("Claude", cv.ModelName, ticker, a, cv.Verdict.ToString(),
                    cv.Reason, cv.Prompt, cv.RawResponse, cv.LatencyMs, ct);

                if (cv.Verdict != ClaudeVerdict.Approve)
                {
                    if (_settings.ClaudeAdvisorMode)
                    {
                        claudeShadowVetoNote = $"[Claude shadow {cv.Verdict}: {Truncate(cv.Reason, 200)}]";
                        _log.LogInformation("User {U} {Ticker} Claude {V} OVERRIDDEN (advisor mode, crypto): {Reason}",
                            _userId, ticker, cv.Verdict, cv.Reason);
                    }
                    else
                    {
                        db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                            $"Claude {cv.Verdict}: {cv.Reason}", priceUsd: priceSnapshot));
                        await db.SaveChangesAsync(ct);
                        continue;
                    }
                }
            }

            // ── Bullish buy: notional dollar order ──
            // Refresh price (could be 10-30s after priceSnapshot now that we've gone through
            // Grok+Claude). This fresh price is what we store on UserOrder.PriceAtSubmitUsd.
            var price = await GetLatestCryptoPriceAsync(ticker, ct) ?? 0m;
            if (price <= 0m)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected", "No valid crypto price",
                    priceUsd: priceSnapshot));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Position cap: existing market value + pending buy notional vs cap (same math as equity)
            var maxPositionDollars = (acct.Equity ?? 0m) * (decimal)_settings.MaxPositionFraction;
            var existingValue = positions
                .Where(x => x.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.MarketValue ?? 0m);
            var pendingBuyNotional = pending.BuyQty * price;   // for crypto, BuyQty is approximate
            var headroom = maxPositionDollars - existingValue - pendingBuyNotional;
            if (headroom <= 0m)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                    $"Position cap reached ({existingValue:C} owned + {pendingBuyNotional:C} pending ≥ {maxPositionDollars:C} cap)",
                    priceUsd: price));
                await db.SaveChangesAsync(ct);
                continue;
            }
            var spendable = Math.Min(headroom, acct.BuyingPower ?? 0m);
            // Alpaca rejects crypto notional orders under $1.
            if (spendable < 1m)
            {
                db.UserDecisions.Add(MakeDecision(ticker, a, verdict, "Rejected",
                    $"Spendable {spendable:C} below Alpaca's $1 notional minimum", priceUsd: price));
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Round down to whole dollars for cleanliness — partial-dollar orders work but make
            // the dashboard noisy.
            var notional = Math.Floor(spendable);
            var entryReason = $"Bullish crypto entry: {verdict.Reasoning}";
            if (claudeShadowVetoNote is not null) entryReason += " " + claudeShadowVetoNote;
            var bought = await SubmitCryptoOrderAsync(db, ticker, OrderSide.Buy,
                OrderQuantity.Notional(notional), notional,
                entryReason, ct, priceAtSubmit: price);
            var outcomeReason = bought ? $"Buy ~{notional:C} of {ticker} @ ~{price:C}" : "Broker rejected the crypto buy";
            if (claudeShadowVetoNote is not null) outcomeReason += " " + claudeShadowVetoNote;
            db.UserDecisions.Add(MakeDecision(ticker, a, verdict,
                bought ? "Submitted" : "Rejected",
                outcomeReason,
                side: "Buy", qty: (int)notional, priceUsd: price));
            await db.SaveChangesAsync(ct);
            if (bought) _tradesToday++;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Discovery — firehose buzz + Grok trending → dynamic watchlist
    // ════════════════════════════════════════════════════════════════════════
    private async Task RunDiscoveryIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Buzz tracking + Gemini extractor (cheap-ish, runs every DiscoveryExtractorIntervalSeconds)
        if (_settings.DiscoveryEnabled && _firehose is not null && _buzz is not null && _watchlist is not null
            && now - _lastDiscoveryRun >= TimeSpan.FromSeconds(Math.Max(60, _settings.DiscoveryExtractorIntervalSeconds)))
        {
            _lastDiscoveryRun = now;
            try
            {
                _buzz.Prune(now);
                _watchlist.Expire(now);

                var articles = (await _firehose.GetAsync(ct)).ToList();

                // Reddit removed in v8 — Anthropic's web_search via Claude verification covers
                // similar ground without Reddit's anti-scraping problems.

                if (articles.Count > 0)
                {
                    // Backfill tickers via Gemini if extractor is enabled
                    if (_extractor is not null)
                    {
                        // Cap to first 30 untagged headlines to keep token usage bounded
                        var untagged = articles.Where(a => a.Tickers.Length == 0).Take(30).ToList();
                        if (untagged.Count > 0)
                        {
                            var enriched = await _extractor.ExtractAsync(untagged, ct);
                            var enrichedMap = enriched.ToDictionary(a => a.Id, StringComparer.Ordinal);
                            articles = articles.Select(a => enrichedMap.GetValueOrDefault(a.Id) ?? a).ToList();
                        }
                    }

                    foreach (var a in articles) _buzz.Ingest(a);

                    var buzzy = _buzz.Buzzy()
                        .Select(b => (b.Ticker, b.Score, (string?)$"buzz={b.Score}"))
                        .ToList();
                    if (buzzy.Count > 0)
                    {
                        var newlyPromoted = _watchlist.PromoteMany(buzzy, _settings.Universe());
                        await PersistWatchlistEventsAsync("Buzz", newlyPromoted, ct);
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "User {U} discovery failed", _userId); }
        }

        // Grok trending (expensive, runs every GrokPollIntervalSeconds)
        if (_grok is not null && _watchlist is not null
            && now - _lastGrokRun >= TimeSpan.FromSeconds(Math.Max(300, _settings.GrokPollIntervalSeconds)))
        {
            _lastGrokRun = now;
            try
            {
                var trending = await _grok.FetchAsync(ct);
                LastTrending = trending;
                if (trending.Count > 0)
                {
                    var promote = trending
                        .Where(t => t.Sentiment != "bearish")    // don't promote pure shorts
                        .Select(t => (t.Ticker, 5, (string?)$"grok: {t.Reason}"))
                        .ToList();
                    if (promote.Count > 0)
                    {
                        var newlyPromoted = _watchlist.PromoteMany(promote, _settings.Universe());
                        await PersistWatchlistEventsAsync("Grok", newlyPromoted, ct);
                    }
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "User {U} Grok trending failed", _userId); }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Mechanical exits (stop / trail / TP / time)
    // ════════════════════════════════════════════════════════════════════════
    private async Task EvaluateExitsAsync(
        IReadOnlyList<IPosition> positions,
        IReadOnlyDictionary<string, (long BuyQty, long SellQty)> pendingByTicker,
        CancellationToken ct)
    {
        if (positions.Count == 0) return;
        await using var scope = _rootSp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();

        foreach (var pos in positions)
        {
            ct.ThrowIfCancellationRequested();
            var ticker = pos.Symbol;
            var qty = (long)pos.IntegerQuantity;
            if (qty <= 0) continue;

            var entry = pos.AverageEntryPrice;
            var currentPrice = (pos.MarketValue ?? 0m) / qty;
            var pnlPct = entry > 0m ? (double)((currentPrice - entry) / entry) : 0.0;

            var pendingSell = pendingByTicker.TryGetValue(ticker, out var pp) ? pp.SellQty : 0L;
            if (pendingSell >= qty) continue;

            var dbPos = await db.UserPositions
                .FirstOrDefaultAsync(p => p.UserId == _userId && p.Ticker == ticker, ct);
            if (dbPos is null) continue;

            var sinceOpen = (DateTimeOffset.UtcNow - dbPos.OpenedAtUtc).TotalMinutes;
            var sinceBotStart = (DateTimeOffset.UtcNow - _botStartedAt).TotalMinutes;
            var armed = sinceOpen >= _settings.StopArmDelayMinutes
                     && sinceBotStart >= _settings.StopArmDelayMinutes
                     && sinceOpen >= _settings.MinHoldMinutes;   // user's personal min-hold rule

            string? reason = null;

            var stopActive = _settings.StopLossType is "Hard" or "Both";
            if (armed && stopActive && _settings.StopLossPercent > 0 && pnlPct <= -_settings.StopLossPercent)
                reason = $"Stop loss: {pnlPct:P2} (entry {entry:C}, now {currentPrice:C})";

            if (reason is null && _settings.TakeProfitPercent > 0 && pnlPct >= _settings.TakeProfitPercent)
                reason = $"Take profit: +{pnlPct:P2}";

            var trailActive = _settings.StopLossType is "Trailing" or "Both";
            if (reason is null && armed && trailActive && _settings.TrailingStopPercent > 0
                && pnlPct >= _settings.TrailingStopActivationPercent)
            {
                var peak = dbPos.PeakPrice > 0 ? dbPos.PeakPrice : currentPrice;
                var dropFromPeak = peak > 0m ? (double)((currentPrice - peak) / peak) : 0.0;
                if (dropFromPeak <= -_settings.TrailingStopPercent)
                    reason = $"Trailing stop: peak {peak:C}, now {currentPrice:C} ({dropFromPeak:P2} off peak, banking +{pnlPct:P2})";
            }

            if (reason is null && _settings.MaxHoldDays > 0)
            {
                var heldDays = (DateTimeOffset.UtcNow - dbPos.OpenedAtUtc).TotalDays;
                if (heldDays >= _settings.MaxHoldDays)
                    reason = $"Time exit: held {heldDays:F1}d (≥{_settings.MaxHoldDays}d), {pnlPct:P2}";
            }

            if (reason is null) continue;

            var sellableQty = qty - pendingSell;
            if (sellableQty <= 0) continue;
            var ok = await SubmitOrderAsync(db, ticker, OrderSide.Sell, sellableQty, reason, ct);
            if (ok)
            {
                _tradesToday++;
                _log.LogInformation("User {U} EXIT {Ticker}: {Reason}", _userId, ticker, reason);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  DB sync helpers
    // ════════════════════════════════════════════════════════════════════════
    private async Task SyncPositionsAsync(IReadOnlyList<IPosition> live, CancellationToken ct)
    {
        await using var scope = _rootSp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
        var existing = await db.UserPositions.Where(p => p.UserId == _userId).ToListAsync(ct);
        var liveByTicker = live.ToDictionary(p => p.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var e in existing.ToList())
            if (!liveByTicker.ContainsKey(e.Ticker))
                db.UserPositions.Remove(e);

        foreach (var p in live)
        {
            // For crypto positions, IntegerQuantity rounds 0.027 BTC down to 0 — use Quantity (decimal)
            // for the include-or-skip check so fractional crypto positions still persist to the DB.
            if (p.Quantity <= 0) continue;
            var qty = (long)p.IntegerQuantity;
            var price = p.Quantity > 0m ? (p.MarketValue ?? 0m) / p.Quantity : 0m;

            var row = existing.FirstOrDefault(e => e.Ticker.Equals(p.Symbol, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                db.UserPositions.Add(new UserPosition
                {
                    UserId = _userId,
                    Ticker = p.Symbol,
                    Quantity = qty,
                    AverageEntryPrice = p.AverageEntryPrice,
                    MarketValue = p.MarketValue ?? 0m,
                    UnrealizedPnL = p.UnrealizedProfitLoss ?? 0m,
                    OpenedAtUtc = DateTimeOffset.UtcNow,
                    PeakPrice = Math.Max(price, p.AverageEntryPrice),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                row.Quantity = qty;
                row.AverageEntryPrice = p.AverageEntryPrice;
                row.MarketValue = p.MarketValue ?? 0m;
                row.UnrealizedPnL = p.UnrealizedProfitLoss ?? 0m;
                if (price > row.PeakPrice) row.PeakPrice = price;
                row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SyncOrdersAsync(IReadOnlyList<IOrder> live, CancellationToken ct)
    {
        await using var scope = _rootSp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
        var ourIds = await db.UserOrders.Where(o => o.UserId == _userId).Select(o => o.OrderId).ToListAsync(ct);
        var ourSet = new HashSet<string>(ourIds, StringComparer.Ordinal);

        foreach (var o in live)
        {
            var idStr = o.OrderId.ToString();
            if (!ourSet.Contains(idStr)) continue;
            var row = await db.UserOrders.FirstOrDefaultAsync(x => x.UserId == _userId && x.OrderId == idStr, ct);
            if (row is null) continue;
            row.Status = o.OrderStatus.ToString().ToLowerInvariant();
            row.FilledQuantity = (long)o.IntegerFilledQuantity;
            row.FilledAvgPrice = o.AverageFillPrice;
            row.FilledAtUtc = o.FilledAtUtc;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SnapshotEquityAsync(IAccount acct, CancellationToken ct)
    {
        await using var scope = _rootSp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
        db.UserEquitySnapshots.Add(new UserEquitySnapshot
        {
            UserId = _userId,
            AtUtc = DateTimeOffset.UtcNow,
            Equity = acct.Equity ?? 0m,
            Cash = acct.TradableCash,
            BuyingPower = acct.BuyingPower ?? 0m,
        });
        await db.SaveChangesAsync(ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Order submission
    // ════════════════════════════════════════════════════════════════════════
    private async Task<bool> SubmitOrderAsync(
        OwlNestDbContext db,
        string ticker, OrderSide side, long qty, string reason, CancellationToken ct,
        decimal? priceAtSubmit = null)
    {
        if (qty <= 0) return false;
        try
        {
            var order = side == OrderSide.Buy
                ? MarketOrder.Buy(ticker, qty)
                : MarketOrder.Sell(ticker, qty);
            var submitted = await _trading.PostOrderAsync(order.WithDuration(TimeInForce.Day), ct);
            db.UserOrders.Add(new UserOrder
            {
                UserId = _userId,
                OrderId = submitted.OrderId.ToString(),
                Ticker = ticker,
                Side = side == OrderSide.Buy ? "Buy" : "Sell",
                Quantity = qty,
                Status = "new",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                Reason = reason,
                PriceAtSubmitUsd = priceAtSubmit,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "User {U} order failed: {Side} {Qty} {Ticker}", _userId, side, qty, ticker);
            return false;
        }
    }

    /// <summary>
    /// Crypto-flavoured order submission. Two shapes:
    ///   - Buy:  pass a notional USD amount (Alpaca splits it into the right fractional qty).
    ///   - Sell: pass a fractional coin quantity (closes that many units of the position).
    /// We use TimeInForce.Gtc for crypto since Day orders aren't supported on the 24/7 venue.
    /// Quantity in the DB row is the rounded-down notional for buys and the floor of qty for
    /// sells — display will be slightly imprecise for sub-dollar crypto, but every actual fill
    /// at Alpaca uses the precise OrderQuantity we hand the SDK.
    /// </summary>
    private async Task<bool> SubmitCryptoOrderAsync(
        OwlNestDbContext db,
        string ticker, OrderSide side, OrderQuantity quantity, decimal displayValue, string reason, CancellationToken ct,
        decimal? priceAtSubmit = null)
    {
        if (quantity.Value <= 0) return false;
        try
        {
            var order = side == OrderSide.Buy
                ? MarketOrder.Buy(ticker, quantity)
                : MarketOrder.Sell(ticker, quantity);
            var submitted = await _trading.PostOrderAsync(order.WithDuration(TimeInForce.Gtc), ct);
            db.UserOrders.Add(new UserOrder
            {
                UserId = _userId,
                OrderId = submitted.OrderId.ToString(),
                Ticker = ticker,
                Side = side == OrderSide.Buy ? "Buy" : "Sell",
                Quantity = (long)Math.Floor(displayValue),
                Status = "new",
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                Reason = reason,
                PriceAtSubmitUsd = priceAtSubmit,
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "User {U} crypto order failed: {Side} {Q} {Ticker}", _userId, side, quantity.Value, ticker);
            return false;
        }
    }

    /// <summary>
    /// Latest trade price for a crypto symbol via Alpaca's crypto data client. Defaults to the
    /// Coinbase venue which is what Alpaca recommends for US-account crypto pricing. Returns
    /// null on any error (caller treats that as "no valid price → skip the trade").
    /// </summary>
    private async Task<decimal?> GetLatestCryptoPriceAsync(string ticker, CancellationToken ct)
    {
        if (_cryptoData is null) return null;
        try
        {
            // The single-symbol GetLatestTradeAsync + per-exchange request constructor are
            // deprecated in SDK 7.2 — use the list API with a one-element symbol list and let
            // Alpaca pick the venue (Coinbase by default).
            var trades = await _cryptoData.ListLatestTradesAsync(
                new LatestDataListRequest(new[] { ticker }), ct);
            return trades.TryGetValue(ticker, out var t) ? t.Price : (decimal?)null;
        }
        catch { return null; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  News + price + LLM
    // ════════════════════════════════════════════════════════════════════════
    private async Task<List<Article>> FetchFinnhubAsync(string ticker, DateTimeOffset since, CancellationToken ct)
    {
        if (!_settings.UseFinnhub) return new();
        var to = DateTimeOffset.UtcNow.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var from = since.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var url = $"company-news?symbol={Uri.EscapeDataString(ticker)}&from={from}&to={to}&token={Uri.EscapeDataString(_finnhubKey)}";
        try
        {
            var raw = await _finnhub.GetFromJsonAsync<RawArticle[]>(url, ct);
            if (raw is null) return new();
            var sinceUnix = since.ToUnixTimeSeconds();
            return raw.Where(a => a.Datetime > sinceUnix && !string.IsNullOrWhiteSpace(a.Headline))
                      .Select(a => new Article(
                          Id: $"fh:{a.Id}",
                          Source: a.Source ?? "",
                          Headline: a.Headline ?? "",
                          Summary: a.Summary ?? "",
                          Url: a.Url ?? "",
                          PublishedAt: DateTimeOffset.FromUnixTimeSeconds(a.Datetime)))
                      .ToList();
        }
        catch { return new(); }
    }

    private async Task<decimal?> GetLatestPriceAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var t = await _data.GetLatestTradeAsync(new LatestMarketDataRequest(ticker), ct);
            return t.Price;
        }
        catch { return null; }
    }

    /// <summary>
    /// Run the LLM sentiment classifier. Returns the parsed verdict plus a diagnostic string
    /// when the call fails (so the "SentimentSkipped" decision row can record the actual cause
    /// instead of the generic "network/parse/rate-limit" stand-in). On success: (verdict, null).
    /// On any failure: (null, "&lt;short reason&gt;") — never throws.
    ///
    /// The reason string is intentionally surfaced into the user-visible decisions feed, so it
    /// has to be short and contain zero secrets. We trim error bodies aggressively and never
    /// include the API key (it lives in the query string, never in the response body).
    /// </summary>
    private async Task<(Verdict? Verdict, string? FailureReason)> AnalyzeAsync(
        string ticker, string headline, string summary, CancellationToken ct)
    {
        // Inject macro context if enabled & available — combine Manifold odds + recent Fed events.
        var macroParts = new List<string>();
        if (_settings.MacroSource.Equals("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            var s = _macro.PromptSummary();
            if (!string.IsNullOrWhiteSpace(s)) macroParts.Add(s);
        }
        if (_settings.UseFomcMacro)
        {
            var f = _fed.PromptSummary();
            if (!string.IsNullOrWhiteSpace(f)) macroParts.Add(f);
        }
        var macroLine = macroParts.Count == 0 ? "" : "\n\nMacro context: " + string.Join(" ", macroParts);

        var user = $"Ticker: {ticker}\nHeadline: {headline}\nSummary: {summary}{macroLine}";
        const string sys = "You are a stock sentiment classifier. Return JSON: {\"sentiment\":\"bullish|bearish|neutral\",\"confidence\":0.0-1.0,\"is_actionable\":bool,\"reasoning\":\"one sentence\"}. Be conservative — only actionable for genuinely new, material, credibly-sourced catalysts. If macro context is provided, factor it in (e.g. a bullish company headline with a bearish macro backdrop should be slightly less actionable).";

        try
        {
            string raw;
            string text;
            if (_llmProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var body = new
                {
                    model = _anthropicModel,
                    max_tokens = 400,
                    system = sys,
                    messages = new[] { new { role = "user", content = user } }
                };
                using var resp = await _llmHttp.PostAsJsonAsync("v1/messages", body, ct);
                raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return (null, $"Anthropic HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                text = ExtractAnthropicText(raw);
                if (string.IsNullOrWhiteSpace(text))
                    return (null, "Anthropic returned empty content");
            }
            else if (_llmProvider.Equals("Llama", StringComparison.OrdinalIgnoreCase))
            {
                // Groq-hosted Llama via OpenAI-compatible chat/completions. system + user roles.
                // NOTE: we intentionally DO NOT set response_format=json_object — Llama 4 Scout
                // has a known bug where strict JSON mode causes premature server-side truncation
                // (~46-char outputs regardless of max_tokens). The system prompt already asks
                // for JSON, and ParseVerdict's brace-extraction handles any model preamble.
                var body = new
                {
                    model = _llamaModel,
                    messages = new object[]
                    {
                        new { role = "system", content = sys },
                        new { role = "user",   content = user }
                    },
                    temperature = 0.0,
                    max_tokens = 1000,
                };
                using var resp = await _llmHttp.PostAsJsonAsync("openai/v1/chat/completions", body, ct);
                raw = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return (null, $"Llama HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}");
                text = ExtractOpenAIChatText(raw);
                if (string.IsNullOrWhiteSpace(text))
                    return (null, "Llama returned empty content");
            }
            else
            {
                var body = new
                {
                    systemInstruction = new { parts = new[] { new { text = sys } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
                    generationConfig = new
                    {
                        temperature = 0.0,
                        // 2000 (was 800) — safety belt in case thinkingBudget=0 doesn't fully
                        // suppress reasoning on a future model rev. With reasoning OFF, a typical
                        // verdict is ~70 tokens, so 2000 is massive overkill but free.
                        maxOutputTokens = 2000,
                        responseMimeType = "application/json",
                        // CRITICAL: Gemini 2.5 Flash is a reasoning model — it emits hidden
                        // "thinking" tokens before the visible answer, and they COUNT against
                        // maxOutputTokens. Verified via API probe: a 45-token prompt produced
                        // 103 content tokens + 191 thinking tokens. With macro context injected
                        // into longer crypto prompts, reasoning was blowing through the old 800
                        // budget and Gemini was getting cut off mid-JSON → "Could not parse JSON"
                        // failures we chased through Scout, 70B, Llama fallback for days.
                        // thinkingBudget=0 disables reasoning entirely. For "classify this
                        // headline, return JSON, no explanation" tasks, reasoning adds zero
                        // value and just burns tokens.
                        thinkingConfig = new { thinkingBudget = 0 }
                    }
                };
                var url = $"v1beta/models/{_geminiModel}:generateContent?key={Uri.EscapeDataString(_geminiKey ?? "")}";
                using var resp = await _llmHttp.PostAsJsonAsync(url, body, ct);
                raw = await resp.Content.ReadAsStringAsync(ct);

                // Detect any kind of Gemini failure — HTTP error (429 rate limit, 503 outage),
                // safety-filter block, or empty content. All three cases trigger the same Llama
                // rescue path so a hiccup on Google's end doesn't kill the signal.
                string? geminiFailReason = null;
                if (!resp.IsSuccessStatusCode)
                {
                    geminiFailReason = $"Gemini HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}";
                }
                else
                {
                    var safetyReason = DetectGeminiBlock(raw);
                    if (safetyReason is not null)
                        geminiFailReason = $"Gemini blocked: {safetyReason}";
                }

                if (geminiFailReason is not null)
                {
                    // Rescue with Llama if configured. Llama has no financial-content filter, no
                    // shared rate limit with Google, and almost always returns a verdict. We
                    // persist a UserGateCall row capturing what we sent + got so the audit log
                    // shows whether each Gemini failure was rescued or not.
                    if (_llamaFallbackHttp is not null)
                    {
                        var fbSw = System.Diagnostics.Stopwatch.StartNew();
                        var (fbText, fbErr, fbRawResponse) = await CallLlamaFallbackAsync(sys, user, ct);
                        fbSw.Stop();
                        var fbLatencyMs = (int)fbSw.ElapsedMilliseconds;

                        Verdict? fbVerdict = fbText is not null ? ParseVerdict(fbText) : null;
                        var auditVerdict = fbVerdict?.Sentiment ?? (fbErr ?? "parse-failed");
                        var auditReason  = fbVerdict?.Reasoning
                                          ?? (fbErr ?? "ParseVerdict returned null on text: " + Truncate(fbText, 300));

                        await SaveGateCallAsync("LlamaFallback", _llamaModel, ticker,
                            new Article(Id: "", Source: geminiFailReason,
                                Headline: headline, Summary: summary, Url: "",
                                PublishedAt: DateTimeOffset.UtcNow),
                            auditVerdict, auditReason, user, fbRawResponse, fbLatencyMs, ct);

                        if (fbVerdict is not null)
                        {
                            _log.LogInformation("Gemini failed for {Ticker} ({Reason}) — Llama fallback returned {Sentiment}",
                                ticker, geminiFailReason, fbVerdict.Sentiment);
                            fbVerdict.Reasoning = $"[Llama fallback after Gemini fail] {fbVerdict.Reasoning}";
                            return (fbVerdict, null);
                        }
                        return (null, $"{geminiFailReason} + Llama fallback failed: {fbErr ?? "JSON parse error"}");
                    }
                    return (null, geminiFailReason);
                }

                text = ExtractGeminiText(raw);
                if (string.IsNullOrWhiteSpace(text))
                    return (null, "Gemini returned empty text (no candidates)");
            }
            var verdict = ParseVerdict(text);
            return verdict is not null
                ? (verdict, null)
                : (null, $"Could not parse JSON from model: {Truncate(text, 200)}");
        }
        // Caller-triggered cancellation propagates up; everything else turns into a friendly reason.
        // TaskCanceledException derives from OperationCanceledException, so we use a `when` filter
        // to split "user cancelled" from "HttpClient.Timeout fired".
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (null, "LLM call timed out (>30s)");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Network: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Run a single Llama (Groq) call using the same prompt the primary classifier would have used,
    /// returning either the model's raw text or a short failure reason. Used as the safety-block
    /// fallback when the primary provider (Gemini) refuses. Never throws.
    /// </summary>
    /// <summary>
    /// Run a Llama (Groq) call as fallback when Gemini refuses. Returns the parsed message text,
    /// a failure reason if the call failed, AND the raw HTTP body so the caller can persist it
    /// to UserGateCalls for debugging (e.g. when Groq mysteriously returns truncated output).
    /// </summary>
    private async Task<(string? Text, string? Failure, string RawResponse)> CallLlamaFallbackAsync(
        string sys, string user, CancellationToken ct)
    {
        if (_llamaFallbackHttp is null) return (null, "fallback not configured", "");
        try
        {
            var body = new
            {
                model = _llamaModel,
                messages = new object[]
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = user }
                },
                temperature = 0.0,
                max_tokens = 1000,
                // NO response_format — Llama 4 Scout truncates server-side at ~46 chars when
                // strict JSON mode is enabled, regardless of max_tokens. The system prompt asks
                // for JSON, and ParseVerdict extracts braces from any preamble.
            };
            using var resp = await _llamaFallbackHttp.PostAsJsonAsync("openai/v1/chat/completions", body, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}: {Truncate(raw, 200)}", raw);
            var text = ExtractOpenAIChatText(raw);
            return string.IsNullOrWhiteSpace(text)
                ? (null, "empty content", raw)
                : (text, null, raw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (null, ex.Message, ""); }
    }

    /// <summary>
    /// Returns a short reason string when Gemini blocked the response (safety filter, recitation,
    /// blocklist), null otherwise. Looks at candidates[0].finishReason — Gemini returns the
    /// candidate but with no parts when blocked.
    /// </summary>
    private static string? DetectGeminiBlock(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            // Top-level promptFeedback.blockReason fires when the INPUT is filtered.
            if (doc.RootElement.TryGetProperty("promptFeedback", out var pf)
                && pf.TryGetProperty("blockReason", out var br) && br.ValueKind == JsonValueKind.String)
                return $"prompt blocked ({br.GetString()})";
            // candidates[0].finishReason fires when the OUTPUT is filtered.
            if (doc.RootElement.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                if (first.TryGetProperty("finishReason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    var reason = fr.GetString();
                    // STOP and MAX_TOKENS are the happy-path values; anything else is a refusal.
                    if (reason is not null and not "STOP" and not "MAX_TOKENS")
                        return $"finishReason={reason}";
                }
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "...";

    /// <summary>
    /// Persist UserWatchlistEvent rows for tickers freshly promoted to the dynamic watchlist —
    /// one row per ticker, with a price snapshot fetched at promotion time. Best-effort: any
    /// failure (DB transient, Alpaca data hiccup) is logged and swallowed. Used by both the buzz
    /// discovery path and the Grok trending path.
    /// </summary>
    private async Task PersistWatchlistEventsAsync(
        string source, IReadOnlyList<(string Ticker, int Score, string? Reason)> promotions, CancellationToken ct)
    {
        if (promotions.Count == 0) return;
        try
        {
            await using var scope = _rootSp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
            foreach (var (ticker, score, reason) in promotions)
            {
                // Pick the right price feed by symbol shape — crypto tickers contain a slash.
                decimal? price = TierPolicy.IsCryptoTicker(ticker)
                    ? await GetLatestCryptoPriceAsync(ticker, ct)
                    : await GetLatestPriceAsync(ticker, ct);
                db.UserWatchlistEvents.Add(new UserWatchlistEvent
                {
                    UserId = _userId,
                    AtUtc = DateTimeOffset.UtcNow,
                    Ticker = ticker,
                    Source = source,
                    BuzzScore = score,
                    Reason = reason,
                    PriceUsd = price,
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist UserWatchlistEvents for user {U}", _userId);
        }
    }

    /// <summary>
    /// Persist one UserGateCall row capturing what we sent to Grok/Claude and what came back.
    /// Called from both ProcessTickerAsync (equity) and ProcessCryptoTickerAsync (crypto) after
    /// every gate invocation, regardless of outcome. Prompt + raw response are truncated to 8 KB
    /// each so the table doesn't balloon on misbehaving models.
    /// </summary>
    private async Task SaveGateCallAsync(
        string gate, string modelName, string ticker, Article a, string verdict, string reason,
        string prompt, string rawResponse, int latencyMs, CancellationToken ct)
    {
        try
        {
            await using var scope = _rootSp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OwlNestDbContext>();
            db.UserGateCalls.Add(new UserGateCall
            {
                UserId = _userId,
                AtUtc = DateTimeOffset.UtcNow,
                Gate = gate,
                ModelName = modelName ?? "",
                Ticker = ticker,
                Source = a.Source ?? "",
                Headline = a.Headline ?? "",
                Prompt = Truncate(prompt, 8000),
                RawResponse = Truncate(rawResponse, 8000),
                Verdict = verdict ?? "",
                Reason = reason ?? "",
                LatencyMs = latencyMs,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit-log writes should never break the trading loop. Log and move on.
            _log.LogWarning(ex, "Failed to persist UserGateCall for user {U} ticker {T}", _userId, ticker);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Decision helper + JSON wire types + utilities
    // ════════════════════════════════════════════════════════════════════════
    private UserDecision MakeDecision(string ticker, Article a, Verdict? v, string outcome, string reason,
        string? side = null, int? qty = null, decimal? priceUsd = null) =>
        new()
        {
            UserId = _userId,
            AtUtc = DateTimeOffset.UtcNow,
            Ticker = ticker,
            Source = a.Source,
            Headline = a.Headline,
            Url = a.Url,
            PublishedAtUtc = a.PublishedAt,
            Sentiment = v?.Sentiment,
            Confidence = v?.Confidence,
            Actionable = v?.Actionable,
            Reasoning = v?.Reasoning,
            Outcome = outcome,
            OutcomeReason = reason,
            Side = side,
            Quantity = qty,
            PriceUsd = priceUsd,
        };

    private static bool IsPendingStatus(OrderStatus s) => s switch
    {
        OrderStatus.New or OrderStatus.Accepted or OrderStatus.PendingNew or OrderStatus.Held or
        OrderStatus.AcceptedForBidding or OrderStatus.PartiallyFilled or OrderStatus.PendingReplace or
        OrderStatus.PendingCancel => true,
        _ => false
    };

    private static string ExtractAnthropicText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
                if (p.TryGetProperty("text", out var t)) return t.GetString() ?? "";
        return "";
    }
    /// <summary>
    /// Extract the model text from a Gemini response. CRITICAL: Gemini may split the JSON output
    /// across MULTIPLE entries in candidates[0].content.parts — especially when responseMimeType
    /// is "application/json" and the JSON is pretty-printed. The previous version returned only
    /// parts[0].text and ate the rest, producing fake "truncated JSON" failures.
    ///
    /// This version concatenates every text part in the FIRST candidate, reconstructing the full
    /// model output. Empty/non-text parts are skipped silently.
    /// </summary>
    private static string ExtractGeminiText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
            return "";
        var first = arr[0];
        if (!first.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                sb.Append(t.GetString());
        return sb.ToString();
    }
    /// <summary>OpenAI-shape: choices[0].message.content. Used by Groq/Llama and any future OpenAI-compat endpoint.</summary>
    private static string ExtractOpenAIChatText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("choices", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var first = arr[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
        }
        return "";
    }
    private static Verdict? ParseVerdict(string text)
    {
        var json = text.Trim();
        if (json.StartsWith("```")) { var nl = json.IndexOf('\n'); if (nl > 0) json = json[(nl + 1)..]; if (json.EndsWith("```")) json = json[..^3]; json = json.Trim(); }
        var firstBrace = json.IndexOf('{');
        var lastBrace = json.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace) json = json[firstBrace..(lastBrace + 1)];
        try { return JsonSerializer.Deserialize<Verdict>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return null; }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_runTask is not null) { try { await _runTask; } catch { /* ignored */ } }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _finnhub.Dispose();
        _llmHttp.Dispose();
        _grokHttp.Dispose();
        _llamaFallbackHttp?.Dispose();
        _claudeHttp?.Dispose();
        _trading.Dispose();
        _data.Dispose();
    }

    private sealed record Article(string Id, string Source, string Headline, string Summary, string Url, DateTimeOffset PublishedAt);

    private sealed class RawArticle
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("datetime")] public long Datetime { get; set; }
        [JsonPropertyName("headline")] public string? Headline { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    public sealed class Verdict
    {
        [JsonPropertyName("sentiment")] public string Sentiment { get; set; } = "neutral";
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("is_actionable")] public bool Actionable { get; set; }
        [JsonPropertyName("reasoning")] public string Reasoning { get; set; } = "";
    }
}
