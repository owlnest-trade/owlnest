using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingBot.Web.Data;

namespace TradingBot.Web.Pages.Account;

/// <summary>
/// Two-step email-first login for existing accounts only.
/// </summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users, ILogger<LoginModel> log)
    {
        _signIn = signIn;
        _users = users;
        _log = log;
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public bool EmailKnown { get; set; }
    public bool EmailUnknown { get; set; }

    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [DataType(DataType.Password)] public string Password { get; set; } = "";
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostCheckEmailAsync()
    {
        ModelState.Remove("Input.Password");
        if (!ModelState.IsValid) return Page();

        var trimmedEmail = Input.Email.Trim();
        var user = await _users.FindByEmailAsync(trimmedEmail);
        if (user is null)
        {
            _log.LogInformation("Login lookup: no account for {Email}", Input.Email);
            EmailUnknown = true;
            return Page();
        }

        EmailKnown = true;
        return Page();
    }

    public async Task<IActionResult> OnPostSignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Password is required.");
            EmailKnown = true;
            return Page();
        }
        if (!ModelState.IsValid)
        {
            EmailKnown = true;
            return Page();
        }

        var user = await _users.FindByEmailAsync(Input.Email.Trim());
        if (user is null)
        {
            EmailUnknown = true;
            return Page();
        }

        var check = await _signIn.CheckPasswordSignInAsync(user, Input.Password, lockoutOnFailure: false);
        if (check.IsLockedOut)
        {
            ModelState.AddModelError("", "Account is locked. Wait a few minutes and try again.");
            EmailKnown = true;
            return Page();
        }
        if (!check.Succeeded)
        {
            _log.LogInformation("Login failed: wrong password for {Email}", Input.Email);
            ModelState.AddModelError("", "Wrong password.");
            EmailKnown = true;
            return Page();
        }

        await _signIn.SignInAsync(user, isPersistent: true);
        _log.LogInformation("Login OK for {Email}", Input.Email);
        return RedirectToPage("/Dashboard");
    }
}
