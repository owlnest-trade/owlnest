namespace TradingBot.Options;

/// <summary>
/// Manifold Markets prediction-market integration. Free public API, no auth, globally accessible
/// (no geoblock). Markets are play-money so signal quality is lower than real-money markets like
/// Polymarket — useful when Polymarket isn't reachable.
/// </summary>
public sealed class ManifoldOptions
{
    public const string SectionName = "Manifold";

    public string BaseUrl { get; set; } = "https://api.manifold.markets/";

    /// <summary>How many results to pull per keyword search.</summary>
    public int SearchLimitPerKeyword { get; set; } = 20;

    /// <summary>How many filtered markets to keep / show on the dashboard.</summary>
    public int KeepTopN { get; set; } = 20;

    /// <summary>Minimum total liquidity (Manifold-mana) for a market to be considered. Filters out hobbyist noise.</summary>
    public int MinLiquidity { get; set; } = 100;

    /// <summary>
    /// Keyword queries used as Manifold search terms. Set in appsettings.json so it's the single
    /// source of truth (binding concatenates arrays, so leave the default empty).
    /// </summary>
    public string[] SearchTerms { get; set; } = [];
}
