using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.News;

/// <summary>
/// Singleton cache mapping ticker → 10-digit-padded CIK. The SEC publishes the entire universe
/// in a single JSON file (~1MB) which is small enough to keep in memory for the process lifetime.
/// </summary>
public sealed class CikCache
{
    private readonly SecEdgarOptions _opts;
    private readonly ILogger<CikCache> _log;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private Dictionary<string, string>? _tickerToCik;

    public CikCache(IOptions<SecEdgarOptions> opts, ILogger<CikCache> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Returns the 10-digit padded CIK string for the ticker, or null if unknown.</summary>
    public async Task<string?> GetCikAsync(string ticker, CancellationToken ct)
    {
        if (_tickerToCik is null)
        {
            await _loadGate.WaitAsync(ct);
            try { if (_tickerToCik is null) await LoadAsync(ct); }
            finally { _loadGate.Release(); }
        }
        return _tickerToCik!.TryGetValue(ticker.ToUpperInvariant(), out var cik) ? cik : null;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        // One-shot HttpClient — this runs at most once per process lifetime.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TradingBot/1.0 ({_opts.ContactEmail})");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation("Downloading SEC company-tickers map from {Url}", _opts.CompanyTickersUrl);
        try
        {
            // Wire format: { "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc."}, "1": {...}, ... }
            var raw = await http.GetFromJsonAsync<Dictionary<string, TickerEntry>>(_opts.CompanyTickersUrl, ct);
            if (raw is null)
            {
                _log.LogWarning("SEC company-tickers returned null; SEC provider will return empty results");
                _tickerToCik = new Dictionary<string, string>(0);
                return;
            }

            var map = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in raw.Values)
            {
                if (string.IsNullOrWhiteSpace(entry.Ticker)) continue;
                // SEC's submissions endpoint wants the CIK left-padded to 10 digits.
                map[entry.Ticker.ToUpperInvariant()] = entry.Cik.ToString("D10");
            }
            _tickerToCik = map;
            _log.LogInformation("Loaded {Count} ticker→CIK mappings", _tickerToCik.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load SEC company-tickers map; SEC provider will return empty results");
            _tickerToCik = new Dictionary<string, string>(0);
        }
    }

    private sealed class TickerEntry
    {
        [JsonPropertyName("cik_str")] public long Cik { get; set; }
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
}
