using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradingBot.Web.Pages.Account;

[AllowAnonymous]
public sealed class RegisterModel : PageModel
{
    public IActionResult OnGet()
    {
        TempData["Flash"] = "Public signup is closed. Contact us if you need managed-instance or maintainer access.";
        return RedirectToPage("/Contact");
    }

    public IActionResult OnPost()
    {
        TempData["Flash"] = "Public signup is closed. Contact us if you need managed-instance or maintainer access.";
        return RedirectToPage("/Contact");
    }
}
