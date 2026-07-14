using Microsoft.AspNetCore.Identity;

namespace TradingBot.Web.Data;

public sealed class AppUser : IdentityUser
{
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DisplayName { get; set; } = "";

    // Lifecycle of this user's bot
    public bool BotRunning { get; set; } = false;
    public DateTimeOffset? BotStartedAtUtc { get; set; }

    /// <summary>When this user accepted the Risk Disclosure. Required at signup; nullable so
    /// EF doesn't blow up on existing rows during a schema migration.</summary>
    public DateTimeOffset? AcceptedTermsAtUtc { get; set; }

    /// <summary>The version of the Terms they accepted, so we can prompt re-acceptance if we update them.</summary>
    public string AcceptedTermsVersion { get; set; } = "";
}
