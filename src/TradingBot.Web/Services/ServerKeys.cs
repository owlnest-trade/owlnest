namespace TradingBot.Web.Services;

/// <summary>
/// Platform-owned API keys. Live in user-secrets (dev) or App Service Configuration (prod) —
/// NEVER in appsettings.json. Bound from configuration section "ServerKeys".
///
/// Users no longer paste these — owlnest pays for them at the platform level and meters
/// usage per subscription tier.
/// </summary>
public sealed class ServerKeys
{
    public string Finnhub { get; set; } = "";
    public string Gemini { get; set; } = "";
    /// <summary>Optional — only used if a Plus/Pro user enables Grok trending or the Grok 2nd-opinion gate.</summary>
    public string Grok { get; set; } = "";
    /// <summary>Optional — used by the Pro-tier Claude verification gate. Uses Anthropic's web_search tool.</summary>
    public string Anthropic { get; set; } = "";

    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    /// <summary>Claude model for the verification gate. Sonnet is the cost/quality sweet spot.</summary>
    public string AnthropicModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>
    /// Groq Cloud key (https://console.groq.com) — used when LlmProvider = "Llama".
    /// NOT to be confused with <see cref="Grok"/> (xAI's product). Groq hosts open-weights models
    /// like Llama for sentiment classification at a fraction of Gemini's price, with no aggressive
    /// safety filter on financial content.
    /// </summary>
    public string Llama { get; set; } = "";
    /// <summary>
    /// Groq model identifier. Defaults to Llama 3.3 70B — the most battle-tested classifier on
    /// Groq's catalog with reliable JSON output without needing response_format=json_object.
    /// We tried Llama 4 Scout (17B MoE) first; it has a known bug where strict JSON mode causes
    /// premature stops mid-token, returning ~46-char truncated responses regardless of max_tokens.
    /// Older llama-3.1-8b-instant was retired by Groq. openai/gpt-oss-* are reasoning models
    /// that burn most of their budget on chain-of-thought. 3.3-70b stays the sweet spot.
    /// </summary>
    public string LlamaModel { get; set; } = "llama-3.3-70b-versatile";

    // ── Postmark (transactional email) ──
    /// <summary>Postmark Server Token. When set, contact-form submissions also email the owner.</summary>
    public string PostmarkServerToken { get; set; } = "";
    /// <summary>Postmark requires a verified Sender Signature. Configure DNS + verify the domain on postmarkapp.com.</summary>
    public string PostmarkFromEmail { get; set; } = "noreply@owlnest.trade";
    /// <summary>Where contact-form notifications go. Defaults to the owlnest owner.</summary>
    public string PostmarkToEmail { get; set; } = "rani.adam@gmail.com";

    public bool IsFinnhubReady   => !string.IsNullOrWhiteSpace(Finnhub);
    public bool IsGeminiReady    => !string.IsNullOrWhiteSpace(Gemini);
    public bool IsGrokReady      => !string.IsNullOrWhiteSpace(Grok);
    public bool IsAnthropicReady => !string.IsNullOrWhiteSpace(Anthropic);
    public bool IsLlamaReady     => !string.IsNullOrWhiteSpace(Llama);
    public bool IsPostmarkReady  => !string.IsNullOrWhiteSpace(PostmarkServerToken);
}
