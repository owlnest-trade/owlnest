namespace TradingBot.Services.Macro;

/// <summary>
/// One Polymarket market reduced to the fields the dashboard cares about.
/// Yes price is the implied probability of the "Yes" outcome (0.0–1.0).
/// </summary>
public sealed record MacroMarket(
    string Slug,
    string Question,
    double YesPrice,
    double Volume,
    DateTimeOffset? EndDate,
    string Url);

public sealed record MacroSnapshot(
    DateTimeOffset At,
    IReadOnlyList<MacroMarket> Markets);
