using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.News;

public sealed class FinnhubNewsProvider : INewsProvider
{
    private readonly HttpClient _http;
    private readonly FinnhubOptions _opts;
    private readonly ILogger<FinnhubNewsProvider> _log;

    public FinnhubNewsProvider(
        HttpClient http,
        IOptions<FinnhubOptions> opts,
        ILogger<FinnhubNewsProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<NewsItem>> GetRecentNewsAsync(
        string ticker,
        DateTimeOffset since,
        CancellationToken ct)
    {
        // Finnhub's /company-news only takes YYYY-MM-DD date params. We pull today (UTC)
        // and filter client-side using the article's exact timestamp, so callers always get
        // strictly post-`since` items.
        var nowUtc = DateTimeOffset.UtcNow;
        var fromDate = since.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var toDate = nowUtc.UtcDateTime.Date.ToString("yyyy-MM-dd");

        var url = $"company-news?symbol={Uri.EscapeDataString(ticker)}&from={fromDate}&to={toDate}&token={Uri.EscapeDataString(_opts.ApiKey)}";

        FinnhubArticle[]? raw;
        try
        {
            raw = await _http.GetFromJsonAsync<FinnhubArticle[]>(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Finnhub news fetch failed for {Ticker}", ticker);
            return Array.Empty<NewsItem>();
        }

        var rawCount = raw?.Length ?? 0;
        if (raw is null || rawCount == 0)
        {
            _log.LogDebug("Finnhub returned 0 raw articles for {Ticker}", ticker);
            return Array.Empty<NewsItem>();
        }

        var sinceUnix = since.ToUnixTimeSeconds();
        var results = new List<NewsItem>(raw.Length);
        foreach (var a in raw)
        {
            if (a.Datetime <= sinceUnix) continue;
            if (string.IsNullOrWhiteSpace(a.Headline)) continue;

            results.Add(new NewsItem(
                Id: a.Id.ToString(),
                Ticker: ticker,
                Headline: a.Headline ?? "",
                Summary: a.Summary ?? "",
                Source: a.Source ?? "",
                Url: a.Url ?? "",
                PublishedAt: DateTimeOffset.FromUnixTimeSeconds(a.Datetime)));
        }

        _log.LogDebug("Finnhub returned {Fresh}/{Raw} fresh articles for {Ticker} since {Since:o}",
            results.Count, rawCount, ticker, since);

        return results;
    }

    // Wire shape from Finnhub. Only the fields we use.
    private sealed class FinnhubArticle
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("datetime")] public long Datetime { get; set; }
        [JsonPropertyName("headline")] public string? Headline { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
