namespace TradingBot.Options;

public sealed class AlpacaOptions
{
    public const string SectionName = "Alpaca";

    /// <summary>Alpaca API Key ID. Loaded from user-secrets.</summary>
    public string KeyId { get; set; } = "";

    /// <summary>Alpaca API Secret Key. Loaded from user-secrets.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// When true (default) we use Alpaca's paper-trading endpoint and no real money is at risk.
    /// Flip to false only after you've reviewed performance over many weeks of paper trading.
    /// </summary>
    public bool UsePaperTrading { get; set; } = true;
}
