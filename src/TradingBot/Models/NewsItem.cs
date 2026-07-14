namespace TradingBot.Models;

/// <summary>
/// Normalized news article. Whatever provider we use (Finnhub, Alpha Vantage, ...) should map into this shape.
/// </summary>
public sealed record NewsItem(
    string Id,
    string Ticker,
    string Headline,
    string Summary,
    string Source,
    string Url,
    DateTimeOffset PublishedAt);
