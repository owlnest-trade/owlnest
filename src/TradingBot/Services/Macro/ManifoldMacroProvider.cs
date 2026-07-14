using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Macro;

/// <summary>
/// Manifold Markets data source. Searches each configured keyword via /v0/search-markets,
/// dedupes, filters to BINARY markets with non-trivial liquidity, sorts by volume,
/// returns the top N. Free, no-auth, globally accessible.
/// </summary>
public sealed class ManifoldMacroProvider : IMacroProvider
{
    private readonly HttpClient _http;
    private readonly ManifoldOptions _opts;
    private readonly ILogger<ManifoldMacroProvider> _log;

    public string SourceName => "Manifold Markets";

    public ManifoldMacroProvider(
        HttpClient http,
        IOptions<ManifoldOptions> opts,
        ILogger<ManifoldMacroProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<MacroSnapshot> FetchAsync(CancellationToken ct)
    {
        var byId = new Dictionary<string, ManifoldMarket>(StringComparer.Ordinal);
        int totalSearched = 0;

        foreach (var term in _opts.SearchTerms)
        {
            if (ct.IsCancellationRequested) break;
            var url = $"v0/search-markets?term={Uri.EscapeDataString(term)}&limit={_opts.SearchLimitPerKeyword}&sort=score";
            ManifoldMarket[]? page;
            try
            {
                page = await _http.GetFromJsonAsync<ManifoldMarket[]>(url, ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Manifold search failed for term '{Term}'", term);
                continue;
            }
            if (page is null) continue;
            totalSearched += page.Length;

            foreach (var m in page)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                byId[m.Id] = m;   // overwrites on duplicate, fine
            }

            // Small polite delay between keyword searches.
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { break; }
        }

        var filtered = byId.Values
            .Where(m => !m.IsResolved)
            .Where(m => string.Equals(m.OutcomeType, "BINARY", StringComparison.OrdinalIgnoreCase))
            .Where(m => m.Probability is >= 0 and <= 1)
            .Where(m => (m.TotalLiquidity ?? 0) >= _opts.MinLiquidity)
            .OrderByDescending(m => m.Volume ?? 0)
            .Take(_opts.KeepTopN)
            .ToList();

        var results = filtered.Select(m => new MacroMarket(
            Slug: m.Slug ?? m.Id ?? "",
            Question: m.Question ?? "(no question)",
            YesPrice: m.Probability ?? 0,
            Volume: m.Volume ?? 0,
            EndDate: m.CloseTime is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(m.CloseTime.Value) : null,
            Url: m.Url ?? $"https://manifold.markets/market/{m.Slug ?? m.Id}"
        )).ToList();

        _log.LogInformation("Manifold: {Searched} results from {Terms} terms → {Unique} unique → {Kept} after filter",
            totalSearched, _opts.SearchTerms.Length, byId.Count, results.Count);

        return new MacroSnapshot(DateTimeOffset.UtcNow, results);
    }

    // --- Wire types (subset of Manifold market shape) ---------------------------------------
    private sealed class ManifoldMarket
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("question")] public string? Question { get; set; }
        [JsonPropertyName("outcomeType")] public string? OutcomeType { get; set; }
        [JsonPropertyName("probability")] public double? Probability { get; set; }
        [JsonPropertyName("volume")] public double? Volume { get; set; }
        [JsonPropertyName("totalLiquidity")] public double? TotalLiquidity { get; set; }
        [JsonPropertyName("closeTime")] public long? CloseTime { get; set; }
        [JsonPropertyName("isResolved")] public bool IsResolved { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
