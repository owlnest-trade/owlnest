using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.Shared;

public sealed record MacroMarket(string Question, double YesPrice, double Volume, string Url);
public sealed record MacroSnapshot(DateTimeOffset AtUtc, IReadOnlyList<MacroMarket> Markets);

/// <summary>
/// Shared singleton — polls Manifold every 10 minutes and exposes the latest macro snapshot.
/// All users with MacroSource = "Manifold" read from this cache, so we make one set of Manifold
/// API calls regardless of how many users are logged in.
/// </summary>
public sealed class ManifoldFeed : BackgroundService
{
    private static readonly string[] SearchTerms = new[]
    {
        "recession 2026", "Fed rate", "CPI", "S&P 500", "Trump", "China tariff", "Russia Ukraine", "Israel"
    };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly ILogger<ManifoldFeed> _log;
    private MacroSnapshot _snapshot = new(DateTimeOffset.MinValue, Array.Empty<MacroMarket>());

    public ManifoldFeed(ILogger<ManifoldFeed> log)
    {
        _log = log;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.manifold.markets/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public MacroSnapshot Latest => _snapshot;

    /// <summary>One-paragraph plain-English summary suitable for prompt injection.</summary>
    public string PromptSummary()
    {
        if (_snapshot.Markets.Count == 0) return "";
        var sb = new StringBuilder("Current prediction-market odds (Manifold): ");
        var top = _snapshot.Markets.OrderByDescending(m => m.Volume).Take(6).ToList();
        for (int i = 0; i < top.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append('"').Append(top[i].Question.Trim()).Append("\" ").Append((top[i].YesPrice * 100).ToString("F0")).Append("% YES");
        }
        sb.Append('.');
        return sb.ToString();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial fetch with a short delay so app startup isn't blocked by network.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Manifold poll failed"); }
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var byId = new Dictionary<string, ManifoldMarket>(StringComparer.Ordinal);
        foreach (var term in SearchTerms)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var page = await _http.GetFromJsonAsync<ManifoldMarket[]>(
                    $"v0/search-markets?term={Uri.EscapeDataString(term)}&limit=10&sort=score", ct);
                if (page is null) continue;
                foreach (var m in page)
                    if (!string.IsNullOrEmpty(m.Id)) byId[m.Id!] = m;
            }
            catch (Exception ex) { _log.LogDebug(ex, "Manifold term '{T}' failed", term); }
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { return; }
        }

        var markets = byId.Values
            .Where(m => !m.IsResolved
                && string.Equals(m.OutcomeType, "BINARY", StringComparison.OrdinalIgnoreCase)
                && m.Probability is >= 0 and <= 1
                && (m.TotalLiquidity ?? 0) >= 50)
            .OrderByDescending(m => m.Volume ?? 0)
            .Take(12)
            .Select(m => new MacroMarket(
                Question: m.Question ?? "",
                YesPrice: m.Probability ?? 0,
                Volume: m.Volume ?? 0,
                Url: m.Url ?? $"https://manifold.markets/market/{m.Slug ?? m.Id}"))
            .ToList();

        _snapshot = new MacroSnapshot(DateTimeOffset.UtcNow, markets);
        _log.LogInformation("Manifold: refreshed {N} markets", markets.Count);
    }

    public override void Dispose() { _http.Dispose(); base.Dispose(); }

    private sealed class ManifoldMarket
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("question")] public string? Question { get; set; }
        [JsonPropertyName("outcomeType")] public string? OutcomeType { get; set; }
        [JsonPropertyName("probability")] public double? Probability { get; set; }
        [JsonPropertyName("volume")] public double? Volume { get; set; }
        [JsonPropertyName("totalLiquidity")] public double? TotalLiquidity { get; set; }
        [JsonPropertyName("isResolved")] public bool IsResolved { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
