namespace TradingBot.Services.Macro;

/// <summary>
/// Abstraction over prediction-market data sources (Polymarket, Manifold, etc.).
/// Implementations should be tolerant of upstream failures and return an empty
/// snapshot rather than throwing.
/// </summary>
public interface IMacroProvider
{
    /// <summary>Display name shown on the dashboard so the user knows which source they're reading.</summary>
    string SourceName { get; }

    /// <summary>Fetch the latest filtered/curated market list.</summary>
    Task<MacroSnapshot> FetchAsync(CancellationToken ct);
}
