namespace TradingBot.Options;

/// <summary>
/// Dynamic-universe discovery: scan all market news, count ticker mentions in a rolling window,
/// promote "buzzy" tickers onto a dynamic watchlist for the next N hours, then drop them.
/// This lets the bot react to catalysts on tickers you never pre-picked.
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>Master switch. When false, only the fixed Trading:Universe is scanned.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum mentions in <see cref="BuzzWindowMinutes"/> required to promote a ticker.</summary>
    public int BuzzThreshold { get; set; } = 3;

    /// <summary>Rolling-window length, in minutes, over which mentions are counted.</summary>
    public int BuzzWindowMinutes { get; set; } = 60;

    /// <summary>How long a promoted ticker stays on the watchlist before being dropped.</summary>
    public int WatchlistTtlHours { get; set; } = 6;

    /// <summary>Hard cap on dynamic watchlist size to prevent runaway scanning costs.</summary>
    public int MaxWatchlistSize { get; set; } = 30;

    /// <summary>How many market-wide articles to pull per discovery tick (Finnhub returns up to ~200).</summary>
    public int MarketNewsLookbackMinutes { get; set; } = 30;

    /// <summary>
    /// Minimum seconds between Claude ticker-extractor calls. The discovery feed gets re-pulled on
    /// every worker tick, but the LLM extractor (batched, ~$0.01 per call) runs at most this often.
    /// Default 300 = every 5 minutes. Increase to lower cost; decrease to react faster to new tickers.
    /// </summary>
    public int ExtractorMinIntervalSeconds { get; set; } = 300;
}
