using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;
using SentimentEnum = TradingBot.Models.Sentiment;

namespace TradingBot.Services.Sentiment;

/// <summary>
/// Google Gemini-backed sentiment analyzer. Same contract as ClaudeSentimentAnalyzer: takes a
/// news item plus optional macro context, returns a structured verdict. Uses Gemini 2.0 Flash by
/// default — free tier covers our usage.
/// </summary>
public sealed class GeminiSentimentAnalyzer : ISentimentAnalyzer
{
    private readonly HttpClient _http;
    private readonly GeminiOptions _opts;
    private readonly ILogger<GeminiSentimentAnalyzer> _log;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public GeminiSentimentAnalyzer(
        HttpClient http,
        IOptions<GeminiOptions> opts,
        ILogger<GeminiSentimentAnalyzer> log)
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

        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = SentimentPrompts.SystemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPayload } }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = _opts.MaxSentimentTokens,
                responseMimeType = "application/json"
            }
        };

        var url = $"v1beta/models/{_opts.SentimentModel}:generateContent?key={Uri.EscapeDataString(_opts.ApiKey)}";

        GeminiResponse? response;
        try
        {
            using var resp = await _http.PostAsJsonAsync(url, body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Gemini API call failed ({Status}) for {Ticker}: {Body}",
                    resp.StatusCode, news.Ticker, err);
                return null;
            }
            response = await resp.Content.ReadFromJsonAsync<GeminiResponse>(Json, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gemini API call threw for {Ticker}", news.Ticker);
            return null;
        }

        var text = response?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.LogWarning("Gemini returned no text content for {Ticker}", news.Ticker);
            return null;
        }

        return TryParseVerdict(text, news.Ticker);
    }

    private SentimentResult? TryParseVerdict(string raw, string fallbackTicker)
    {
        // Gemini with responseMimeType=application/json gives clean JSON, but be defensive anyway.
        var json = raw.Trim();
        if (json.StartsWith("```"))
        {
            var nl = json.IndexOf('\n');
            if (nl > 0) json = json[(nl + 1)..];
            if (json.EndsWith("```")) json = json[..^3];
            json = json.Trim();
        }

        try
        {
            var verdict = JsonSerializer.Deserialize<VerdictJson>(json, Json);
            if (verdict is null) return null;

            var sentiment = verdict.Sentiment?.ToLowerInvariant() switch
            {
                "bullish" => SentimentEnum.Bullish,
                "bearish" => SentimentEnum.Bearish,
                _ => SentimentEnum.Neutral
            };

            return new SentimentResult(
                Ticker: string.IsNullOrWhiteSpace(verdict.Ticker) ? fallbackTicker : verdict.Ticker.ToUpperInvariant(),
                Sentiment: sentiment,
                Confidence: Math.Clamp(verdict.Confidence, 0.0, 1.0),
                IsActionable: verdict.IsActionable,
                Reasoning: verdict.Reasoning ?? "",
                Model: _opts.SentimentModel,
                EvaluatedAt: DateTimeOffset.UtcNow);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse Gemini sentiment JSON for {Ticker}. Raw: {Raw}", fallbackTicker, raw);
            return null;
        }
    }

    // --- Gemini wire types (subset) ---------------------------------------------------------
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
    private sealed class VerdictJson
    {
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("sentiment")] public string? Sentiment { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("is_actionable")] public bool IsActionable { get; set; }
        [JsonPropertyName("reasoning")] public string? Reasoning { get; set; }
    }
}
