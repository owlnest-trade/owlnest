using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingBot.Web.Data;
using TradingBot.Web.Services;

namespace TradingBot.Web.Pages;

/// <summary>
/// Public Contact form. Anyone (logged in or not) can submit a message. Messages land in the
/// ContactMessages table (audit trail) AND get emailed to the owner via Postmark for instant
/// notification. The email send is fire-and-forget — if Postmark is unreachable or unconfigured,
/// the DB row is still authoritative.
/// </summary>
[AllowAnonymous]
public sealed class ContactModel : PageModel
{
    private readonly OwlNestDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly ILogger<ContactModel> _log;
    private readonly ContactNotifier _notifier;

    public ContactModel(OwlNestDbContext db, UserManager<AppUser> users, ILogger<ContactModel> log, ContactNotifier notifier)
    { _db = db; _users = users; _log = log; _notifier = notifier; }

    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Sent { get; set; }

    public sealed class InputModel
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Light validation — keep things friendly, return to the form with error on bad input.
        if (string.IsNullOrWhiteSpace(Input.Name) || Input.Name.Length > 200)
            ModelState.AddModelError("Input.Name", "Please tell us your name.");
        if (string.IsNullOrWhiteSpace(Input.Email) || !Input.Email.Contains('@') || Input.Email.Length > 200)
            ModelState.AddModelError("Input.Email", "We need a valid email so we can reply.");
        if (string.IsNullOrWhiteSpace(Input.Subject) || Input.Subject.Length > 200)
            ModelState.AddModelError("Input.Subject", "Add a short subject.");
        if (string.IsNullOrWhiteSpace(Input.Message) || Input.Message.Length > 4000)
            ModelState.AddModelError("Input.Message", "Your message looks empty or too long (max 4000 chars).");
        if (!ModelState.IsValid) return Page();

        var msg = new ContactMessage
        {
            SentAtUtc = DateTimeOffset.UtcNow,
            Name = Input.Name.Trim(),
            Email = Input.Email.Trim(),
            Subject = Input.Subject.Trim(),
            Message = Input.Message.Trim(),
            UserId = _users.GetUserId(User),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        };
        _db.ContactMessages.Add(msg);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Contact form: {Subj} from {Email}", msg.Subject, msg.Email);

        // Fire-and-forget Postmark notification. We don't await it — the form response should be
        // instant regardless of email delivery. The notifier swallows its own errors and logs.
        _ = _notifier.NotifyAsync(msg, CancellationToken.None);

        Sent = true;
        Input = new();   // clear form
        return Page();
    }
}
