using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TradingBot.Web.Pages;

[Authorize]
public sealed class ReportsModel : PageModel { }
