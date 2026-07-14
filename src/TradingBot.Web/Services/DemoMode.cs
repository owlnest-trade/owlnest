using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using TradingBot.Web.Data;

namespace TradingBot.Web.Services;

/// <summary>
/// Demo mode is disabled for the public/open-source release. The type remains so older pages and
/// API code can compile while all requests resolve to the authenticated user's own data.
/// </summary>
public static class DemoMode
{
    public const string OwnerEmail = "";
    public const string WriteBlockedFlash = "Demo mode is disabled.";

    public static bool IsDemo(ClaimsPrincipal user) => false;

    public static Task<string?> EffectiveUserIdAsync(
        ClaimsPrincipal user,
        UserManager<AppUser> users,
        OwlNestDbContext db,
        CancellationToken ct = default)
    {
        return Task.FromResult(users.GetUserId(user));
    }

    public static Task EnsureDemoUserAsync(UserManager<AppUser> users, ILogger logger)
    {
        logger.LogInformation("Demo user seeding is disabled");
        return Task.CompletedTask;
    }
}
