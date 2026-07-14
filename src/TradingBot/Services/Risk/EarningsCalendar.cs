using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Risk;

/// <summary>
/// Finnhub-backed earnings calendar. Pulls scheduled earnings dates+times per ticker, caches by
/// ticker → next earnings UTC. The risk gate consults this before approving any buy — if a
/// ticker has earnings within ±N hours, we reject the trade. Gap risk on the print is too high
/// to bet against with a 75% sentiment signal.
/// </summary>
public sealed class EarningsCalendar
{
    private readonly HttpClient _http;
    private readonly FinnhubOptions _opts;
    private readonly ILogger<EarningsCalendar> _log;

    // ticker → next scheduled earnings (UTC). Null sentinel means "checked, none upcoming".
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public EarningsCalendar(
        HttpClient http,
        IOptions<FinnhubOptions> opts,
        ILogger<EarningsCalendar> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>Returns the next scheduled earnings time for a ticker, or null if none / not cached.</summary>
    public DateTimeOffset? NextEarnings(string ticker) =>
        _cache.TryGetValue(ticker, out var t) ? t : null;

    /// <summary>True if any earnings is scheduled within ±<paramref name="hours"/> of <paramref name="now"/>.</summary>
    public bool HasUpcomingEarnings(string ticker, DateTimeOffset now, int hours)
    {
        var next = NextEarnings(ticker);
        if (next is null) return false;
        var diff = Math.Abs((next.Value - now).TotalHours);
        return diff <= hours;
    }

    /// <summary>Refresh the cache for every ticker in the universe. Called by EarningsCalendarPoller.</summary>
    public async Task RefreshAsync(IEnumerable<string> tickers, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var fromStr = today.AddDays(-1).ToString("yyyy-MM-dd"); // include "today already" case
        var toStr = today.AddDays(60).ToString("yyyy-MM-dd");   // next ~2 months

        var refreshed = 0;
        foreach (var ticker in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            await FetchOneAsync(ticker, fromStr, toStr, ct);
            refreshed++;
            // Polite delay — Finnhub free tier is 60 req/min.
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { return; }
        }

        _log.LogInformation("EarningsCalendar refreshed {Count} tickers ({Upcoming} have upcoming earnings)",
            refreshed, _cache.Count(kv => kv.Value is not null));
    }

    private async Task FetchOneAsync(string ticker, string from, string to, CancellationToken ct)
    {
        var url = $"calendar/earnings?from={from}&to={to}&symbol={Uri.EscapeDataString(ticker)}&token={Uri.EscapeDataString(_opts.ApiKey)}";

        EarningsCalendarResponse? body;
        try
        {
            body = await _http.GetFromJsonAsync<EarningsCalendarResponse>(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Earnings fetch failed for {Ticker}", ticker);
            return;
        }

        var entries = body?.EarningsCalendar ?? Array.Empty<EarningsEntry>();
        var now = DateTimeOffset.UtcNow;

        DateTimeOffset? nextUtc = null;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Date)) continue;
            if (!DateTime.TryParse(e.Date, out var date)) continue;

            // Hour code → approximate ET time. ET is UTC-4 during EDT (May–Nov), UTC-5 EST.
            // Close-enough estimate; we're only using it for a ±24h gate so minor TZ slop is fine.
            var hourEt = (e.Hour ?? "amc").ToLowerInvariant() switch
            {
                "bmo" => 8,    // before market open ≈ 8:30am ET
                "dmh" => 12,   // during market hours ≈ noon ET
                _      => 16,  // amc / unknown ≈ 4:30pm ET
            };
            // EDT in this period — convert assumed ET to UTC by adding 4h.
            var utc = new DateTimeOffset(date.Year, date.Month, date.Day, hourEt + 4, 0, 0, TimeSpan.Zero);
            if (utc < now.AddDays(-1)) continue; // skip already-happened
            if (nextUtc is null || utc < nextUtc) nextUtc = utc;
        }

        _cache[ticker] = nextUtc;
    }

    private sealed class EarningsCalendarResponse
    {
        [JsonPropertyName("earningsCalendar")] public EarningsEntry[]? EarningsCalendar { get; set; }
    }
    private sealed class EarningsEntry
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("hour")] public string? Hour { get; set; }
        [JsonPropertyName("symbol")] public string? Symbol { get; set; }
    }
}
