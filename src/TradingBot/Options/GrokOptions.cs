namespace TradingBot.Options;

/// <summary>
/// xAI Grok integration. Used ONLY for the optional X/FinTwit trending-tickers discovery loop,
/// not for sentiment. Calls Grok with native X search enabled to ask "what's trending right now"
/// and pipes the resulting tickers into WatchlistManager so the main bot picks them up.
/// </summary>
public sealed class GrokOptions
{
    public const string SectionName = "Grok";

    /// <summary>Master switch. False (default until key is set) → trending worker doesn't run.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>xAI API key. Loaded from user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>xAI base URL (OpenAI-compatible chat-completions API).</summary>
    public string BaseUrl { get; set; } = "https://api.x.ai/";

    /// <summary>
    /// Model name. grok-3-mini is the cheap variant; grok-4-fast is the newer fast variant.
    /// Both are fine for "give me a JSON list of trending tickers."
    /// </summary>
    public string Model { get; set; } = "grok-3-mini";

    /// <summary>How often to poll Grok for the trending list. Default 1800s (30 min).</summary>
    public int PollIntervalSeconds { get; set; } = 1800;

    /// <summary>Max tickers to keep per poll.</summary>
    public int MaxTickersPerPoll { get; set; } = 15;
}
