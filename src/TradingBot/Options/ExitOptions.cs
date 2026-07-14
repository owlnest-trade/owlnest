namespace TradingBot.Options;

/// <summary>
/// Mechanical exit policy. Runs after every position snapshot, independent of news/sentiment.
/// For each held position, fires a market sell if any of the configured triggers cross:
///   - StopLoss: position is down by this fraction vs. average entry price.
///   - TakeProfit: position is up by this fraction vs. average entry price.
///   - MaxHoldDays: position has been held longer than this.
/// </summary>
public sealed class ExitOptions
{
    public const string SectionName = "Exits";

    /// <summary>Master switch. When false, no mechanical exits fire (news-driven exits still work).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sell if position down by this fraction. 0.05 = 5%. Set 0 to disable.</summary>
    public double StopLossPercent { get; set; } = 0.05;

    /// <summary>
    /// Hard ceiling take-profit. Sell if position up by this fraction regardless of trailing stop.
    /// Default 0 = disabled (trailing stop replaces it). Set e.g. 0.20 to add a +20% ceiling backstop.
    /// </summary>
    public double TakeProfitPercent { get; set; } = 0.0;

    /// <summary>
    /// Trailing stop: once a position has gained <see cref="TrailingStopActivationPercent"/> from entry,
    /// sell if the price ever drops by this fraction from the highest price seen since open. 0.015 = 1.5%.
    /// Set 0 to disable.
    /// </summary>
    public double TrailingStopPercent { get; set; } = 0.015;

    /// <summary>
    /// The trailing stop only arms once the position is up by this much from entry. Prevents the trail
    /// from firing during normal early-trade noise. 0.03 = +3%.
    /// </summary>
    public double TrailingStopActivationPercent { get; set; } = 0.03;

    /// <summary>Sell if held longer than this many days. Set 0 to disable.</summary>
    public int MaxHoldDays { get; set; } = 5;

    /// <summary>Where to persist the rolling per-position peak prices across restarts.</summary>
    public string PeaksFile { get; set; } = "state/position-peaks.json";

    /// <summary>Where to persist position open-times across restarts.</summary>
    public string OpenTimesFile { get; set; } = "state/position-opens.json";
}
