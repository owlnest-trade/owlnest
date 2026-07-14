namespace TradingBot.Web.Services;

/// <summary>
/// Single source of truth for tier-gated behaviour. Anywhere in the bot that needs to know
/// "is this feature allowed on this tier?" goes through here so we don't drift across files.
///
/// Tier definitions (legacy/internal only; public pricing was removed):
///   Starter — 20 fixed equity tickers, 10 crypto symbols, 10-minute poll, no dynamic discovery, no AI verification.
///   Plus    — 30 fixed + 10 dynamic equity tickers, +commodities, 10 crypto symbols, 5-minute poll, Grok discovery + Grok 2nd-opinion gate.
///   Pro     — unlimited universe (stocks + crypto), 1-minute poll, Grok + Claude verification gates (stack both for double sign-off).
/// </summary>
public static class TierPolicy
{
    // Internal tier names. "Free" stays as a legacy alias because old DB rows may still have it;
    // Normalize maps it to Starter.
    public const string Starter = "Starter";
    public const string Plus    = "Plus";
    public const string Pro     = "Pro";

    /// <summary>Canonical tier name. Treats "Free" (legacy) as Starter and unknown values as Starter too.</summary>
    public static string Normalize(string? tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "pro"  => Pro,
        "plus" => Plus,
        _      => Starter,    // includes "starter", "free" (legacy), null, ""
    };

    /// <summary>Hard cap on the fixed-universe size.</summary>
    public static int MaxFixedTickers(string tier) => Normalize(tier) switch
    {
        Pro  => int.MaxValue,
        Plus => 30,
        _    => 20            // Starter
    };

    /// <summary>Hard cap on dynamic watchlist size (auto-discovered tickers).</summary>
    public static int MaxDynamicTickers(string tier) => Normalize(tier) switch
    {
        Pro  => int.MaxValue,
        Plus => 10,
        _    => 0             // Starter has no dynamic watchlist
    };

    /// <summary>Minimum allowed poll interval. Caller must clamp UserSettings.PollIntervalSeconds to >= this.</summary>
    public static int MinPollIntervalSeconds(string tier) => Normalize(tier) switch
    {
        Pro  => 60,           // 1 min
        Plus => 300,           // 5 min
        _    => 600            // Starter: 10 min
    };

    /// <summary>Starter cannot trade commodity ETFs. Plus and Pro can.</summary>
    private static readonly HashSet<string> CommodityTickers = new(StringComparer.OrdinalIgnoreCase)
    {
        "GLD", "GDX", "SLV", "SIL", "USO", "XLE", "DBA", "DBC", "PDBC", "USCI"
    };
    public static bool IsCommodityTicker(string ticker) => CommodityTickers.Contains(ticker);
    public static bool AllowsCommodities(string tier) => Normalize(tier) is Plus or Pro;

    /// <summary>
    /// Alpaca routes orders to its crypto venue based on the symbol containing a slash
    /// (e.g. BTC/USD, ETH/USD). The bot uses the same convention — anywhere we branch on
    /// "is this a crypto order or an equity order", we go through this helper.
    /// </summary>
    public static bool IsCryptoTicker(string ticker) =>
        !string.IsNullOrWhiteSpace(ticker) && ticker.Contains('/');

    /// <summary>Crypto trading is available on every tier. Empty CryptoUniverseCsv disables it.</summary>
    public static bool AllowsCrypto(string tier) => true;

    /// <summary>Hard cap on number of crypto symbols the universe can carry. Pro is unlimited.</summary>
    public static int MaxCryptoTickers(string tier) => Normalize(tier) switch
    {
        Pro  => int.MaxValue,
        _    => 10
    };

    /// <summary>
    /// Filter the user's typed crypto list down to what their tier allows + a clean canonical
    /// form. Normalizes to upper-case and rejects anything that doesn't look like SYMBOL/USD.
    /// </summary>
    public static IReadOnlyList<string> FilterCryptoUniverse(string tier, IEnumerable<string> symbols)
    {
        var maxFixed = MaxCryptoTickers(tier);
        return symbols
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(IsCryptoTicker)
            .Distinct()
            .Take(maxFixed)
            .ToList();
    }

    /// <summary>Dynamic discovery requires Plus+.</summary>
    public static bool AllowsDiscovery(string tier) => Normalize(tier) is Plus or Pro;

    /// <summary>Grok X-trending discovery (separate from confirmation gate) requires Plus+.</summary>
    public static bool AllowsGrokTrending(string tier) => Normalize(tier) is Plus or Pro;

    /// <summary>Grok 2nd-opinion gate (per-buy live X+web check) requires Plus+.</summary>
    public static bool AllowsGrokConfirmation(string tier) => Normalize(tier) is Plus or Pro;

    /// <summary>Claude verification gate (per-buy reasoning check with Anthropic's web_search) requires Pro.</summary>
    public static bool AllowsClaudeConfirmation(string tier) => Normalize(tier) == Pro;

    /// <summary>Filter a candidate universe (CSV or list) down to what this tier is allowed to trade.</summary>
    public static IReadOnlyList<string> FilterUniverse(string tier, IEnumerable<string> tickers)
    {
        var maxFixed = MaxFixedTickers(tier);
        var allowCommodity = AllowsCommodities(tier);
        return tickers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(t => allowCommodity || !IsCommodityTicker(t))
            .Distinct()
            .Take(maxFixed)
            .ToList();
    }
}
