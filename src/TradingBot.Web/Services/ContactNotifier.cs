using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TradingBot.Web.Data;

namespace TradingBot.Web.Services;

/// <summary>
/// Sends contact-form submissions to the owner's inbox via Postmark's transactional-email API.
///
/// Design notes:
///   - Fire-and-forget from the perspective of the HTTP request — the Contact form's POST handler
///     should never block on Postmark. We expose an async Task method anyway so the caller can
///     await it (e.g. tests, future "wait for confirmation" use cases), but the page handler kicks
///     it on a Task and moves on.
///   - Silent no-op when <see cref="ServerKeys.IsPostmarkReady"/> is false. Local dev runs (no
///     token) and prod-before-the-env-var-is-set both keep working — messages still hit
///     ContactMessages, just no email.
///   - We never throw out of <see cref="NotifyAsync"/> — Postmark being down isn't a reason for
///     the user's "Message sent" confirmation to flip to a 500.
///   - The visitor's email goes in ReplyTo so a one-tap reply on Gmail/Outlook goes straight to
///     the person who submitted the form. The From header is our verified sender (you can't put
///     the visitor's email there or Postmark rejects with InactiveRecipient).
/// </summary>
public sealed class ContactNotifier
{
    private const string PostmarkUrl = "https://api.postmarkapp.com/email";

    private readonly ServerKeys _keys;
    private readonly HttpClient _http;
    private readonly ILogger<ContactNotifier> _log;

    public ContactNotifier(IOptions<ServerKeys> keys, ILogger<ContactNotifier> log)
    {
        _keys = keys.Value;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task NotifyAsync(ContactMessage msg, CancellationToken ct = default)
    {
        if (!_keys.IsPostmarkReady)
        {
            _log.LogDebug("Postmark not configured — contact form notification skipped for {Email}", msg.Email);
            return;
        }

        try
        {
            var body = new
            {
                From = _keys.PostmarkFromEmail,
                To = _keys.PostmarkToEmail,
                ReplyTo = msg.Email,                     // hitting "Reply" goes to the visitor, not noreply
                Subject = $"[owlnest] {msg.Subject}",
                TextBody = BuildText(msg),
                HtmlBody = BuildHtml(msg),
                MessageStream = "outbound",              // Postmark's default transactional stream
                Tag = "contact-form",
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, PostmarkUrl)
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("X-Postmark-Server-Token", _keys.PostmarkServerToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Postmark send failed for {Email}: HTTP {Code} — {Body}",
                    msg.Email, (int)resp.StatusCode, err[..Math.Min(300, err.Length)]);
            }
            else
            {
                _log.LogInformation("Postmark notified for contact from {Email} subject \"{Subject}\"",
                    msg.Email, msg.Subject);
            }
        }
        catch (Exception ex)
        {
            // Never let an email failure surface to the user — the DB row is the source of truth.
            _log.LogWarning(ex, "Postmark send threw for contact from {Email}", msg.Email);
        }
    }

    private static string BuildText(ContactMessage m) => $"""
        New contact form submission on owlnest.trade

        From:    {m.Name} <{m.Email}>
        Subject: {m.Subject}
        IP:      {m.IpAddress ?? "(unknown)"}
        User:    {(m.UserId is null ? "(anonymous)" : m.UserId)}
        Time:    {m.SentAtUtc:yyyy-MM-dd HH:mm:ss} UTC

        ---
        {m.Message}
        ---

        Reply directly to this email to respond — it goes to {m.Email}.
        """;

    private static string BuildHtml(ContactMessage m) =>
        $$"""
        <!DOCTYPE html>
        <html>
        <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 24px; color: #1f2328; background: #f6f8fa;">
            <div style="background: white; border: 1px solid #d0d7de; border-radius: 8px; padding: 24px;">
                <h2 style="margin: 0 0 16px; color: #0969da;">📩 New owlnest contact</h2>
                <p style="margin: 0 0 4px;"><strong>From:</strong> {{System.Net.WebUtility.HtmlEncode(m.Name)}} &lt;{{System.Net.WebUtility.HtmlEncode(m.Email)}}&gt;</p>
                <p style="margin: 0 0 4px;"><strong>Subject:</strong> {{System.Net.WebUtility.HtmlEncode(m.Subject)}}</p>
                <p style="margin: 0 0 4px; color: #656d76; font-size: 12px;">IP: {{System.Net.WebUtility.HtmlEncode(m.IpAddress ?? "(unknown)")}} · User: {{System.Net.WebUtility.HtmlEncode(m.UserId ?? "(anonymous)")}}</p>
                <p style="margin: 0 0 16px; color: #656d76; font-size: 12px;">{{m.SentAtUtc:yyyy-MM-dd HH:mm:ss}} UTC</p>
                <div style="border-top: 1px solid #d0d7de; padding-top: 16px; white-space: pre-wrap; line-height: 1.6;">{{System.Net.WebUtility.HtmlEncode(m.Message)}}</div>
                <p style="margin: 16px 0 0; color: #656d76; font-size: 12px; border-top: 1px solid #d0d7de; padding-top: 12px;">
                    Hit Reply — your response goes straight to {{System.Net.WebUtility.HtmlEncode(m.Email)}}.
                </p>
            </div>
        </body>
        </html>
        """;
}
