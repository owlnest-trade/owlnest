using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingBot.Web.Data;

namespace TradingBot.Web.Pages.Account;

public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    public LogoutModel(SignInManager<AppUser> signIn) { _signIn = signIn; }

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToPage("/Index");
    }
}
