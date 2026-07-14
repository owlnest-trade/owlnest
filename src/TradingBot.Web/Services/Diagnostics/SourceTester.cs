using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TradingBot.Web.Services.Shared;
using TradingBot.Web.Services.UserBot;

namespace TradingBot.Web.Services.Diagnostics;

/// <summary>One test result per news source for the /Diagnostics page.</summary>
public sealed record SourceResult(
    string Source,
    bool Ok,
    string Status,        // "OK" | "FAIL: <reason>"
    int ResultCount,
    long ElapsedMs,
    IReadOnlyList<string> Samples);   // up to 3 sample headlines / one-line summaries

/// <summary>
/// Runs a live test against every news source for a given ticker (default AAPL) and
/// returns what each one actually returned. Used by /Diagnostics to prove the pipeline
/// without needing a user bot to be running.
/// </summary>
public sealed class SourceTester
{
    private readonly ServerKeys _serverKeys;
    private readonly SecCikCache _cikCache;
    private readonly SecFilingsFeed _sec;
    private readonly ManifoldFeed _macro;
    private readonly FedFeed _fed;
    private readonly ILogger<SourceTester> _log;
    private readonly HttpClient _finnhubHttp;
    private readonly HttpClient _llmHttp;
    private readonly HttpClient _grokHttp;
    private readonly HttpClient _anthropicHttp;
    private readonly HttpClient _groqHttpCloud;   // Groq Cloud (Llama) — DIFFERENT from _grokHttp above (xAI)

