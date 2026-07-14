using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Pulls the firehose Finnhub general-market news feed. Unlike the per-ticker provider, this is
/// the discovery input — we use the `related` tags to figure out WHICH tickers to investigate.
/// </summary>
public sealed class FinnhubMarketNewsProvider
{
    private readonly HttpClient _http;
    private readonly FinnhubOptions _opts;
    private readonly ILogger<FinnhubMarketNewsProvider> _log;

    public FinnhubMarketNewsProvider(
        HttpClient http,
        IOptions<FinnhubOptions> opts,
        ILogger<FinnhubMarketNewsProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Returns the entire latest market-news batch from Finnhub (no time filter). The caller's
    /// BuzzTracker handles deduplication by article ID and prunes mentions older than its window,
    /// so feeding it the same firehose every tick is safe and correct.
    /// </summary>
    public async Task<IReadOnlyList<MarketNewsItem>> GetGeneralNewsAsync(CancellationToken ct)
    {
        var url = $"news?category=general&token={Uri.EscapeDataString(_opts.ApiKey)}";

        FinnhubArticle[]? raw;
        try
        {
            raw = await _http.GetFromJsonAsync<FinnhubArticle[]>(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Finnhub general-news fetch failed");
            return Array.Empty<MarketNewsItem>();
        }

        if (raw is null || raw.Length == 0)
            return Array.Empty<MarketNewsItem>();

        var results = new List<MarketNewsItem>(raw.Length);
        foreach (var a in raw)
        {
            if (string.IsNullOrWhiteSpace(a.Headline)) continue;

            // `related` is a comma-separated list of tickers Finnhub already extracted for us.
            var tickers = string.IsNullOrWhiteSpace(a.Related)
                ? Array.Empty<string>()
                : a.Related.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Where(t => IsLikelyTicker(t))
                           .Select(t => t.ToUpperInvariant())
                           .Distinct()
                           .ToArray();

            results.Add(new MarketNewsItem(
                Id: a.Id.ToString(),
                Tickers: tickers,
                Headline: a.Headline ?? "",
                Source: a.Source ?? "",
                Url: a.Url ?? "",
                PublishedAt: DateTimeOffset.FromUnixTimeSeconds(a.Datetime)));
        }

        var withTickers = results.Count(r => r.Tickers.Length > 0);
        _log.LogInformation("Discovery feed: {Raw} raw articles, {WithTickers} carry ticker tags",
            raw.Length, withTickers);
        return results;
    }

    private static bool IsLikelyTicker(string s)
    {
        // US tickers are 1–5 uppercase letters, sometimes with a dot for class shares (BRK.B).
        // This drops garbage like full company names that occasionally appear in `related`.
        if (string.IsNullOrWhiteSpace(s) || s.Length > 6) return false;
        foreach (var c in s)
        {
            if (!(char.IsLetter(c) || c == '.' || c == '-')) return false;
        }
        return true;
    }

    private sealed class FinnhubArticle
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("datetime")] public long Datetime { get; set; }
        [JsonPropertyName("headline")] public string? Headline { get; set; }
        [JsonPropertyName("related")] public string? Related { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
