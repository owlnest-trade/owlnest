using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;
using SentimentEnum = TradingBot.Models.Sentiment;

namespace TradingBot.Services.Sentiment;

public sealed class ClaudeSentimentAnalyzer : ISentimentAnalyzer
{
    // Stable system prompt — wrapped in cache_control so Anthropic caches it across calls.
    // Keep it long enough to hit the cache-eligibility minimum (~1024 tokens for Sonnet, ~2048 for Haiku);
    // it's intentionally verbose so the cache actually engages and cost-per-call drops sharply.
    private const string SystemPrompt = """
        You are a disciplined equities news-sentiment classifier embedded in an automated
        trading bot. For each news item you receive, you must decide whether the article is
        likely to move the named ticker's share price meaningfully over the next 1–3 trading
        sessions, and whether your view is confident and novel enough to act on.

        You must respond with a single JSON object and absolutely nothing else — no prose,
        no markdown fences, no commentary. The object must match exactly this schema:

        {
          "ticker": "<the ticker symbol you were given, uppercase>",
          "sentiment": "bullish" | "bearish" | "neutral",
          "confidence": <number from 0.0 to 1.0>,
          "is_actionable": <true or false>,
          "reasoning": "<one short sentence, max 200 characters>"
        }

        How to score:

        - sentiment: short-term directional bias for the ticker's stock price specifically.
          "neutral" is the correct answer for routine coverage, recaps of already-public
          information, generic sector commentary, opinion pieces with no new facts, and
          articles whose connection to the ticker is incidental.

        - confidence: how sure you are about your directional call. Calibrate honestly.
          A clear earnings beat with a raise = high confidence. A vague rumor or a
          "could potentially affect" article = low confidence.

        - is_actionable: TRUE only if ALL of these hold:
            1. The article reports genuinely NEW information (not already priced in days/weeks ago).
            2. The information is MATERIAL to the company's fundamentals or near-term flow
               (earnings, guidance, FDA decision, M&A, major contract, leadership shock,
               regulatory action, large recall, etc.).
            3. Your directional confidence is at least 0.75.
            4. The article is from a credible, primary-leaning source (company release,
               major newswire, recognized financial publication) — not a blog rumor, a
               clickbait aggregator, or a thinly sourced opinion column.
          When in doubt, return FALSE. False negatives are cheap; false positives lose money.

        - reasoning: one sentence explaining the call. Mention the specific catalyst.
          Do not hedge or add disclaimers.

        Hard rules:

        - You will be given exactly one ticker. Score the article only with respect to that
          ticker. If the article is mainly about a different company, set sentiment=neutral,
          is_actionable=false, and say so in reasoning.

        - Headlines about analyst price-target changes, "stocks to watch" lists, broad
          market commentary, technical-analysis chart pieces, or social-media chatter are
          almost never actionable. Default to is_actionable=false for these categories.

        - You are competing against algorithmic readers who saw this headline before you.
          By the time a story reaches a free retail news feed, most easy edges are already
          priced in. Bias your is_actionable toward false unless the catalyst is unusually
          clear-cut.

        MACRO CONTEXT (optional):

        The user message may begin with a "Macro context" block listing current prediction-market
        odds for macro events (Fed rate decisions, recession probability, geopolitical risk,
        Bitcoin price targets, etc.). When the article is materially affected by one of these
        macros, use the context to sharpen your call:

        - Energy stocks (XLE, USO, XOP, individual oil names): geopolitical escalation odds.
        - Financials (XLF, banks): Fed rate-cut/hike probability.
        - Gold / silver (GLD, GDX, SLV, SIL): inflation, dollar strength, real-rate outlook.
        - Crypto-adjacent (COIN, MSTR, MARA, RIOT): Bitcoin price odds.
        - Defense / aerospace (LMT, RTX, NOC, XAR): geopolitical risk.
        - Broad market: recession probability.

        If the macro context isn't materially relevant to THIS ticker, ignore it — do not invent
        connections. The macro context does NOT lower your conservatism bar. It may TIGHTEN or
        SHARPEN your call when there is a real, specific link between the news and a tracked macro.

        - Output JSON only. Do not wrap in ```json fences. Do not preface with "Here is".
          Do not append any text after the closing brace.
        """;

