namespace TradingBot.Services.Discovery;

/// <summary>
/// One article from the market-wide news feed (not pre-filtered to a ticker).
/// The Tickers array is parsed from Finnhub's `related` field.
/// </summary>
public sealed record MarketNewsItem(
    string Id,
    string[] Tickers,
    string Headline,
    string Source,
    string Url,
    DateTimeOffset PublishedAt);
