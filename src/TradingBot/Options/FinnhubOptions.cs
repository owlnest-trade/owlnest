namespace TradingBot.Options;

public sealed class FinnhubOptions
{
    public const string SectionName = "Finnhub";

    /// <summary>Finnhub API key. Loaded from user-secrets.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Base URL for the Finnhub REST API.</summary>
    public string BaseUrl { get; set; } = "https://finnhub.io/api/v1/";

    /// <summary>
    /// How far back to look for news on the first poll after startup.
    /// Subsequent polls only fetch news newer than the last-seen timestamp per ticker.
    /// </summary>
    public int InitialLookbackMinutes { get; set; } = 60;
}
