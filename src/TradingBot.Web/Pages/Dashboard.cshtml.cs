using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingBot.Web.Data;
using TradingBot.Web.Services;

namespace TradingBot.Web.Pages;

[Authorize]
public sealed class DashboardModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly UserBotHost _host;

    public DashboardModel(UserManager<AppUser> users, UserBotHost host)
    { _users = users; _host = host; }

    public bool BotRunning { get; set; }

    public Task<IActionResult> OnGetAsync()
    {
        // Status flag is purely cosmetic on the page — in demo mode we still show whether the
        // owner's bot is running, so we report on the OWNER's host slot rather than the demo
        // user's empty one.
        if (DemoMode.IsDemo(User))
        {
            // Look up the owner's UserId synchronously via UserManager — small DB round-trip.
            var ownerUser = _users.FindByEmailAsync(DemoMode.OwnerEmail).GetAwaiter().GetResult();
            if (ownerUser is not null) BotRunning = _host.IsRunning(ownerUser.Id);
        }
        else
        {
            var uid = _users.GetUserId(HttpContext.User)!;
            BotRunning = _host.IsRunning(uid);
        }
        return Task.FromResult<IActionResult>(Page());
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken ct)
    {
        if (DemoMode.IsDemo(User))
        {
            TempData["Flash"] = DemoMode.WriteBlockedFlash;
            return RedirectToPage("/Dashboard");
        }
        var uid = _users.GetUserId(HttpContext.User)!;
        TempData["Flash"] = await _host.StartAsync(uid, ct);
        return RedirectToPage("/Dashboard");
    }

    public async Task<IActionResult> OnPostStopAsync(CancellationToken ct)
    {
        if (DemoMode.IsDemo(User))
        {
            TempData["Flash"] = DemoMode.WriteBlockedFlash;
            return RedirectToPage("/Dashboard");
        }
        var uid = _users.GetUserId(HttpContext.User)!;
        TempData["Flash"] = await _host.StopAsync(uid, ct);
        return RedirectToPage("/Dashboard");
    }
}
