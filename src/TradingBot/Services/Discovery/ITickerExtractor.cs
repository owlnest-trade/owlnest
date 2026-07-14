namespace TradingBot.Services.Discovery;

/// <summary>
/// LLM-backed batch ticker extractor. Takes the firehose of market news items whose Tickers[]
/// is empty and returns a re-stamped list with tickers identified for each.
/// Implementations should be tolerant of API failures and return the input unchanged on error.
/// </summary>
public interface ITickerExtractor
{
    Task<IReadOnlyList<MarketNewsItem>> ExtractAsync(
        IReadOnlyList<MarketNewsItem> items,
        CancellationToken ct);
}
