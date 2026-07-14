using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingBot.Web.Data;

namespace TradingBot.Web.Pages.Account;

/// <summary>
/// Google OAuth for existing accounts only. New account creation is intentionally closed.
/// </summary>
[AllowAnonymous]
public sealed class ExternalLoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly ILogger<ExternalLoginModel> _log;

    public ExternalLoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, ILogger<ExternalLoginModel> log)
    {
        _signIn = signIn;
        _users = users;
        _log = log;
    }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet() => RedirectToPage("/Account/Login");

    public IActionResult OnPostExternal(string provider, string? returnUrl = null)
    {
        var redirectUrl = Url.Page("/Account/ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        var properties = _signIn.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return new ChallengeResult(provider, properties);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/Dashboard");
        if (remoteError is not null)
        {
            TempData["Flash"] = $"External sign-in failed: {remoteError}";
            return RedirectToPage("/Account/Login");
        }

        var info = await _signIn.GetExternalLoginInfoAsync();
        if (info is null)
        {
            TempData["Flash"] = "Could not load external login info.";
            return RedirectToPage("/Account/Login");
        }

        var existingSignIn = await _signIn.ExternalLoginSignInAsync(
            info.LoginProvider,
            info.ProviderKey,
            isPersistent: true,
            bypassTwoFactor: true);
        if (existingSignIn.Succeeded)
        {
            _log.LogInformation("User logged in via {Provider}", info.LoginProvider);
            return LocalRedirect(returnUrl);
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrEmpty(email))
        {
            var existingUser = await _users.FindByEmailAsync(email);
            if (existingUser is not null)
            {
                var linkResult = await _users.AddLoginAsync(existingUser, info);
                if (!linkResult.Succeeded)
                {
                    TempData["Flash"] = string.Join(" ", linkResult.Errors.Select(e => e.Description));
                    return RedirectToPage("/Account/Login");
                }

                await _signIn.SignInAsync(existingUser, isPersistent: true);
                _log.LogInformation("Linked {Provider} login to existing user {UserId}", info.LoginProvider, existingUser.Id);
                return LocalRedirect(returnUrl);
            }
        }

        TempData["Flash"] = "Public signup is closed. Contact us if you need managed-instance or maintainer access.";
        return RedirectToPage("/Contact");
    }

    public IActionResult OnPostConfirm()
    {
        TempData["Flash"] = "Public signup is closed. Contact us if you need managed-instance or maintainer access.";
        return RedirectToPage("/Contact");
    }
}
