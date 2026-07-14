namespace TradingBot.Web.Data;

/// <summary>
/// A message sent through the public Contact form. Stored so the owner can review them from
/// the admin view (future). For now they just land in the local database.
/// </summary>
public sealed class ContactMessage
{
    public int Id { get; set; }
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>Optional — the logged-in user's ID if they were signed in when they submitted. Null for anonymous.</summary>
    public string? UserId { get; set; }

    /// <summary>Originating IP (for spam triage). Best-effort — may be the proxy IP on Azure App Service.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Owner toggle: mark as handled so it disappears from the open queue.</summary>
    public bool Handled { get; set; }
}
