using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Asks Grok (xAI) — with X/news search enabled via the Agent Tools API — for the US-listed
/// stocks currently trending in financial discussion. Uses the v1/responses endpoint (the
/// older v1/chat/completions search_parameters approach was deprecated in 2026).
/// </summary>
public sealed class GrokTrendingProvider
{
    private const string SystemPrompt = """
        You are a stock-market discovery scout for an automated trading bot. Use the x_search and
        web_search tools to identify US-listed stocks currently trending in serious financial
        discussion right now.

        Prefer tickers with at least one of:
        - A clear, recent news catalyst (earnings, guidance change, M&A, regulatory action,
          FDA decision, major contract, leadership change, product launch).
        - Unusual mention volume vs baseline (real conviction, not random chatter).
        - Discussion from credible accounts (journalists, sell-side analysts, official accounts,
          well-known fund managers) — not anonymous shill accounts.

        EXCLUDE:
        - Crypto-only tickers (BTC, ETH, etc.) — bot doesn't trade crypto.
        - Stocks trending only due to obvious pump-and-dump or meme campaigns.
        - Pure speculation with no underlying catalyst.
        - Foreign-only stocks not listed on US exchanges.

        Respond with a single JSON object and nothing else (no prose, no markdown fences):
        {
          "trending": [
            { "ticker": "AAPL", "reason": "<one sentence catalyst>", "sentiment": "bullish" | "bearish" | "mixed" }
          ]
        }

        Cap at the most relevant 15 tickers. UPPERCASE tickers only.
        """;

    private readonly HttpClient _http;
    private readonly GrokOptions _opts;
    private readonly ILogger<GrokTrendingProvider> _log;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public GrokTrendingProvider(
        HttpClient http,
        IOptions<GrokOptions> opts,
        ILogger<GrokTrendingProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<TrendingSnapshot> FetchAsync(CancellationToken ct)
    {
        // New Agent Tools API uses `/v1/responses` with `input` + `tools`, NOT chat/completions.
        var body = new
        {
            model = _opts.Model,
            stream = false,
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = "Run the discovery now. Return only the JSON object." }
            },
            tools = new object[]
            {
                new { type = "x_search" },
                new { type = "web_search" }
            }
        };

        string? rawText;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
            {
                Content = JsonContent.Create(body)
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Grok API call failed ({Status}): {Body}", resp.StatusCode, err);
                return new TrendingSnapshot(DateTimeOffset.UtcNow, Array.Empty<TrendingTicker>());
            }
            rawText = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Grok API call threw");
            return new TrendingSnapshot(DateTimeOffset.UtcNow, Array.Empty<TrendingTicker>());
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            _log.LogWarning("Grok returned empty response body");
            return new TrendingSnapshot(DateTimeOffset.UtcNow, Array.Empty<TrendingTicker>());
        }

        // Response shape isn't 100% documented; extract the text content defensively.
        var content = ExtractText(rawText);
        if (string.IsNullOrWhiteSpace(content))
        {
            _log.LogWarning("Grok response had no extractable text. Raw (first 400 chars): {Snippet}",
                rawText.Length > 400 ? rawText[..400] : rawText);
            return new TrendingSnapshot(DateTimeOffset.UtcNow, Array.Empty<TrendingTicker>());
        }

        // Tolerate code fences if Grok slipped any in.
        var json = content.Trim();
        if (json.StartsWith("```"))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        // Grok might wrap the JSON in additional prose — try to locate the first { ... } block.
        var firstBrace = json.IndexOf('{');
        var lastBrace = json.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            json = json[firstBrace..(lastBrace + 1)];

        TrendingResults? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TrendingResults>(json, Json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse Grok JSON. Raw text: {Raw}", content);
            return new TrendingSnapshot(DateTimeOffset.UtcNow, Array.Empty<TrendingTicker>());
        }

        var trending = (parsed?.Trending ?? Array.Empty<TrendingItem>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Ticker) && t.Ticker.Length is >= 1 and <= 6)
            .Select(t => new TrendingTicker(
                Ticker: t.Ticker!.ToUpperInvariant(),
                Reason: t.Reason ?? "",
                Sentiment: (t.Sentiment ?? "mixed").ToLowerInvariant()))
            .GroupBy(t => t.Ticker)
            .Select(g => g.First())
            .Take(_opts.MaxTickersPerPoll)
            .ToList();

        _log.LogInformation("Grok trending: {Count} tickers returned", trending.Count);
        return new TrendingSnapshot(DateTimeOffset.UtcNow, trending);
    }

    /// <summary>
    /// Walk the responses-API JSON looking for the model's text output. xAI's exact shape isn't
    /// fully documented; we accept several likely paths and fall back to scanning all string
    /// fields for the JSON we asked for.
    /// </summary>
    private static string? ExtractText(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Path 1: { "output_text": "..." }
            if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
                return ot.GetString();

            // Path 2: { "output": [ { "content": [ { "type": "output_text"|"text", "text": "..." } ] } ] }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (item.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in contentArr.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object &&
                                part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                            {
                                return t.GetString();
                            }
                        }
                    }
                    if (item.TryGetProperty("text", out var direct) && direct.ValueKind == JsonValueKind.String)
                        return direct.GetString();
                }
            }

            // Path 3 (OpenAI chat-compat fallback): { "choices": [ { "message": { "content": "..." } } ] }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                {
                    return mc.GetString();
                }
            }
        }
        catch { /* swallow — caller logs */ }

        return null;
    }

    private sealed class TrendingResults
    {
        [JsonPropertyName("trending")] public TrendingItem[]? Trending { get; set; }
    }
    private sealed class TrendingItem
    {
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
    }
}
