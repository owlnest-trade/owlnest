using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Macro;

/// <summary>
/// Pulls active markets from Polymarket's free Gamma API, filters them by keyword for stock-trading
/// relevance (Fed, recession, geopolitical, BTC, etc.), and returns the top-N by volume.
/// </summary>
public sealed class PolymarketMacroProvider : IMacroProvider
{
    private readonly HttpClient _http;
    private readonly PolymarketOptions _opts;
    private readonly ILogger<PolymarketMacroProvider> _log;

    public string SourceName => "Polymarket";

    public PolymarketMacroProvider(
        HttpClient http,
        IOptions<PolymarketOptions> opts,
        ILogger<PolymarketMacroProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<MacroSnapshot> FetchAsync(CancellationToken ct)
    {
        var url = $"markets?active=true&closed=false&order=volumeNum&ascending=false&limit={_opts.FetchTopN}";

        GammaMarket[]? raw;
        try
        {
            raw = await _http.GetFromJsonAsync<GammaMarket[]>(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Polymarket fetch failed");
            return new MacroSnapshot(DateTimeOffset.UtcNow, Array.Empty<MacroMarket>());
        }

        if (raw is null || raw.Length == 0)
            return new MacroSnapshot(DateTimeOffset.UtcNow, Array.Empty<MacroMarket>());

        var filters = _opts.KeywordFilters.Select(k => k.ToLowerInvariant()).ToArray();
        var results = new List<MacroMarket>();

        foreach (var m in raw)
        {
            var question = m.Question ?? "";
            if (string.IsNullOrWhiteSpace(question)) continue;

            var lower = question.ToLowerInvariant();
            if (!filters.Any(f => lower.Contains(f))) continue;

            // outcomePrices is a JSON-encoded string array like "[\"0.78\",\"0.22\"]".
            var yesPrice = ParseFirstPrice(m.OutcomePrices);
            if (yesPrice is null) continue;

            DateTimeOffset? end = null;
            if (!string.IsNullOrEmpty(m.EndDate) && DateTimeOffset.TryParse(m.EndDate, out var parsed))
                end = parsed;

            var slug = m.Slug ?? m.Id ?? "";
            var marketUrl = $"https://polymarket.com/event/{Uri.EscapeDataString(slug)}";

            results.Add(new MacroMarket(
                Slug: slug,
                Question: question,
                YesPrice: yesPrice.Value,
                Volume: m.VolumeNum ?? 0,
                EndDate: end,
                Url: marketUrl));

            if (results.Count >= _opts.KeepTopN) break;
        }

        _log.LogInformation("Polymarket: {Raw} markets fetched → {Kept} matched keywords", raw.Length, results.Count);
        return new MacroSnapshot(DateTimeOffset.UtcNow, results);
    }

    private static double? ParseFirstPrice(string? outcomePricesJson)
    {
        // The field is sometimes the JSON string "[\"0.78\",\"0.22\"]", sometimes a real array.
        // We tolerate both forms.
        if (string.IsNullOrWhiteSpace(outcomePricesJson)) return null;
        try
        {
            var trimmed = outcomePricesJson.Trim();
            if (!trimmed.StartsWith("[")) return null;
            var inner = trimmed.TrimStart('[').TrimEnd(']');
            if (string.IsNullOrWhiteSpace(inner)) return null;
            var firstChunk = inner.Split(',')[0].Trim().Trim('"');
            return double.TryParse(firstChunk, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? v : null;
        }
        catch { return null; }
    }

    // --- Wire types (only the fields we use) -------------------------------------------------
    private sealed class GammaMarket
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("question")] public string? Question { get; set; }
        [JsonPropertyName("outcomePrices")] public string? OutcomePrices { get; set; }
        [JsonPropertyName("volumeNum")] public double? VolumeNum { get; set; }
        [JsonPropertyName("endDate")] public string? EndDate { get; set; }
    }
}
