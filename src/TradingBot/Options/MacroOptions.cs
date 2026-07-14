namespace TradingBot.Options;

/// <summary>
/// Shared macro pipeline settings. The actual market data source is selected by <see cref="Source"/>.
/// Source-specific tuning lives in the per-source options (PolymarketOptions, ManifoldOptions).
/// </summary>
public sealed class MacroOptions
{
    public const string SectionName = "Macro";

    public bool Enabled { get; set; } = true;

    /// <summary>Which prediction-market data source to use. "Manifold" (default) or "Polymarket".</summary>
    public string Source { get; set; } = "Manifold";

    /// <summary>How often the macro poller runs. Default 600s (10 min).</summary>
    public int PollIntervalSeconds { get; set; } = 600;
}
