namespace TradingBot.Web.Data;

/// <summary>
/// API keys per user. All four fields hold ciphertext produced by <c>ApiKeyProtector</c>; never the raw value.
/// </summary>
public sealed class UserApiKeys
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";

    public string? AlpacaKeyIdEncrypted { get; set; }
    public string? AlpacaSecretKeyEncrypted { get; set; }
    public bool AlpacaPaperMode { get; set; } = true;     // safety: paper by default

    public string? FinnhubApiKeyEncrypted { get; set; }
    public string? GeminiApiKeyEncrypted { get; set; }
    public string? AnthropicApiKeyEncrypted { get; set; }
    public string? GrokApiKeyEncrypted { get; set; }
}
