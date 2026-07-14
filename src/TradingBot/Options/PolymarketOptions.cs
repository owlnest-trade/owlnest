namespace TradingBot.Options;

/// <summary>
/// Polymarket prediction-market integration. Reads top-volume markets from the free Polymarket
/// Gamma API (no auth) and filters them by keyword to surface macro/political markets that have
/// stock-trading relevance (Fed decisions, recession, CPI, geopolitical, Bitcoin price targets).
/// </summary>
public sealed class PolymarketOptions
{
    public const string SectionName = "Polymarket";

    /// <summary>Master switch. When false, no polls and no macro tile.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Polymarket Gamma API base URL (free, no auth).</summary>
    public string BaseUrl { get; set; } = "https://gamma-api.polymarket.com/";

    /// <summary>How often to poll. Default 600s (10 min) — Polymarket odds move slowly enough.</summary>
    public int PollIntervalSeconds { get; set; } = 600;

    /// <summary>How many top-volume markets to fetch per poll before filtering.</summary>
    public int FetchTopN { get; set; } = 200;

    /// <summary>How many filtered markets to keep / show on the dashboard.</summary>
    public int KeepTopN { get; set; } = 20;

    /// <summary>
    /// Lowercase substrings that — if any appear in a market's question — mean the market is relevant.
    /// Keep this curated for signal quality. Defaults cover Fed/macro/geopolitical/crypto.
    /// </summary>
    public string[] KeywordFilters { get; set; } =
    [
        "fed", "fomc", "rate cut", "rate hike", "interest rate",
        "recession", "gdp", "cpi", "inflation", "jobs report", "unemployment",
        "bitcoin", "btc", "ethereum", "eth",
        "iran", "israel", "russia", "ukraine", "china", "taiwan", "north korea",
        "election", "trump", "biden", "harris", "presidential",
        "oil price", "opec", "wti", "brent",
        "gold", "silver"
    ];
}
