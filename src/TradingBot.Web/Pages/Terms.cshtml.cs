using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradingBot.Web.Pages;

[AllowAnonymous]
public sealed class TermsModel : PageModel
{
    /// <summary>Bump this version string whenever the terms change. Users who accepted an older
    /// version get re-prompted on next sign-in.</summary>
    public const string CurrentVersion = "2026-06-18-public-v1";
}
