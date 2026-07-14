using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TradingBot.Web.Data;
using TradingBot.Web.Services;

namespace TradingBot.Web.Pages;

[Authorize]
public sealed class SettingsModel : PageModel
{
    private readonly OwlNestDbContext _db;
    private readonly UserManager<AppUser> _users;

    public SettingsModel(OwlNestDbContext db, UserManager<AppUser> users)
    { _db = db; _users = users; }

    [BindProperty] public UserSettings Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        // For demo users we want the page to render the OWNER's settings so the visitor can see
        // how the real bot is configured. The form is still editable in the browser (we don't try
        // to disable every input — too invasive) but the OnPost handler refuses to save.
        var uid = await DemoMode.EffectiveUserIdAsync(User, _users, _db);
        if (uid is null) return Page();
        Input = await _db.UserSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == uid)
                ?? new UserSettings { UserId = uid };
        // The Tier <select> only has options for the current public tier names. If the DB still
        // holds the legacy "Free" string we'd render an unselected dropdown, which a Save would
        // turn into the default Starter unexpectedly. Normalize before binding so the dropdown
        // pre-selects "Starter" for those users.
        Input.Tier = TierPolicy.Normalize(Input.Tier);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (DemoMode.IsDemo(User))
        {
            TempData["Flash"] = DemoMode.WriteBlockedFlash;
            return RedirectToPage("/Settings");
        }
        var uid = _users.GetUserId(User)!;
        var s = await _db.UserSettings.FirstOrDefaultAsync(x => x.UserId == uid);
        if (s is null)
        {
            Input.UserId = uid;
            // Normalize tier to canonical Starter / Plus / Pro values
            Input.Tier = TierPolicy.Normalize(Input.Tier);
            _db.UserSettings.Add(Input);
        }
        else
        {
            // Copy each field so EF tracks the update on the original row.
            // Subscription tier (with normalization)
            s.Tier = TierPolicy.Normalize(Input.Tier);
            // Master switches
            s.TradingEnabled = Input.TradingEnabled;
            s.RegularHoursOnly = Input.RegularHoursOnly;
            // News
            s.UseFinnhub = Input.UseFinnhub;
            s.FinnhubLookbackMinutes = Input.FinnhubLookbackMinutes;
            s.UseSecEdgar = Input.UseSecEdgar;
            s.SecEdgarContactEmail = Input.SecEdgarContactEmail ?? "";
            s.SecEdgarForm8K = Input.SecEdgarForm8K;
            s.SecEdgarForm10Q = Input.SecEdgarForm10Q;
            s.SecEdgarForm10K = Input.SecEdgarForm10K;
            s.UseInsiderTransactions = Input.UseInsiderTransactions;
            s.UseGoogleNews = Input.UseGoogleNews;
            s.UseFomcMacro = Input.UseFomcMacro;
            s.ClaudeConfirmationEnabled = Input.ClaudeConfirmationEnabled;
            s.ClaudeAdvisorMode = Input.ClaudeAdvisorMode;
            s.UseGrokTrending = Input.UseGrokTrending;
            s.GrokPollIntervalSeconds = Input.GrokPollIntervalSeconds;
            s.GrokConfirmationEnabled = Input.GrokConfirmationEnabled;
            // Discovery
            s.DiscoveryEnabled = Input.DiscoveryEnabled;
            s.DiscoveryExtractorIntervalSeconds = Input.DiscoveryExtractorIntervalSeconds;
            s.DiscoveryBuzzThreshold = Input.DiscoveryBuzzThreshold;
            s.DiscoveryBuzzWindowMinutes = Input.DiscoveryBuzzWindowMinutes;
            s.DiscoveryWatchlistTtlHours = Input.DiscoveryWatchlistTtlHours;
            // Macro
            s.MacroSource = Input.MacroSource ?? "Manifold";
            s.MacroPollIntervalSeconds = Input.MacroPollIntervalSeconds;
            // LLM
            s.LlmProvider = Input.LlmProvider ?? "Gemini";
            s.GeminiModel = Input.GeminiModel ?? "gemini-2.5-flash";
            s.AnthropicModel = Input.AnthropicModel ?? "claude-haiku-4-5";
            s.LlamaModel = Input.LlamaModel ?? "llama-3.1-8b-instant";
            // Entry
            s.MinConfidence = Input.MinConfidence;
            s.RequiredSignalCount = Input.RequiredSignalCount;
            s.ConfirmationWindowMinutes = Input.ConfirmationWindowMinutes;
            s.EarningsBlackoutEnabled = Input.EarningsBlackoutEnabled;
            s.EarningsBlackoutHours = Input.EarningsBlackoutHours;
            // Sizing + risk
            s.MaxPositionFraction = Input.MaxPositionFraction;
            s.MaxDailyLossFraction = Input.MaxDailyLossFraction;
            s.MaxTradesPerDay = Input.MaxTradesPerDay;
            s.PollIntervalSeconds = Input.PollIntervalSeconds;
            // Exits
            s.StopLossType = Input.StopLossType ?? "Both";
            s.StopLossPercent = Input.StopLossPercent;
            s.TakeProfitPercent = Input.TakeProfitPercent;
            s.TrailingStopPercent = Input.TrailingStopPercent;
            s.TrailingStopActivationPercent = Input.TrailingStopActivationPercent;
            s.MaxHoldDays = Input.MaxHoldDays;
            s.BearishNewsExitsEnabled = Input.BearishNewsExitsEnabled;
            s.BearishNewsMinConfidence = Input.BearishNewsMinConfidence;
            s.StopArmDelayMinutes = Input.StopArmDelayMinutes;
            // Universe
            s.UniverseCsv = Input.UniverseCsv ?? "";
            s.CryptoUniverseCsv = Input.CryptoUniverseCsv ?? "";
            // Personal trading rules
            s.BlacklistedTickersCsv = Input.BlacklistedTickersCsv ?? "";
            s.BlockedKeywordsCsv = Input.BlockedKeywordsCsv ?? "";
            s.BoostKeywordsCsv = Input.BoostKeywordsCsv ?? "";
            s.NoTradeMinutesAfterOpen = Input.NoTradeMinutesAfterOpen;
            s.NoTradeMinutesBeforeClose = Input.NoTradeMinutesBeforeClose;
            s.MinHoldMinutes = Input.MinHoldMinutes;
        }
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Settings saved.";

        // Round-trip the active tab so the post-save redirect lands on the same tab.
        // The tab name comes back as a plain form field named "ActiveTab" set by the
        // page's tab JS just before submit. Falls back to nothing (#overview) on missing/empty.
        var activeTab = (Request.Form["ActiveTab"].ToString() ?? "").Trim().ToLowerInvariant();
        var validTabs = new[] { "overview", "news", "verify", "rules", "risk" };
        var anchor = validTabs.Contains(activeTab) ? "#" + activeTab : "";
        return Redirect("/Settings" + anchor);
    }
}