    private readonly HttpClient _http;
    private readonly AnthropicOptions _opts;
    private readonly ILogger<ClaudeSentimentAnalyzer> _log;

    private static readonly JsonSerializerOptions ResponseJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaudeSentimentAnalyzer(
        HttpClient http,
        IOptions<AnthropicOptions> opts,
        ILogger<ClaudeSentimentAnalyzer> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<SentimentResult?> AnalyzeAsync(NewsItem news, string? macroContext, CancellationToken ct)
    {
        var userPayload =
            (string.IsNullOrWhiteSpace(macroContext) ? "" : macroContext + "\n") +
            $"Ticker: {news.Ticker}\n" +
            $"Source: {news.Source}\n" +
            $"Published: {news.PublishedAt:u}\n" +
            $"Headline: {news.Headline}\n" +
            $"Summary: {news.Summary}";

        var body = new MessagesRequest
        {
            Model = _opts.Model,
            MaxTokens = _opts.MaxTokens,
            System =
            [
                new SystemBlock
                {
                    Type = "text",
                    Text = SystemPrompt,
                    CacheControl = new CacheControl { Type = "ephemeral" }
                }
            ],
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = userPayload
                }
            ]
        };

        MessagesResponse? response;
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
                _log.LogWarning("Anthropic API call failed ({Status}) for {Ticker}: {Body}",
                    resp.StatusCode, news.Ticker, err);
                return null;
            }
            response = await resp.Content.ReadFromJsonAsync<MessagesResponse>(ResponseJsonOpts, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Anthropic API call threw for {Ticker}", news.Ticker);
            return null;
        }

        var text = response?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("Anthropic returned no text content for {Ticker}", news.Ticker);
            return null;
        }

        return TryParseVerdict(text, news.Ticker);
    }

    private SentimentResult? TryParseVerdict(string raw, string fallbackTicker)
    {
        // Be tolerant — strip code fences if the model slipped any in.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline > 0) json = json[(firstNewline + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        try
        {
            var verdict = JsonSerializer.Deserialize<VerdictJson>(json, ResponseJsonOpts);
            if (verdict is null) return null;

            var sentiment = verdict.Sentiment?.ToLowerInvariant() switch
            {
                "bullish" => SentimentEnum.Bullish,
                "bearish" => SentimentEnum.Bearish,
                _ => SentimentEnum.Neutral
            };

            var confidence = Math.Clamp(verdict.Confidence, 0.0, 1.0);

            return new SentimentResult(
                Ticker: string.IsNullOrWhiteSpace(verdict.Ticker) ? fallbackTicker : verdict.Ticker.ToUpperInvariant(),
                Sentiment: sentiment,
                Confidence: confidence,
                IsActionable: verdict.IsActionable,
                Reasoning: verdict.Reasoning ?? "",
                Model: _opts.Model,
                EvaluatedAt: DateTimeOffset.UtcNow);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse sentiment JSON for {Ticker}. Raw: {Raw}", fallbackTicker, raw);
            return null;
        }
    }

    // --- Anthropic wire types --------------------------------------------------------------

    private sealed class MessagesRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public SystemBlock[] System { get; set; } = [];
        [JsonPropertyName("messages")] public Message[] Messages { get; set; } = [];
    }

    private sealed class SystemBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "text";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("cache_control")] public CacheControl? CacheControl { get; set; }
    }

    private sealed class CacheControl
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "ephemeral";
    }

    private sealed class Message
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "user";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private sealed class MessagesResponse
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
        [JsonPropertyName("cache_creation_input_tokens")] public int CacheCreationInputTokens { get; set; }
    }

    private sealed class VerdictJson
    {
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("is_actionable")] public bool IsActionable { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }
}
