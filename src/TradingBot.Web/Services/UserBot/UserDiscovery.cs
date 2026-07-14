using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.UserBot;

public sealed record WatchEntry(string Ticker, DateTimeOffset PromotedAt, DateTimeOffset ExpiresAt, int Buzz, string? Reason);
public sealed record MarketArticle(string Id, string Headline, string Source, string Url, string[] Tickers, DateTimeOffset PublishedAt);

/// <summary>
/// Per-user discovery state. Tracks ticker mention buzz over a rolling window from the Finnhub
/// firehose, promotes high-buzz tickers onto a TTL'd dynamic watchlist, and lets the user's
/// trading loop additionally scan those tickers each tick.
/// </summary>
public sealed class UserBuzzTracker
{
    private readonly int _windowMinutes;
    private readonly int _threshold;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _mentions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ingestedIds = new(StringComparer.Ordinal);

    public UserBuzzTracker(int windowMinutes, int threshold)
    {
        _windowMinutes = Math.Max(15, windowMinutes);
        _threshold = Math.Max(1, threshold);
    }

    public void Ingest(MarketArticle article)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_ingestedIds.Add(article.Id)) return;
            foreach (var t in article.Tickers)
            {
                if (!_mentions.TryGetValue(t, out var list))
                {
                    list = new List<DateTimeOffset>(4);
                    _mentions[t] = list;
                }
                list.Add(now);
            }
        }
    }

    public void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-_windowMinutes);
        lock (_gate)
        {
            foreach (var k in _mentions.Keys.ToList())
            {
                _mentions[k].RemoveAll(ts => ts < cutoff);
                if (_mentions[k].Count == 0) _mentions.Remove(k);
            }
            if (_ingestedIds.Count > 5000) _ingestedIds.Clear();
        }
    }

    public IReadOnlyList<(string Ticker, int Score)> Buzzy()
    {
        lock (_gate)
        {
            return _mentions.Where(kv => kv.Value.Count >= _threshold)
                .Select(kv => (kv.Key, kv.Value.Count))
                .OrderByDescending(x => x.Count).ToList();
        }
    }
}

public sealed class UserWatchlist
{
    private readonly int _ttlHours;
    private readonly int _maxSize;
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public UserWatchlist(int ttlHours, int maxSize = 25)
    {
        _ttlHours = Math.Max(1, ttlHours);
        _maxSize = Math.Max(5, maxSize);
    }

    /// <summary>
    /// Add tickers to the watchlist. Returns the subset that were NEWLY promoted (not already on
    /// the list — re-promotions just extend TTL silently). Callers use this to write
    /// UserWatchlistEvent audit rows only for fresh promotions.
    /// </summary>
    public IReadOnlyList<(string Ticker, int Score, string? Reason)> PromoteMany(
        IEnumerable<(string Ticker, int Score, string? Reason)> items,
        IReadOnlyCollection<string> exclude)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(_ttlHours);
        var excl = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var newlyPromoted = new List<(string, int, string?)>();
        lock (_gate)
        {
            foreach (var (ticker, score, reason) in items)
            {
                if (excl.Contains(ticker)) continue;
                if (_entries.TryGetValue(ticker, out var existing))
                {
                    _entries[ticker] = existing with { ExpiresAt = expires, Buzz = Math.Max(existing.Buzz, score), Reason = reason ?? existing.Reason };
                    continue;
                }
                if (_entries.Count >= _maxSize)
                {
                    var weakest = _entries.Values.OrderBy(e => e.Buzz).First();
                    if (score <= weakest.Buzz) continue;
                    _entries.Remove(weakest.Ticker);
                }
                _entries[ticker] = new WatchEntry(ticker, now, expires, score, reason);
                newlyPromoted.Add((ticker, score, reason));
            }
        }
        return newlyPromoted;
    }

    public void Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values.Where(e => e.ExpiresAt <= now).ToList())
                _entries.Remove(e.Ticker);
        }
    }

    public IReadOnlyList<string> ActiveTickers() { lock (_gate) return _entries.Keys.ToList(); }
    public IReadOnlyList<WatchEntry> ActiveEntries() { lock (_gate) return _entries.Values.OrderByDescending(e => e.Buzz).ToList(); }
}

