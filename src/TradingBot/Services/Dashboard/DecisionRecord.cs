namespace TradingBot.Services.Dashboard;

/// <summary>
/// One unit of "what the bot looked at and what it decided" — surfaced to the dashboard.
/// Kept flat and serialization-friendly so it can be returned as JSON without further mapping.
/// </summary>
public sealed record DecisionRecord(
    DateTimeOffset At,
    string Ticker,
    string Source,                 // "Finnhub", "SEC EDGAR", ...
    string Headline,
    string? Url,
    DateTimeOffset PublishedAt,

    // Claude verdict (null if sentiment call failed and we skipped)
    string? Sentiment,             // "Bullish" | "Bearish" | "Neutral" | null
    double? Confidence,            // 0.0–1.0
    bool? Actionable,
    string? Reasoning,

    // Outcome
    DecisionOutcome Outcome,
    string OutcomeReason,
    string? Side,                  // "Buy" | "Sell" | null
    int? Quantity,
    string? OrderId,

    // Macro context that was passed to Claude (short, dashboard-friendly form). Null when none.
    string? MacroSummary = null
);

public enum DecisionOutcome
{
    SentimentSkipped,   // Claude returned null / errored
    NoTradeGate,        // sentiment present but didn't clear actionable+confidence bar
    Rejected,           // sentiment cleared its gate, but risk manager rejected
    Approved,           // risk manager approved (with TradingEnabled=false this is still the terminal state)
    Submitted           // order actually went to broker
}
