using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.Discovery;

/// <summary>
/// Batch ticker extractor. Takes the firehose of MarketNewsItems where Finnhub didn't provide
/// `related` tags, sends them all in one Claude (Haiku) call with a cached system prompt,
/// returns each article re-stamped with the tickers Claude identified.
/// One call per N items keeps cost predictable: ~$0.01 per batch of 100 headlines.
/// </summary>
public sealed class ClaudeTickerExtractor : ITickerExtractor
{
    private const string SystemPrompt = """
        You are a financial news ticker classifier. You will be given a numbered list of news
        headlines. For each one, identify which US-listed publicly traded stock(s) the headline
        is MEANINGFULLY about — not tickers mentioned in passing, not sectors, not generic
        market commentary.

        Output rules — absolutely strict:

        - Respond with a single JSON object and nothing else. No prose, no markdown fences,
          no commentary before or after.
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
          them only if the article is specifically about that ETF or its underlying — not for
          generic "stocks fell" mentions.
        """;

    private readonly HttpClient _http;
    private readonly AnthropicOptions _opts;
    private readonly ILogger<ClaudeTickerExtractor> _log;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ClaudeTickerExtractor(
        HttpClient http,
        IOptions<AnthropicOptions> opts,
        ILogger<ClaudeTickerExtractor> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    /// <summary>
    /// Returns a new list with the same items but with Tickers populated for any item that
    /// was missing them. Items that already have tickers (from Finnhub `related`) are passed
    /// through untouched. Returns the input unchanged if the API call fails.
    /// </summary>
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

        // Build a compact numbered list — short tokens, one line per article.
        var sb = new System.Text.StringBuilder(needExtraction.Count * 80);
        for (int i = 0; i < needExtraction.Count; i++)
        {
            sb.Append(i).Append(". [").Append(needExtraction[i].Item.Source).Append("] ");
            sb.AppendLine(needExtraction[i].Item.Headline);
        }

        var body = new
        {
            model = _opts.Model,
            max_tokens = Math.Max(_opts.MaxTokens, needExtraction.Count * 25 + 200),
            system = new object[]
            {
                new {
                    type = "text",
                    text = SystemPrompt,
                    cache_control = new { type = "ephemeral" }
                }
            },
            messages = new object[]
            {
                new { role = "user", content = sb.ToString() }
            }
        };

        ExtractorResponse? response;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = JsonContent.Create(body)
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Ticker extractor API call failed ({Status}): {Body}", resp.StatusCode, err);
                return items;
            }
            response = await resp.Content.ReadFromJsonAsync<ExtractorResponse>(Json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ticker extractor API call threw");
            return items;
        }

        var text = response?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("Ticker extractor returned no content");
            return items;
        }

        // Tolerate code-fence wrap if the model slipped one in.
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
            _log.LogWarning(ex, "Failed to parse extractor JSON. Raw: {Raw}", text);
            return items;
        }
        if (parsed?.Results is null) return items;

        // Re-stamp the input items with extracted tickers.
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

        _log.LogInformation("Ticker extractor: {Sent} headlines → {Tagged} tickered ({Usage} tokens out, {InputTokens} in, {Cached} cached)",
            needExtraction.Count, tagged,
            response?.Usage?.OutputTokens ?? 0,
            response?.Usage?.InputTokens ?? 0,
            response?.Usage?.CacheReadInputTokens ?? 0);

        return output;
    }

    // --- Anthropic wire types (subset) ---
    private sealed class ExtractorResponse
    {
        [JsonPropertyName("content")] public ContentBlock[]? Content { get; set; }
        [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    }
    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class Usage
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public int CacheReadInputTokens { get; set; }
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
