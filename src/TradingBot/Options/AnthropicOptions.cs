namespace TradingBot.Options;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Anthropic API key. Loaded from user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Anthropic Messages API base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com/";

    /// <summary>
    /// Model used for sentiment scoring. Default is the fast/cheap Haiku tier.
    /// For harder ambiguous cases you could route to a Sonnet/Opus tier later.
    /// </summary>
    public string Model { get; set; } = "claude-haiku-4-5";

    /// <summary>Max output tokens per sentiment call. Sentiment JSON is short so this stays small.</summary>
    public int MaxTokens { get; set; } = 400;

    /// <summary>API version header required by Anthropic.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";
}
