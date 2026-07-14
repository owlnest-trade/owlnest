namespace TradingBot.Web.Data;

/// <summary>
/// One unit of "what the bot saw and what it decided" for this user. Persisted so the dashboard
/// survives restarts and so we can compute per-user analytics later.
/// </summary>
public sealed class UserDecision
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }

    public string Ticker { get; set; } = "";
    public string Source { get; set; } = "";
    public string Headline { get; set; } = "";
    public string? Url { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }

    public string? Sentiment { get; set; }
    public double? Confidence { get; set; }
    public bool? Actionable { get; set; }
    public string? Reasoning { get; set; }

    public string Outcome { get; set; } = "";           // string form of DecisionOutcome
    public string OutcomeReason { get; set; } = "";
    public string? Side { get; set; }
    public int? Quantity { get; set; }
    public string? OrderId { get; set; }
    public string? MacroSummary { get; set; }

    /// <summary>
    /// Market price for this ticker at the moment the decision was recorded. Used to compute
    /// "price drift" (how much did the price move between the article publishing, the bot reacting,
    /// and any resulting order filling). Null when the bot didn't fetch a price for this decision
    /// — typically early-rejection paths (blocked keyword, sentiment-skipped) skip the price call.
    /// </summary>
    public decimal? PriceUsd { get; set; }
}
