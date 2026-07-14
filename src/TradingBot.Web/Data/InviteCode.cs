namespace TradingBot.Web.Data;

/// <summary>
/// One-time-use invite code. Owner generates these manually (via SQL Query Editor or future
/// admin page) and shares them with approved users via email or chat. Required at signup —
/// without a valid unused code, account creation is blocked.
/// </summary>
public sealed class InviteCode
{
    public int Id { get; set; }

    /// <summary>The actual code the user types. Case-insensitive lookup. Should be reasonably unguessable.</summary>
    public string Code { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form note for the owner — "issued to alice@example.com after Twitter DM" etc.</summary>
    public string Note { get; set; } = "";

    /// <summary>Optional — if set, only this email address can use the code.</summary>
    public string? RestrictedToEmail { get; set; }

    /// <summary>Null until used.</summary>
    public DateTimeOffset? UsedAtUtc { get; set; }

    /// <summary>Null until used.</summary>
    public string? UsedByUserId { get; set; }
}
