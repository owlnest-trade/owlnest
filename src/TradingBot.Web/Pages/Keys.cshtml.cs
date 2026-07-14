using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TradingBot.Web.Data;
using TradingBot.Web.Services;

namespace TradingBot.Web.Pages;

[Authorize]
public sealed class KeysModel : PageModel
{
    private readonly OwlNestDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly ApiKeyProtector _protector;

    public KeysModel(OwlNestDbContext db, UserManager<AppUser> users, ApiKeyProtector protector)
    { _db = db; _users = users; _protector = protector; }

    [BindProperty] public InputModel Input { get; set; } = new();
    public MaskedKeys Masks { get; private set; } = new();
    public bool CurrentlyLive { get; private set; }

    public sealed class InputModel
    {
        public string? AlpacaKeyId { get; set; }
        public string? AlpacaSecretKey { get; set; }
        /// <summary>"Paper" (default) or "Live". Bound from the radio buttons on the form.</summary>
        public string Mode { get; set; } = "Paper";
        /// <summary>Required to be true when Mode=Live; ignored otherwise.</summary>
        public bool AcknowledgeLiveRisk { get; set; }
        /// <summary>Required to literally equal "LIVE" when Mode=Live; ignored otherwise.</summary>
        public string LiveConfirmation { get; set; } = "";
    }

    public sealed class MaskedKeys
    {
        public string AlpacaKeyId { get; set; } = "(not set)";
        public string AlpacaSecretKey { get; set; } = "(not set)";
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // Demo mode: show "demo" stand-in values instead of the owner's real keys. Even masked,
        // exposing the owner's Alpaca key prefix isn't something we want to do.
        if (DemoMode.IsDemo(User))
        {
            Masks = new MaskedKeys
            {
                AlpacaKeyId = "(hidden in demo mode)",
                AlpacaSecretKey = "(hidden in demo mode)",
            };
            CurrentlyLive = false;
            Input.Mode = "Paper";
            return;
        }
        var uid = _users.GetUserId(User)!;
        var k = await _db.UserApiKeys.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid);
        if (k is null) return;
        Masks = new MaskedKeys
        {
            AlpacaKeyId      = ApiKeyProtector.Mask(_protector.Unprotect(k.AlpacaKeyIdEncrypted)),
            AlpacaSecretKey  = ApiKeyProtector.Mask(_protector.Unprotect(k.AlpacaSecretKeyEncrypted)),
        };
        CurrentlyLive = !k.AlpacaPaperMode;
        Input.Mode = k.AlpacaPaperMode ? "Paper" : "Live";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (DemoMode.IsDemo(User))
        {
            TempData["Flash"] = DemoMode.WriteBlockedFlash;
            return RedirectToPage("/Keys");
        }
        var uid = _users.GetUserId(User)!;
        var k = await _db.UserApiKeys.FirstOrDefaultAsync(x => x.UserId == uid)
                ?? throw new InvalidOperationException("Key bag missing for this account.");

        if (!string.IsNullOrWhiteSpace(Input.AlpacaKeyId))     k.AlpacaKeyIdEncrypted     = _protector.Protect(Input.AlpacaKeyId);
        if (!string.IsNullOrWhiteSpace(Input.AlpacaSecretKey)) k.AlpacaSecretKeyEncrypted = _protector.Protect(Input.AlpacaSecretKey);

        // ── Paper / Live mode — extra friction when switching to Live ──
        if (string.Equals(Input.Mode, "Live", StringComparison.OrdinalIgnoreCase))
        {
            if (!Input.AcknowledgeLiveRisk)
            {
                ModelState.AddModelError("Input.AcknowledgeLiveRisk", "You must tick the risk acknowledgement before enabling Live mode.");
                await LoadAsync();
                return Page();
            }
            if (!string.Equals(Input.LiveConfirmation?.Trim(), "LIVE", StringComparison.Ordinal))
            {
                ModelState.AddModelError("Input.LiveConfirmation", "Type LIVE (uppercase) to confirm you want to use real money.");
                await LoadAsync();
                return Page();
            }
            k.AlpacaPaperMode = false;
            TempData["Flash"] = "⚠️ LIVE MODE enabled. Real money will be used the next time you start your bot.";
        }
        else
        {
            k.AlpacaPaperMode = true;
            TempData["Flash"] = "Saved. Paper mode is active.";
        }

        await _db.SaveChangesAsync();
        return RedirectToPage("/Keys");
    }
}
