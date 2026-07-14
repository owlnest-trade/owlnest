using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.UserBot;

/// <summary>
/// Per-user earnings calendar backed by Finnhub. Refreshes the next-earnings date for each ticker
/// in the user's universe every 6 hours; UserBotInstance asks "has earnings within ±N hours?" before
/// approving a buy. Skipping trades around earnings keeps us out of overnight gap risk.
/// </summary>
public sealed class UserEarningsCalendar
{
    private readonly HttpClient _http;
    private readonly string _finnhubKey;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    public UserEarningsCalendar(HttpClient http, string finnhubKey, ILogger log)
    {
        _http = http;
        _finnhubKey = finnhubKey;
        _log = log;
    }

    public bool HasUpcomingEarnings(string ticker, DateTimeOffset now, int hours)
    {
        if (!_cache.TryGetValue(ticker, out var next) || next is null) return false;
        return Math.Abs((next.Value - now).TotalHours) <= hours;
    }

    public DateTimeOffset? NextEarnings(string ticker) =>
        _cache.TryGetValue(ticker, out var v) ? v : null;

    /// <summary>Refresh every 6h, no more. Call on every tick — cheap if cached.</summary>
    public async Task RefreshIfStaleAsync(IEnumerable<string> tickers, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastRefresh < RefreshInterval) return;
        _lastRefresh = DateTimeOffset.UtcNow;

        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-1).ToString("yyyy-MM-dd");
        var to = today.AddDays(60).ToString("yyyy-MM-dd");

        foreach (var t in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) return;
            await FetchAsync(t, from, to, ct);
            try { await Task.Delay(150, ct); } catch (OperationCanceledException) { return; }
        }
        _log.LogInformation("Earnings calendar refreshed: {Total} tickers, {Upcoming} have upcoming earnings",
            _cache.Count, _cache.Count(kv => kv.Value is not null));
    }

    private async Task FetchAsync(string ticker, string from, string to, CancellationToken ct)
    {
        var url = $"calendar/earnings?from={from}&to={to}&symbol={Uri.EscapeDataString(ticker)}&token={Uri.EscapeDataString(_finnhubKey)}";
        Response? body;
        try { body = await _http.GetFromJsonAsync<Response>(url, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Earnings fetch failed for {Ticker}", ticker); return; }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? nextUtc = null;
        foreach (var e in body?.EarningsCalendar ?? Array.Empty<Entry>())
        {
            if (string.IsNullOrEmpty(e.Date) || !DateTime.TryParse(e.Date, out var date)) continue;
            // Map session code to approx UTC (ET + 4h during EDT, close enough for a ±24h gate).
            var hourEt = (e.Hour ?? "amc").ToLowerInvariant() switch { "bmo" => 8, "dmh" => 12, _ => 16 };
            var utc = new DateTimeOffset(date.Year, date.Month, date.Day, hourEt + 4, 0, 0, TimeSpan.Zero);
            if (utc < now.AddDays(-1)) continue;
            if (nextUtc is null || utc < nextUtc) nextUtc = utc;
        }
        _cache[ticker] = nextUtc;
    }

    private sealed class Response
    {
        [JsonPropertyName("earningsCalendar")] public Entry[]? EarningsCalendar { get; set; }
    }
    private sealed class Entry
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("hour")] public string? Hour { get; set; }
    }
}
