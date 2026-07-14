namespace TradingBot.Web.Data;

/// <summary>
/// Audit trail for every Grok or Claude verification-gate call the bot makes — captures the
/// prompt we sent, the raw model response, the parsed verdict, and latency. Lets us debug
/// "why did Grok veto?" / "is Claude giving sensible answers?" without needing to replay live
/// trades through expensive API calls.
///
/// Written from <see cref="Services.UserBotInstance"/> immediately after each Grok/Claude call.
/// One row per call. The verification gates only run on candidates that survived all earlier
/// gates, so the row count is bounded by real buy-attempts (≤15-30/day for a typical user).
/// </summary>
public sealed class UserGateCall
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }

    /// <summary>"Grok" | "Claude" — which verification gate this call was for.</summary>
    public string Gate { get; set; } = "";

    /// <summary>The specific model invoked (e.g. "grok-3-mini", "claude-sonnet-4-5").</summary>
    public string ModelName { get; set; } = "";

    public string Ticker { get; set; } = "";
    public string Source { get; set; } = "";
    public string Headline { get; set; } = "";

    /// <summary>The user-prompt we sent to the model (truncated to ~8 KB).</summary>
    public string Prompt { get; set; } = "";

    /// <summary>The raw response body the model returned (truncated to ~8 KB).</summary>
    public string RawResponse { get; set; } = "";

    /// <summary>Parsed verdict: "Approve" | "Caution" | "Veto" | "Error".</summary>
    public string Verdict { get; set; } = "";

    /// <summary>The model's one-line reason for its verdict.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Wall-clock latency of the API call (network + model inference + tool calls).</summary>
    public int LatencyMs { get; set; }
}