/// <summary>
/// Per-user Finnhub general-news firehose. Pulls every poll and feeds the buzz tracker.
/// Each user uses their own Finnhub key (and quota).
/// </summary>
public sealed class UserFinnhubFirehose
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly ILogger _log;

    public UserFinnhubFirehose(HttpClient http, string key, ILogger log)
    { _http = http; _key = key; _log = log; }

    public async Task<IReadOnlyList<MarketArticle>> GetAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _http.GetFromJsonAsync<FinnhubArticle[]>(
                $"news?category=general&token={Uri.EscapeDataString(_key)}", ct);
            if (raw is null) return Array.Empty<MarketArticle>();
            return raw.Where(a => !string.IsNullOrWhiteSpace(a.Headline))
                .Select(a =>
                {
                    var tickers = string.IsNullOrWhiteSpace(a.Related)
                        ? Array.Empty<string>()
                        : a.Related.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(IsLikelyTicker).Select(t => t.ToUpperInvariant()).Distinct().ToArray();
                    return new MarketArticle(
                        Id: a.Id.ToString(), Headline: a.Headline ?? "", Source: a.Source ?? "",
                        Url: a.Url ?? "", Tickers: tickers,
                        PublishedAt: DateTimeOffset.FromUnixTimeSeconds(a.Datetime));
                }).ToList();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Firehose fetch failed"); return Array.Empty<MarketArticle>(); }
    }

    private static bool IsLikelyTicker(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 6) return false;
        foreach (var c in s) if (!(char.IsLetter(c) || c == '.' || c == '-')) return false;
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

/// <summary>
/// Per-user Gemini-driven ticker extractor for headlines that Finnhub didn't tag. Runs in batches,
/// one Gemini Flash call per batch — free under the daily quota. Returns the same article list
/// with tickers backfilled.
/// </summary>
public sealed class UserGeminiExtractor
{
    private const string SystemPrompt = """
        You are a financial news ticker classifier. You will be given a numbered list of headlines.
        For each one, identify which US-listed publicly traded stock(s) the headline is MEANINGFULLY about.
        Respond with a single JSON object:
        {"results":[{"i":0,"tickers":["AAPL"]},{"i":1,"tickers":[]}]}
        Rules:
        - UPPERCASE NYSE/NASDAQ tickers only.
        - Include EVERY input index exactly once, in order.
        - Max 3 tickers per item.
        - Empty array for macro headlines, sector commentary, geopolitics, crypto-only, foreign-only stocks.
        """;
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _model;
    private readonly ILogger _log;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public UserGeminiExtractor(HttpClient http, string key, string model, ILogger log)
    { _http = http; _key = key; _model = model; _log = log; }

    public async Task<IReadOnlyList<MarketArticle>> ExtractAsync(IReadOnlyList<MarketArticle> items, CancellationToken ct)
    {
        if (items.Count == 0) return items;
        var need = new List<(int Idx, MarketArticle Item)>();
        for (int i = 0; i < items.Count; i++) if (items[i].Tickers.Length == 0) need.Add((i, items[i]));
        if (need.Count == 0) return items;

        var sb = new StringBuilder(need.Count * 80);
        for (int i = 0; i < need.Count; i++)
            sb.Append(i).Append(". [").Append(need[i].Item.Source).Append("] ").AppendLine(need[i].Item.Headline);

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = sb.ToString() } } } },
            generationConfig = new { temperature = 0.0, maxOutputTokens = need.Count * 25 + 400, responseMimeType = "application/json" }
        };
        var url = $"v1beta/models/{_model}:generateContent?key={Uri.EscapeDataString(_key)}";
        string? text;
        try
        {
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode) return items;
            text = ExtractGeminiText(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Gemini extractor failed"); return items; }
        if (string.IsNullOrWhiteSpace(text)) return items;

        var json = text.Trim();
        if (json.StartsWith("```")) { var nl = json.IndexOf('\n'); if (nl > 0) json = json[(nl + 1)..]; if (json.EndsWith("```")) json = json[..^3]; json = json.Trim(); }
        ExtractWrapper? parsed;
        try { parsed = JsonSerializer.Deserialize<ExtractWrapper>(json, Json); }
        catch { return items; }
        if (parsed?.Results is null) return items;

        var output = items.ToArray();
        foreach (var r in parsed.Results)
        {
            if (r.I < 0 || r.I >= need.Count) continue;
            var (idx, item) = need[r.I];
            var clean = (r.Tickers ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length is >= 1 and <= 6)
                .Select(t => t.ToUpperInvariant()).Distinct().ToArray();
            if (clean.Length == 0) continue;
            output[idx] = item with { Tickers = clean };
        }
        return output;
    }

    private static string ExtractGeminiText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("candidates", out var arr))
                foreach (var c in arr.EnumerateArray())
                    if (c.TryGetProperty("content", out var ct) && ct.TryGetProperty("parts", out var parts))
                        foreach (var p in parts.EnumerateArray())
                            if (p.TryGetProperty("text", out var t)) return t.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private sealed class ExtractWrapper { [JsonPropertyName("results")] public ResultItem[]? Results { get; set; } }
    private sealed class ResultItem
    {
        [JsonPropertyName("i")] public int I { get; set; }
        [JsonPropertyName("tickers")] public string[]? Tickers { get; set; }
    }
}

