namespace TradingBot.Web.Data;

/// <summary>
/// All per-user knobs the settings page exposes. Mirrors the single-user bot's appsettings
/// sections but flattened and per-account.
/// </summary>
public sealed class UserSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    // ── Subscription tier ───────────────────────────────────────────────────
    /// <summary>"Starter" | "Plus" | "Pro" — gates which features the bot actually applies.
    /// Legacy DB rows may still hold "Free"; <see cref="Services.TierPolicy.Normalize"/> maps that to Starter.</summary>
    public string Tier { get; set; } = "Starter";

    // ── Master switches ─────────────────────────────────────────────────────
    public bool TradingEnabled { get; set; } = false;
    public bool RegularHoursOnly { get; set; } = true;

    // ── News sources ────────────────────────────────────────────────────────
    public bool UseFinnhub { get; set; } = true;
    public int FinnhubLookbackMinutes { get; set; } = 60;

    public bool UseSecEdgar { get; set; } = true;
    public string SecEdgarContactEmail { get; set; } = "";
    public bool SecEdgarForm8K { get; set; } = true;
    public bool SecEdgarForm10Q { get; set; } = true;
    public bool SecEdgarForm10K { get; set; } = true;

    /// <summary>Finnhub insider-transactions feed (Form 4 pre-parsed) — open-market insider buys
    /// are one of the highest-signal indicators that exists.</summary>
    public bool UseInsiderTransactions { get; set; } = true;

    /// <summary>Google News RSS per ticker — aggregates Reuters, MarketWatch, Yahoo, etc.
    /// Free, no key needed. Catches stories Finnhub is slow to surface.</summary>
    public bool UseGoogleNews { get; set; } = true;

    // Reddit feed removed in v8 — Anthropic's web_search via Claude verification covers the same
    // ground (and more), without Reddit's anti-scraping flakiness.

    /// <summary>Inject recent Fed press releases + speeches into the macro prompt summary
    /// alongside Manifold odds. Free, no key needed. Useful when trading rate-sensitive tickers.</summary>
    public bool UseFomcMacro { get; set; } = true;

    public bool UseGrokTrending { get; set; } = false;
    public int GrokPollIntervalSeconds { get; set; } = 1800;

    /// <summary>If true, every bullish entry gets a second-opinion check from Grok (x_search + web_search)
    /// before the order is submitted. Conservative: only "approve" lets the buy through.
    /// Costs ~$0.01 per call. Requires Grok API key.</summary>
    public bool GrokConfirmationEnabled { get; set; } = false;

    /// <summary>If true, every bullish entry also gets verified by Claude with Anthropic's web_search tool.
    /// Asks Claude: "does this trade make sense given everything you can find online?" When BOTH Grok
    /// and Claude are enabled, both must approve before the buy. Costs ~$0.01-0.05 per call depending
    /// on model. Pro tier only.</summary>
    public bool ClaudeConfirmationEnabled { get; set; } = false;

    /// <summary>
    /// "Advisor mode" — when true, Claude still runs and its verdict is recorded in UserGateCalls,
    /// but a Veto/Caution NO LONGER blocks the trade. Used to measure Claude's would-have-been P&L
    /// impact against a Grok-only baseline. Buys that proceed despite Claude blocking are marked
    /// "[Claude shadow veto: REASON]" in the order reason so the Reports query can identify them.
    /// </summary>
    public bool ClaudeAdvisorMode { get; set; } = false;

    // ── Discovery (auto-find new tickers) ───────────────────────────────────
    public bool DiscoveryEnabled { get; set; } = true;
    public int DiscoveryExtractorIntervalSeconds { get; set; } = 300;
    public int DiscoveryBuzzThreshold { get; set; } = 2;
    public int DiscoveryBuzzWindowMinutes { get; set; } = 180;
    public int DiscoveryWatchlistTtlHours { get; set; } = 6;

    // ── Macro context (prediction-market odds fed into the AI) ──────────────
    public string MacroSource { get; set; } = "Manifold";   // "Manifold" | "Polymarket" | "Off"
    public int MacroPollIntervalSeconds { get; set; } = 600;

    // ── LLM provider ────────────────────────────────────────────────────────
    /// <summary>"Gemini" | "Anthropic" | "Llama" — which model classifies every headline.</summary>
    public string LlmProvider { get; set; } = "Gemini";
    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public string AnthropicModel { get; set; } = "claude-haiku-4-5";
    /// <summary>Groq-hosted model identifier. Default is Llama 3.3 70B — proven JSON output,
    /// no premature stops, no chain-of-thought tokens. Scout (Llama 4) has a strict-JSON-mode bug
    /// that truncates at ~46 chars; gpt-oss-* are reasoning models; llama-3.1-8b-instant is retired.
    /// 3.3-70b is the proven sweet spot for sentiment classification at modest volume.</summary>
    public string LlamaModel { get; set; } = "llama-3.3-70b-versatile";

    // ── Entry gates ─────────────────────────────────────────────────────────
    public double MinConfidence { get; set; } = 0.85;
    public int RequiredSignalCount { get; set; } = 2;
    public int ConfirmationWindowMinutes { get; set; } = 120;
    public bool EarningsBlackoutEnabled { get; set; } = true;
    public int EarningsBlackoutHours { get; set; } = 24;

    // ── Sizing + risk caps ──────────────────────────────────────────────────
    public double MaxPositionFraction { get; set; } = 0.025;
    public double MaxDailyLossFraction { get; set; } = 0.02;
    public int MaxTradesPerDay { get; set; } = 15;
    public int PollIntervalSeconds { get; set; } = 60;

    // ── Exits ───────────────────────────────────────────────────────────────
    /// <summary>"Hard" (fixed % stop), "Trailing" (peak-based only), "Both" (whichever fires first), or "None".</summary>
    public string StopLossType { get; set; } = "Both";
    public double StopLossPercent { get; set; } = 0.05;
    public double TakeProfitPercent { get; set; } = 0.0;
    public double TrailingStopPercent { get; set; } = 0.015;
    public double TrailingStopActivationPercent { get; set; } = 0.03;
    public int MaxHoldDays { get; set; } = 5;

    /// <summary>If false, the bot will NOT auto-sell a position based on a fresh bearish article.</summary>
    public bool BearishNewsExitsEnabled { get; set; } = true;
    public double BearishNewsMinConfidence { get; set; } = 0.80;

    /// <summary>Minutes after open during which stops/exits are suppressed (avoids the opening-gap spike).</summary>
    public int StopArmDelayMinutes { get; set; } = 30;

    // ── Personal trading rules (per-user differentiation) ──────────────────
    /// <summary>Tickers the bot must NEVER buy, even if they show up in the universe or watchlist.</summary>
    public string BlacklistedTickersCsv { get; set; } = "";

    /// <summary>If a headline contains any of these (case-insensitive), reject the trade outright.</summary>
    public string BlockedKeywordsCsv { get; set; } = "";

    /// <summary>If a headline contains any of these, treat the LLM verdict as 5% more confident.</summary>
    public string BoostKeywordsCsv { get; set; } = "";

    /// <summary>Don't OPEN new positions in the first N minutes after market open (avoids the opening-bell whiplash).</summary>
    public int NoTradeMinutesAfterOpen { get; set; } = 0;

    /// <summary>Don't OPEN new positions in the last N minutes before market close.</summary>
    public int NoTradeMinutesBeforeClose { get; set; } = 0;

    /// <summary>Don't allow mechanical exits (stop, trail, time) within N minutes of opening a position.</summary>
    public int MinHoldMinutes { get; set; } = 0;

    public string[] Blacklist() => Csv(BlacklistedTickersCsv).Select(t => t.ToUpperInvariant()).ToArray();
    public string[] BlockedKeywords() => Csv(BlockedKeywordsCsv).Select(t => t.ToLowerInvariant()).ToArray();
    public string[] BoostKeywords() => Csv(BoostKeywordsCsv).Select(t => t.ToLowerInvariant()).ToArray();

    private static IEnumerable<string> Csv(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Universe ────────────────────────────────────────────────────────────
    public string UniverseCsv { get; set; } = "AAPL,MSFT,NVDA,TSLA,AMD,META,GOOGL,AMZN,GLD,GDX,SLV,SIL,USO,XLE";

    public string[] Universe() => string.IsNullOrWhiteSpace(UniverseCsv)
        ? Array.Empty<string>()
        : UniverseCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Crypto universe (separate from equities). Alpaca crypto symbols use the SYMBOL/USD format
    /// (e.g. BTC/USD, ETH/USD, SOL/USD). Empty disables crypto scanning/trading.
    /// </summary>
    public string CryptoUniverseCsv { get; set; } = "BTC/USD,ETH/USD,SOL/USD";

    public string[] CryptoUniverse() => string.IsNullOrWhiteSpace(CryptoUniverseCsv)
        ? Array.Empty<string>()
        : CryptoUniverseCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
