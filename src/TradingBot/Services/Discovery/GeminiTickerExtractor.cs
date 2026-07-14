using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Gemini-backed batch ticker extractor. Drop-in replacement for ClaudeTickerExtractor.
/// One Gemini Flash call per batch — free under the daily quota.
/// </summary>
public sealed class GeminiTickerExtractor : ITickerExtractor
{
    private const string SystemPrompt = """
        You are a financial news ticker classifier. You will be given a numbered list of news
        headlines. For each one, identify which US-listed publicly traded stock(s) the headline
        is MEANINGFULLY about — not tickers mentioned in passing, not sectors, not generic
        market commentary.

        Output rules — absolutely strict:

        - Respond with a single JSON object and nothing else.
        - Format:
          {
            "results": [
              { "i": 0, "tickers": ["AAPL"] },
              { "i": 1, "tickers": [] },
              { "i": 2, "tickers": ["TSLA", "RIVN"] }
            ]
          }
        - Use UPPERCASE NYSE/NASDAQ ticker symbols only (e.g. AAPL, MSFT, BRK.B, GOOGL).
        - Include EVERY input index exactly once, in order, even if tickers is [].
        - Maximum 3 tickers per item — only the primary subjects.
        - "tickers": [] is correct for:
            * Macro/Fed/CPI/jobs reports (no specific company)
            * Geopolitical/war/election headlines
            * Generic "stocks to watch" listicles
            * Sector-only commentary with no named company
            * Foreign-only stocks not listed on US exchanges
            * Crypto-only headlines (BTC, ETH, etc. are not stocks)
        - For ETFs that primarily track a commodity or index (GLD, SLV, SPY, QQQ), include
          them only if the article is specifically about that ETF or its underlying.
        """;

    private readonly HttpClient _http;
    private readonly GeminiOptions _opts;
    private readonly ILogger<GeminiTickerExtractor> _log;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public GeminiTickerExtractor(
        HttpClient http,
        IOptions<GeminiOptions> opts,
        ILogger<GeminiTickerExtractor> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<MarketNewsItem>> ExtractAsync(
        IReadOnlyList<MarketNewsItem> items,
        CancellationToken ct)
    {
        if (items.Count == 0) return items;

        var needExtraction = new List<(int OriginalIdx, MarketNewsItem Item)>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Tickers.Length == 0)
                needExtraction.Add((i, items[i]));
        }
        if (needExtraction.Count == 0) return items;

        var sb = new System.Text.StringBuilder(needExtraction.Count * 80);
        for (int i = 0; i < needExtraction.Count; i++)
        {
            sb.Append(i).Append(". [").Append(needExtraction[i].Item.Source).Append("] ");
            sb.AppendLine(needExtraction[i].Item.Headline);
        }

        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = SystemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = sb.ToString() } }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = Math.Max(_opts.MaxExtractorTokens, needExtraction.Count * 25 + 200),
                responseMimeType = "application/json"
            }
        };

        var url = $"v1beta/models/{_opts.ExtractorModel}:generateContent?key={Uri.EscapeDataString(_opts.ApiKey)}";

        GeminiResponse? response;
        try
        {
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Gemini extractor failed ({Status}): {Body}", resp.StatusCode, err);
                return items;
            }
            response = await resp.Content.ReadFromJsonAsync<GeminiResponse>(Json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gemini extractor threw");
            return items;
        }

        var text = response?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("Gemini extractor returned no content");
            return items;
        }

        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        ExtractedResults? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ExtractedResults>(json, Json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse Gemini extractor JSON. Raw: {Raw}", text);
            return items;
        }
        if (parsed?.Results is null) return items;

        var output = items.ToArray();
        int tagged = 0;
        foreach (var r in parsed.Results)
        {
            if (r.I < 0 || r.I >= needExtraction.Count) continue;
            var (originalIdx, item) = needExtraction[r.I];
            var clean = (r.Tickers ?? Array.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length is >= 1 and <= 6)
                .Select(t => t.ToUpperInvariant())
                .Distinct()
                .ToArray();
            if (clean.Length == 0) continue;
            output[originalIdx] = item with { Tickers = clean };
            tagged++;
        }

        _log.LogInformation("Gemini extractor: {Sent} headlines → {Tagged} tickered",
            needExtraction.Count, tagged);

        return output;
    }

    // --- Gemini wire types ----
    private sealed class GeminiResponse
    {
        [JsonPropertyName("candidates")] public Candidate[]? Candidates { get; set; }
    }
    private sealed class Candidate
    {
        [JsonPropertyName("content")] public CandidateContent? Content { get; set; }
    }
    private sealed class CandidateContent
    {
        [JsonPropertyName("parts")] public Part[]? Parts { get; set; }
    }
    private sealed class Part
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class ExtractedResults
    {
        [JsonPropertyName("results")] public ExtractedItem[]? Results { get; set; }
    }
    private sealed class ExtractedItem
    {
        [JsonPropertyName("i")] public int I { get; set; }
        [JsonPropertyName("tickers")] public string[]? Tickers { get; set; }
    }
}
