using TradingBot.Models;

namespace TradingBot.Services.Sentiment;

public interface ISentimentAnalyzer
{
    /// <summary>
    /// Send a single news item to the model and return its structured verdict.
    /// Pass <paramref name="macroContext"/> to prepend a prediction-market preamble to the
    /// user message (Claude uses it to weight actionability when the article is macro-sensitive).
    /// Returns null if the call failed (network, parse error, rate limit). Caller should
    /// treat null as "skip this item".
    /// </summary>
    Task<SentimentResult?> AnalyzeAsync(NewsItem news, string? macroContext, CancellationToken ct);
}
