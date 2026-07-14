namespace TradingBot.Options;

/// <summary>
/// Top-level switch for which LLM provider powers sentiment + ticker extraction.
/// "Gemini" → Google Gemini 2.0 Flash (cheap/free).
/// "Anthropic" → Claude Haiku 4.5 (smarter, more expensive).
/// Provider-specific tuning lives in the per-provider options (AnthropicOptions, GeminiOptions).
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "Gemini";
}
