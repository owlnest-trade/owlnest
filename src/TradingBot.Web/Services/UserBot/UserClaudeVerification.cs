using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.UserBot;

public enum ClaudeVerdict { Approve, Caution, Veto, Error }
/// <summary>
/// Result of a Claude verification call. Includes audit-trail fields so the caller can persist
/// a UserGateCall row without re-sending the request.
/// </summary>
public sealed record ClaudeVerification(
    ClaudeVerdict Verdict, string Reason,
    string ModelName, string Prompt, string RawResponse, int LatencyMs);

/// <summary>
/// Per-user Claude-powered second-opinion gate for bullish entries. Uses Anthropic's official
/// web_search server-side tool, so Claude pulls in fresh context from across the web before
/// returning a verdict on whether the trade actually makes sense.
///
/// Differs from UserGrokConfirmation in three ways:
///   1. Claude tends to be better at reasoning over long contexts (earnings calls, SEC filings, etc.).
///   2. Anthropic's API is more reliable than xAI's.
///   3. web_search is built-in — no separate plumbing.
///
/// Either or both of (this + UserGrokConfirmation) can be enabled. If both are on,
/// UserBotInstance requires BOTH to approve before submitting the order — conservative by design.
/// </summary>
public sealed class UserClaudeVerification
{
    private const string SystemPrompt = """
        You are a stock-trading second-opinion AI. An automated trader is about to BUY a US stock
        based on a positive news headline. Your job: investigate via the web_search tool and decide
        if the trade actually makes sense.

        Use web_search aggressively. In the last 24-48 hours look for:
        1. Major contradicting news, downgrades, analyst notes that the trader's headline didn't reflect.
        2. Pump-and-dump, meme-coordinated, or low-float-squeeze indicators.
        3. Skepticism from credible accounts (journalists, sell-side analysts, official sources).
        4. Imminent earnings, FDA, regulatory, or macro risk the trader didn't flag.
        5. Whether the catalyst in the headline is already widely priced in.

        Then ask yourself: "Given everything I just found, does this trade make sense?"

        Respond with a single JSON object and nothing else:
        {"decision": "approve" | "caution" | "veto", "reason": "<one short sentence>"}

        Guidelines:
        - "approve": you found supporting evidence or at least no meaningful contradicting context.
        - "caution": mixed or thin evidence. Lean against the trade — do NOT recommend buying.
        - "veto": clear contradicting evidence found. Definitely do not buy.
        - When in doubt → caution. The trader will only buy on "approve". Be conservative.
        """;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger _log;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public UserClaudeVerification(HttpClient http, string apiKey, string model, ILogger log)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
        _log = log;
    }

    public async Task<ClaudeVerification> CheckAsync(
        string ticker, string headline, string source, double confidence, string llmReasoning,
        CancellationToken ct)
    {
        var userPrompt = $"""
            Ticker: {ticker}
            Headline (from {source}): {headline}
            Our sentiment model: bullish at {confidence:P0} confidence.
            Our reasoning: {llmReasoning}

            Investigate via web_search and return the JSON verdict. Be conservative — only "approve"
            if the evidence clearly supports the trade.
            """;

        var body = new
        {
            model = _model,
            max_tokens = 1024,
            system = SystemPrompt,
            // Anthropic's server-side web_search tool. Claude calls it autonomously and uses
            // results to inform its final text response.
            tools = new object[]
            {
                new { type = "web_search_20250305", name = "web_search" }
            },
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string raw = "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            // Anthropic requires this beta header to use web_search.
            req.Headers.Add("anthropic-beta", "web-search-2025-03-05");

            using var resp = await _http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                sw.Stop();
                _log.LogDebug("Claude verification HTTP {S} for {Ticker}: {Body}", resp.StatusCode, ticker, raw);
                return new ClaudeVerification(ClaudeVerdict.Error, $"Claude HTTP {(int)resp.StatusCode}",
                    _model, userPrompt, raw, (int)sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogDebug(ex, "Claude verification call threw for {Ticker}", ticker);
            return new ClaudeVerification(ClaudeVerdict.Error, "Claude call threw: " + ex.Message,
                _model, userPrompt, raw, (int)sw.ElapsedMilliseconds);
        }
        sw.Stop();
        var latencyMs = (int)sw.ElapsedMilliseconds;

        var text = ExtractFinalText(raw);
        if (string.IsNullOrWhiteSpace(text))
            return new ClaudeVerification(ClaudeVerdict.Error, "Claude returned no text content",
                _model, userPrompt, raw, latencyMs);

        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }
        var first = json.IndexOf('{');
        var last = json.LastIndexOf('}');
        if (first >= 0 && last > first) json = json[first..(last + 1)];

        VerdictResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<VerdictResponse>(json, Json); }
        catch { return new ClaudeVerification(ClaudeVerdict.Error, "Claude JSON unparseable",
            _model, userPrompt, raw, latencyMs); }

        var decision = (parsed?.Decision ?? "").Trim().ToLowerInvariant() switch
        {
            "approve" => ClaudeVerdict.Approve,
            "veto" => ClaudeVerdict.Veto,
            "caution" => ClaudeVerdict.Caution,
            _ => ClaudeVerdict.Error
        };
        return new ClaudeVerification(decision, parsed?.Reason ?? "",
            _model, userPrompt, raw, latencyMs);
    }

    /// <summary>
    /// Anthropic response has multiple content blocks (tool_use blocks for each web_search call,
    /// plus the final text block). We want the LAST text block.
    /// </summary>
    private static string? ExtractFinalText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("content", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            string? lastText = null;
            foreach (var block in arr.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    lastText = txt.GetString();
                }
            }
            return lastText;
        }
        catch { return null; }
    }

    private sealed class VerdictResponse
    {
        [JsonPropertyName("decision")] public string? Decision { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