public sealed record TrendingTicker(string Ticker, string Reason, string Sentiment);

/// <summary>
/// Per-user xAI Grok call for trending US-listed stocks. Uses user's own Grok key. Hits the
/// /v1/responses endpoint with x_search + web_search tools enabled.
/// </summary>
public sealed class UserGrokTrending
{
    private const string SystemPrompt = """
        You are a stock-market discovery scout. Use x_search and web_search to find US-listed
        stocks currently trending in serious financial discussion right now.

        Prefer tickers with a real catalyst (earnings, M&A, FDA, regulatory action) and credible
        accounts (journalists, sell-side analysts, official accounts). EXCLUDE crypto, meme pumps,
        pure speculation, foreign-only stocks.

        Respond with a single JSON object and nothing else:
        {"trending":[{"ticker":"AAPL","reason":"<one sentence>","sentiment":"bullish|bearish|mixed"}]}
        Max 15 tickers, UPPERCASE only.
        """;
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly ILogger _log;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public UserGrokTrending(HttpClient http, string key, ILogger log)
    { _http = http; _key = key; _log = log; }

    public async Task<IReadOnlyList<TrendingTicker>> FetchAsync(CancellationToken ct)
    {
        var body = new
        {
            model = "grok-3-mini",
            stream = false,
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = "Run the discovery now. Return only the JSON object." }
            },
            tools = new object[] { new { type = "x_search" }, new { type = "web_search" } }
        };
        string raw;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _key);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) { _log.LogDebug("Grok HTTP {S}", resp.StatusCode); return Array.Empty<TrendingTicker>(); }
            raw = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Grok call threw"); return Array.Empty<TrendingTicker>(); }

        var text = ExtractText(raw);
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TrendingTicker>();

        var json = text.Trim();
        if (json.StartsWith("```")) { var nl = json.IndexOf('\n'); if (nl > 0) json = json[(nl + 1)..]; if (json.EndsWith("```")) json = json[..^3]; json = json.Trim(); }
        var first = json.IndexOf('{'); var last = json.LastIndexOf('}');
        if (first >= 0 && last > first) json = json[first..(last + 1)];

        TrendingResults? parsed;
        try { parsed = JsonSerializer.Deserialize<TrendingResults>(json, Json); }
        catch { return Array.Empty<TrendingTicker>(); }

        return (parsed?.Trending ?? Array.Empty<TrendingItem>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Ticker) && t.Ticker!.Length is >= 1 and <= 6)
            .Select(t => new TrendingTicker(t.Ticker!.ToUpperInvariant(), t.Reason ?? "", (t.Sentiment ?? "mixed").ToLowerInvariant()))
            .GroupBy(t => t.Ticker).Select(g => g.First()).Take(15).ToList();
    }

    private static string? ExtractText(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String) return ot.GetString();
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var p in arr.EnumerateArray())
                            if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
                    if (item.TryGetProperty("text", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
                }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0];
                if (msg.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                    return mc.GetString();
            }
        }
        catch { }
        return null;
    }

    private sealed class TrendingResults { [JsonPropertyName("trending")] public TrendingItem[]? Trending { get; set; } }
    private sealed class TrendingItem
    {
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    }
}
