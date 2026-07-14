namespace TradingBot.Options;

/// <summary>
/// Pre-risk entry gates that sit between Claude's verdict and the risk manager. These exist to
/// reduce false positives on retail news feeds — confirmation that a single shoddy headline
/// won't trigger a buy, and earnings-blackout protection against gap risk.
/// </summary>
public sealed class EntryOptions
{
    public const string SectionName = "Entry";

    /// <summary>Master switch for the confirmation rule.</summary>
    public bool ConfirmationRequired { get; set; } = true;

    /// <summary>How long a prior actionable signal stays "current" for confirmation purposes.</summary>
    public int ConfirmationWindowMinutes { get; set; } = 120;

    /// <summary>How many same-direction actionable signals are needed before trading. 2 = current + 1 prior.</summary>
    public int RequiredSignalCount { get; set; } = 2;

    /// <summary>Master switch for the earnings blackout.</summary>
    public bool EarningsBlackoutEnabled { get; set; } = true;

    /// <summary>Reject buys if scheduled earnings fall within this many hours either side of "now".</summary>
    public int EarningsBlackoutHours { get; set; } = 24;
}
