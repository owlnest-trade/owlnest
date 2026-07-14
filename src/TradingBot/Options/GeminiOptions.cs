namespace TradingBot.Options;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    /// <summary>Google AI Studio API key. Loaded from user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Gemini API base URL.</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";

    /// <summary>
    /// Sentiment model. gemini-2.0-flash is the right default for our use:
    /// fast, cheap, accepts response_mime_type=application/json for clean structured output.
    /// </summary>
    public string SentimentModel { get; set; } = "gemini-2.0-flash";

    /// <summary>Extractor model. Same default — Flash is plenty for ticker extraction.</summary>
    public string ExtractorModel { get; set; } = "gemini-2.0-flash";

    /// <summary>Max output tokens per sentiment call. Verdict JSON is small.</summary>
    public int MaxSentimentTokens { get; set; } = 400;

    /// <summary>Max output tokens for the ticker-extractor batch call.</summary>
    public int MaxExtractorTokens { get; set; } = 3000;
}
