namespace TradingBot.Services.Risk;

public enum ExitTrigger
{
    StopLoss,
    TakeProfit,
    TrailingStop,
    TimeLimit
}

/// <summary>
/// One position the exit manager wants to close, plus the reason. The worker translates this
/// into a market sell after deduping against any already-pending sell orders.
/// </summary>
public sealed record ExitCandidate(
    string Ticker,
    long Quantity,
    ExitTrigger Trigger,
    string Reason,                   // human-readable, used in logs + dashboard
    double PnLPercent,
    DateTimeOffset? OpenedAt);
