using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.Shared;

/// <summary>
/// Process-wide ticker→CIK lookup for SEC EDGAR. The full company-tickers.json is ~1MB and
/// covers every US-listed issuer, so we load it once at first use and keep it in memory for
/// the lifetime of the process. Shared across all users — there's no user-specific data here.
/// </summary>
public sealed class SecCikCache
{
    private const string CompanyTickersUrl = "https://www.sec.gov/files/company_tickers.json";

    private readonly ILogger<SecCikCache> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _map;

    public SecCikCache(ILogger<SecCikCache> log) { _log = log; }

    public async Task<string?> GetCikAsync(string ticker, string contactEmail, CancellationToken ct)
    {
        if (_map is null)
        {
            await _gate.WaitAsync(ct);
            try { if (_map is null) await LoadAsync(contactEmail, ct); }
            finally { _gate.Release(); }
        }
        return _map!.TryGetValue(ticker.ToUpperInvariant(), out var cik) ? cik : null;
    }

    private async Task LoadAsync(string contactEmail, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // SEC requires a real User-Agent with a contact. We use whichever user triggered the load.
        var ua = string.IsNullOrWhiteSpace(contactEmail) ? "owlnest.trade@example.com" : contactEmail;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"owlnest.trade/1.0 ({ua})");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation("SEC CIK cache: downloading company-tickers map");
        try
        {
            var raw = await http.GetFromJsonAsync<Dictionary<string, TickerEntry>>(CompanyTickersUrl, ct);
            if (raw is null) { _map = new(0); return; }
            var map = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in raw.Values)
            {
                if (string.IsNullOrWhiteSpace(e.Ticker)) continue;
                map[e.Ticker.ToUpperInvariant()] = e.Cik.ToString("D10");
            }
            _map = map;
            _log.LogInformation("SEC CIK cache: loaded {N} ticker→CIK mappings", _map.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SEC CIK cache load failed; SEC filings will return empty");
            _map = new(0);
        }
    }

    private sealed class TickerEntry
    {
        [JsonPropertyName("cik_str")] public long Cik { get; set; }
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    }
}
