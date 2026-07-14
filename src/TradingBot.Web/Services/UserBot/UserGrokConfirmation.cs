using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Web.Services.UserBot;

public enum GrokVerdict { Approve, Caution, Veto, Error }
/// <summary>
/// Result of a Grok 2nd-opinion call. Includes the audit-trail fields so the caller can persist
/// a UserGateCall row without round-tripping through this service for the raw text.
/// </summary>
public sealed record GrokConfirmation(
    GrokVerdict Verdict, string Reason,
    string ModelName, string Prompt, string RawResponse, int LatencyMs);

/// <summary>
/// Per-user Grok-powered second-opinion gate for bullish entries. After our local sentiment +
/// confirmation + earnings checks all pass, we hit Grok with x_search and web_search to ask
/// "is there contradicting context in the last 24h that our news feed missed?". Conservative:
/// only "approve" lets the buy through. Anything else (caution / veto / error) skips the trade.
///
/// Costs ~$0.01 per call (xAI bills web_search). Opt-in via GrokConfirmationEnabled in settings.
/// </summary>
public sealed class UserGrokConfirmation
{
    private const string SystemPrompt = """
        You are a stock-trading second-opinion AI. An automated trader is about to BUY a US stock
        based on a positive news headline. Your job: stop the trade if there's contradicting
        evidence the trader's news feed missed.

        Use x_search and web_search to investigate, in the last 24 hours:
        1. Major contradicting news, downgrades, or analyst notes the headline doesn't reflect.
        2. Whether this is a known pump-and-dump, meme-coordinated trade, or low-float squeeze.
        3. Skepticism from credible accounts (journalists, sell-side analysts, official accounts).
        4. Imminent earnings, FDA, or macro risk that the original signal didn't flag.

        Respond with a single JSON object and nothing else:
        {"decision": "approve" | "caution" | "veto", "reason": "<one short sentence>"}

        Guidelines:
        - "approve": you find supporting evidence or at least no contradicting context. Buy is OK.
        - "caution": mixed signals or thin evidence. Lean against — do NOT recommend buying.
        - "veto": clear contradicting evidence found. Definitely do not buy.
        - When in doubt → caution. The trader will only buy on "approve".
        """;

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly ILogger _log;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public UserGrokConfirmation(HttpClient http, string key, ILogger log)
    { _http = http; _key = key; _log = log; }

    public async Task<GrokConfirmation> CheckAsync(
        string ticker, string headline, string source, double confidence, string llmReasoning,
        CancellationToken ct)
    {
        const string modelName = "grok-3-mini";
        var prompt = $"""
            Ticker: {ticker}
            Headline (from {source}): {headline}
            Our sentiment model: bullish at {confidence:P0} confidence.
            Our reasoning: {llmReasoning}

            Investigate via x_search and web_search. Return the JSON verdict.
            """;

        var body = new
        {
            model = modelName,
            stream = false,
            input = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = prompt }
            },
            tools = new object[] { new { type = "x_search" }, new { type = "web_search" } }
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string raw = "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            using var resp = await _http.SendAsync(req, ct);
            raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                sw.Stop();
                _log.LogDebug("Grok confirm HTTP {S} for {Ticker}", resp.StatusCode, ticker);
                return new GrokConfirmation(GrokVerdict.Error, $"Grok HTTP {(int)resp.StatusCode}",
                    modelName, prompt, raw, (int)sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogDebug(ex, "Grok confirm call threw for {Ticker}", ticker);
            return new GrokConfirmation(GrokVerdict.Error, "Grok call threw: " + ex.Message,
                modelName, prompt, raw, (int)sw.ElapsedMilliseconds);
        }
        sw.Stop();
        var latencyMs = (int)sw.ElapsedMilliseconds;

        var text = ExtractText(raw);
        if (string.IsNullOrWhiteSpace(text))
            return new GrokConfirmation(GrokVerdict.Error, "Grok returned no text",
                modelName, prompt, raw, latencyMs);

        var json = text.Trim();
        if (json.StartsWith("```")) { var nl = json.IndexOf('\n'); if (nl > 0) json = json[(nl + 1)..]; if (json.EndsWith("```")) json = json[..^3]; json = json.Trim(); }
        var first = json.IndexOf('{'); var last = json.LastIndexOf('}');
        if (first >= 0 && last > first) json = json[first..(last + 1)];

        VerdictResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<VerdictResponse>(json, Json); }
        catch { return new GrokConfirmation(GrokVerdict.Error, "Grok JSON unparseable",
            modelName, prompt, raw, latencyMs); }

        var decision = (parsed?.Decision ?? "").ToLowerInvariant() switch
        {
            "approve" => GrokVerdict.Approve,
            "veto" => GrokVerdict.Veto,
            "caution" => GrokVerdict.Caution,
            _ => GrokVerdict.Error
        };
        return new GrokConfirmation(decision, parsed?.Reason ?? "",
            modelName, prompt, raw, latencyMs);
    }

    private static string? ExtractText(string raw)
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

    private sealed class VerdictResponse
    {
        [JsonPropertyName("decision")] public string? Decision { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }
}
