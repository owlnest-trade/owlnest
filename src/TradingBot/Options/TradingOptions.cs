namespace TradingBot.Options;

public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    /// <summary>
    /// Master kill-switch. When false, the bot still pulls news, runs sentiment, and logs
    /// what it would have done, but does NOT place any orders. Default false for safety.
    /// </summary>
    public bool TradingEnabled { get; set; } = false;

    /// <summary>List of tickers to monitor for news. Set in appsettings.json (Trading:Universe).</summary>
    public string[] Universe { get; set; } = [];

    /// <summary>How often the main loop ticks, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Minimum sentiment confidence from Claude (0.0–1.0) required to trade.</summary>
    public double MinConfidence { get; set; } = 0.75;

    /// <summary>Max fraction of portfolio equity allowed in any single position. 0.05 = 5%.</summary>
    public double MaxPositionFraction { get; set; } = 0.05;

    /// <summary>Daily loss cap as fraction of starting equity. If breached, kill switch trips for the day.</summary>
    public double MaxDailyLossFraction { get; set; } = 0.02;

    /// <summary>Max number of new trades the bot is allowed to open per calendar day.</summary>
    public int MaxTradesPerDay { get; set; } = 10;

    /// <summary>
    /// When true, only trade during regular US market hours (9:30am–4pm ET). When false,
    /// the bot would queue orders for the next open — currently unused, paper API auto-rejects after hours.
    /// </summary>
    public bool RegularHoursOnly { get; set; } = true;

    /// <summary>Where to persist processed-news IDs across restarts.</summary>
    public string StateDirectory { get; set; } = "state";
}
