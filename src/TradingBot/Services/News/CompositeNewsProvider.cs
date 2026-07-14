using Microsoft.Extensions.Logging;
using TradingBot.Models;

namespace TradingBot.Services.News;

/// <summary>
/// Fan-out provider. Calls every underlying source in parallel and returns the union.
/// A failure in one source must not block the others — each underlying provider already
/// returns an empty list on failure, so we just concatenate.
/// </summary>
public sealed class CompositeNewsProvider : INewsProvider
{
    private readonly FinnhubNewsProvider _finnhub;
    private readonly SecEdgarNewsProvider _sec;
    private readonly ILogger<CompositeNewsProvider> _log;

    public CompositeNewsProvider(
        FinnhubNewsProvider finnhub,
        SecEdgarNewsProvider sec,
        ILogger<CompositeNewsProvider> log)
    {
        _finnhub = finnhub;
        _sec = sec;
        _log = log;
    }

    public async Task<IReadOnlyList<NewsItem>> GetRecentNewsAsync(
        string ticker,
        DateTimeOffset since,
        CancellationToken ct)
    {
        var finnhubTask = _finnhub.GetRecentNewsAsync(ticker, since, ct);
        var secTask = _sec.GetRecentNewsAsync(ticker, since, ct);
        await Task.WhenAll(finnhubTask, secTask);

        var combined = new List<NewsItem>(finnhubTask.Result.Count + secTask.Result.Count);
        combined.AddRange(finnhubTask.Result);
        combined.AddRange(secTask.Result);

        if (combined.Count > 0)
        {
            _log.LogDebug("Composite for {Ticker}: {Finnhub} Finnhub + {Sec} SEC = {Total}",
                ticker, finnhubTask.Result.Count, secTask.Result.Count, combined.Count);
        }
        return combined;
    }
}