    public SourceTester(
        IOptions<ServerKeys> serverKeys, SecCikCache cikCache, SecFilingsFeed sec,
        ManifoldFeed macro, FedFeed fed, ILogger<SourceTester> log)
    {
        _serverKeys = serverKeys.Value;
        _cikCache = cikCache;
        _sec = sec;
        _macro = macro;
        _fed = fed;
        _log = log;
        _finnhubHttp = new HttpClient
        {
            BaseAddress = new Uri("https://finnhub.io/api/v1/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _llmHttp = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _llmHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _grokHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.x.ai/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _anthropicHttp = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        // Groq Cloud — separate company from xAI; hosts open-weights models (Llama, Mixtral) via
        // an OpenAI-compatible API. Used for the cheap-tier sentiment classifier.
        _groqHttpCloud = new HttpClient
        {
            BaseAddress = new Uri("https://api.groq.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<IReadOnlyList<SourceResult>> TestAllAsync(string ticker, CancellationToken ct)
    {
        // Wrap the two sync methods in Task.FromResult so they all share the Task<SourceResult> type.
        var tasks = new Task<SourceResult>[]
        {
            TestFinnhubAsync(ticker, ct),
            TestSecAsync(ticker, ct),
            TestInsiderAsync(ticker, ct),
            TestGoogleNewsAsync(ticker, ct),
            Task.FromResult(TestManifold()),
            Task.FromResult(TestFed()),
            TestGeminiAsync(ct),
            TestGrokAsync(ct),
            TestAnthropicAsync(ct),
            TestLlamaAsync(ct),
            TestCryptoNewsAsync(ct),
        };
        return await Task.WhenAll(tasks);
    }

    // ── Per-source tests ─────────────────────────────────────────────────

    private async Task<SourceResult> TestFinnhubAsync(string ticker, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsFinnhubReady)
            return new("Finnhub per-ticker news", false, "FAIL: no server key configured", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var to = DateTime.UtcNow.Date.ToString("yyyy-MM-dd");
            var from = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
            var url = $"company-news?symbol={Uri.EscapeDataString(ticker)}&from={from}&to={to}&token={Uri.EscapeDataString(_serverKeys.Finnhub)}";
            var raw = await _finnhubHttp.GetFromJsonAsync<FinnhubArticle[]>(url, ct);
            sw.Stop();
            var arr = raw ?? Array.Empty<FinnhubArticle>();
            var samples = arr.Where(a => !string.IsNullOrWhiteSpace(a.Headline)).Take(3)
                             .Select(a => $"[{a.Source}] {a.Headline}").ToList();
            return new("Finnhub per-ticker news", arr.Length > 0, arr.Length > 0 ? "OK" : "OK but 0 results", arr.Length, sw.ElapsedMilliseconds, samples);
        }
        catch (Exception ex) { sw.Stop(); return new("Finnhub per-ticker news", false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    private async Task<SourceResult> TestSecAsync(string ticker, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            var filings = await _sec.GetFilingsAsync(ticker, since, "diagnostics@owlnest.trade",
                include8K: true, include10Q: true, include10K: true, ct);
            sw.Stop();
            var samples = filings.Take(3).Select(f => f.Headline + $" ({f.AcceptedAt:yyyy-MM-dd})").ToList();
            return new("SEC EDGAR filings", true, filings.Count > 0 ? "OK" : "OK but 0 recent filings", filings.Count, sw.ElapsedMilliseconds, samples);
        }
        catch (Exception ex) { sw.Stop(); return new("SEC EDGAR filings", false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    private async Task<SourceResult> TestInsiderAsync(string ticker, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsFinnhubReady)
            return new("Insider transactions (Form 4)", false, "FAIL: no server key", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var feed = new UserInsiderFeed(_finnhubHttp, _serverKeys.Finnhub, _log);
            var txs = await feed.GetAsync(ticker, DateTimeOffset.UtcNow.AddDays(-90), ct);
            sw.Stop();
            var samples = txs.Take(3).Select(t => UserInsiderFeed.FormatHeadline(t).headline).ToList();
            return new("Insider transactions (Form 4)", true, txs.Count > 0 ? "OK" : "OK but 0 recent transactions", txs.Count, sw.ElapsedMilliseconds, samples);
        }
        catch (Exception ex) { sw.Stop(); return new("Insider transactions (Form 4)", false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    private async Task<SourceResult> TestGoogleNewsAsync(string ticker, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var feed = new UserGoogleNewsFeed(_log);
            var articles = await feed.GetAsync(ticker, DateTimeOffset.UtcNow.AddDays(-7), ct);
            sw.Stop();
            var samples = articles.Take(3).Select(a => $"[{a.Source}] {a.Headline}").ToList();
            return new("Google News RSS", articles.Count > 0, articles.Count > 0 ? "OK" : "OK but 0 results", articles.Count, sw.ElapsedMilliseconds, samples);
        }
        catch (Exception ex) { sw.Stop(); return new("Google News RSS", false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    // Reddit test removed in v8 — Reddit is no longer a source.

    private SourceResult TestManifold()
    {
        var sw = Stopwatch.StartNew();
        var snap = _macro.Latest;
        sw.Stop();
        var samples = snap.Markets.Take(3).Select(m => $"{m.Question} → {(m.YesPrice * 100):F0}% YES").ToList();
        return new("Manifold prediction markets", snap.Markets.Count > 0,
            snap.Markets.Count > 0 ? $"OK (last refresh {snap.AtUtc:HH:mm} UTC)" : "EMPTY (waiting for first poll)",
            snap.Markets.Count, sw.ElapsedMilliseconds, samples);
    }

    private SourceResult TestFed()
    {
        var sw = Stopwatch.StartNew();
        var events = _fed.Recent(50);
        sw.Stop();
        var samples = events.Take(3).Select(e => $"[{e.Source}] {e.Title} ({e.PublishedAt:yyyy-MM-dd})").ToList();
        return new("Federal Reserve RSS", events.Count > 0,
            events.Count > 0 ? "OK (cached)" : "EMPTY (waiting for first poll)",
            events.Count, sw.ElapsedMilliseconds, samples);
    }

    private async Task<SourceResult> TestGeminiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsGeminiReady)
            return new("Google Gemini (LLM)", false, "FAIL: no server key configured", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var body = new
            {
                contents = new[] { new { role = "user", parts = new[] {
                    new { text = "Headline: 'Apple beats Q3 estimates, iPhone shipments up 8% YoY'. Reply with one short word: bullish, bearish, or neutral." }
                }}},
                generationConfig = new { temperature = 0.0, maxOutputTokens = 10 }
            };
            var url = $"v1beta/models/{_serverKeys.GeminiModel}:generateContent?key={Uri.EscapeDataString(_serverKeys.Gemini)}";
            using var resp = await _llmHttp.PostAsJsonAsync(url, body, ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new("Google Gemini (LLM)", false, $"FAIL: HTTP {(int)resp.StatusCode} — {err[..Math.Min(120, err.Length)]}", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            string verdict = "?";
            if (doc.RootElement.TryGetProperty("candidates", out var arr) && arr.GetArrayLength() > 0
                && arr[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0
                && parts[0].TryGetProperty("text", out var t))
                verdict = (t.GetString() ?? "?").Trim();
            return new("Google Gemini (LLM)", true, "OK", 1, sw.ElapsedMilliseconds,
                new[] { $"Probe headline → Gemini verdict: \"{verdict}\"" });
        }
        catch (Exception ex) { sw.Stop(); return new("Google Gemini (LLM)", false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    /// <summary>
    /// Cheap probe for the Grok 2nd-opinion gate. Hits xAI's /v1/responses endpoint with a single
    /// non-tool prompt so we verify key + auth + connectivity without spending the ~$0.01 a real
    /// gate call (with x_search + web_search) would cost. The real gate's behaviour is identical
    /// modulo the tool calls.
    /// </summary>
    private async Task<SourceResult> TestGrokAsync(CancellationToken ct)
    {
        const string label = "Grok 2nd-opinion gate (xAI)";
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsGrokReady)
            return new(label, false, "FAIL: no server key configured (set ServerKeys__Grok)", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var body = new
            {
                model = "grok-3-mini",
                stream = false,
                input = new object[]
                {
                    new { role = "user", content = "Headline: 'Apple beats Q3 estimates, iPhone shipments up 8% YoY'. Reply with one short word: bullish, bearish, or neutral." }
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverKeys.Grok);
            using var resp = await _grokHttp.SendAsync(req, ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new(label, false, $"FAIL: HTTP {(int)resp.StatusCode} — {err[..Math.Min(120, err.Length)]}", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
            }
            var raw = await resp.Content.ReadAsStringAsync(ct);
            var verdict = ExtractGrokText(raw) ?? "(no text)";
            return new(label, true, "OK (cheap probe — no web_search)", 1, sw.ElapsedMilliseconds,
                new[] { $"Probe headline → Grok verdict: \"{verdict.Trim()}\"" });
        }
        catch (Exception ex) { sw.Stop(); return new(label, false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    /// <summary>
    /// Cheap probe for the Claude verification gate. Hits Anthropic's /v1/messages with a single
    /// non-tool prompt — verifies key + auth + connectivity without paying for a real web_search
    /// invocation (~$0.05/call). Real gate calls include the web_search tool; the probe doesn't.
    /// </summary>
    private async Task<SourceResult> TestAnthropicAsync(CancellationToken ct)
    {
        const string label = "Claude verification gate (Anthropic)";
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsAnthropicReady)
            return new(label, false, "FAIL: no server key configured (set ServerKeys__Anthropic)", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var body = new
            {
                model = _serverKeys.AnthropicModel,
                max_tokens = 16,
                messages = new[]
                {
                    new { role = "user", content = "Headline: 'Apple beats Q3 estimates, iPhone shipments up 8% YoY'. Reply with one short word: bullish, bearish, or neutral." }
                }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(body) };
            req.Headers.Add("x-api-key", _serverKeys.Anthropic);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await _anthropicHttp.SendAsync(req, ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new(label, false, $"FAIL: HTTP {(int)resp.StatusCode} — {err[..Math.Min(120, err.Length)]}", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
            }
            var raw = await resp.Content.ReadAsStringAsync(ct);
            var verdict = ExtractAnthropicText(raw) ?? "(no text)";
            return new(label, true, "OK (cheap probe — no web_search)", 1, sw.ElapsedMilliseconds,
                new[] { $"Probe headline → Claude ({_serverKeys.AnthropicModel}) verdict: \"{verdict.Trim()}\"" });
        }
        catch (Exception ex) { sw.Stop(); return new(label, false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    // xAI returns the model's text in a few possible shapes — handle the responses-API and
    // the OpenAI-compat chat-completions shape. Mirrors UserGrokConfirmation.ExtractText.
    private static string? ExtractGrokText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
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

    // Anthropic returns content as an array of typed blocks; find the first text block.
    private static string? ExtractAnthropicText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("content", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var block in arr.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    return txt.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Probe Groq Cloud (Llama) — the cheap-tier sentiment classifier. Same probe shape as the
    /// Gemini test: a one-shot "is this headline bullish?" prompt. Returns the verdict word so
    /// you can confirm the model is responding sanely, not just that auth works.
    /// </summary>
    private async Task<SourceResult> TestLlamaAsync(CancellationToken ct)
    {
        const string label = "Groq · Llama (cheap sentiment)";
        var sw = Stopwatch.StartNew();
        if (!_serverKeys.IsLlamaReady)
            return new(label, false, "FAIL: no server key configured (set ServerKeys__Llama)", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
        try
        {
            var body = new
            {
                model = _serverKeys.LlamaModel,
                messages = new object[]
                {
                    new { role = "user", content = "Headline: 'Apple beats Q3 estimates, iPhone shipments up 8% YoY'. Reply with one short word: bullish, bearish, or neutral." }
                },
                temperature = 0.0,
                // Reasoning models (openai/gpt-oss-*) burn their entire budget on internal
                // thinking; at 16 they return ""; 500 leaves enough headroom for the chain-of-
                // thought + the actual one-word answer. Non-reasoning models stay cheap.
                max_tokens = 500
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "openai/v1/chat/completions") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverKeys.Llama);
            using var resp = await _groqHttpCloud.SendAsync(req, ct);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return new(label, false, $"FAIL: HTTP {(int)resp.StatusCode} — {err[..Math.Min(120, err.Length)]}", 0, sw.ElapsedMilliseconds, Array.Empty<string>());
            }
            var raw = await resp.Content.ReadAsStringAsync(ct);
            var verdict = ExtractOpenAIChatText(raw) ?? "(no text)";
            return new(label, true, "OK", 1, sw.ElapsedMilliseconds,
                new[] { $"Probe headline → {_serverKeys.LlamaModel} verdict: \"{verdict.Trim()}\"" });
        }
        catch (Exception ex) { sw.Stop(); return new(label, false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    private static string? ExtractOpenAIChatText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("choices", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                    return c.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Probe the crypto news feed (Google News with BTC/Bitcoin keyword translation) so users can
    /// verify their crypto pipeline before the bot tries to trade BTC at 2am.
    /// We hard-code BTC/USD as the probe symbol because the ticker param to /Diagnostics is an
    /// equity ticker (e.g. AAPL) and there's no UX yet for picking a separate crypto probe symbol.
    /// </summary>
    private async Task<SourceResult> TestCryptoNewsAsync(CancellationToken ct)
    {
        const string label = "Crypto news feed (BTC/USD probe)";
        var sw = Stopwatch.StartNew();
        try
        {
            var feed = new UserCryptoNewsFeed(_log);
            var articles = await feed.GetAsync("BTC/USD", DateTimeOffset.UtcNow.AddDays(-3), ct);
            sw.Stop();
            var samples = articles.Take(3).Select(a => $"[{a.Source}] {a.Headline}").ToList();
            return new(label, articles.Count > 0,
                articles.Count > 0 ? "OK" : "OK but 0 results for BTC in last 72h",
                articles.Count, sw.ElapsedMilliseconds, samples);
        }
        catch (Exception ex) { sw.Stop(); return new(label, false, "FAIL: " + ex.Message, 0, sw.ElapsedMilliseconds, Array.Empty<string>()); }
    }

    private sealed class FinnhubArticle
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("datetime")] public long Datetime { get; set; }
        [JsonPropertyName("headline")] public string? Headline { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }
}
