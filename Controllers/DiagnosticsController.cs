using AttendanceMonitoring.Data;
using AttendanceMonitoring.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin-only diagnostics for verifying integrations (currently just an
/// outbound email smoke test). Intentionally tiny — delete this controller
/// once you no longer need to confirm the mail pipeline works.
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("Diagnostics")]
public class DiagnosticsController : AppController
{
    private readonly IEmailSender _email;
    private readonly IOptionsMonitor<EmailOptions> _emailOptions;

    public DiagnosticsController(
        AppDbContext db,
        IEmailSender email,
        IOptionsMonitor<EmailOptions> emailOptions) : base(db)
    {
        _email = email;
        _emailOptions = emailOptions;
    }

    /// <summary>
    /// Shows the resolved email configuration so we can confirm at a glance
    /// whether the SMTP credentials and sender address arrived from the
    /// Render env vars. The password is replaced with a length indicator so
    /// it's never echoed back over HTTP.
    /// </summary>
    [HttpGet("EmailConfig")]
    public IActionResult EmailConfig()
    {
        var o = _emailOptions.CurrentValue;
        string Mask(string? s) =>
            string.IsNullOrEmpty(s) ? "(blank)" : $"({s.Length} chars)";
        var lines = new[]
        {
            $"Provider     = {o.Provider}",
            $"Enabled      = {o.Enabled}",
            $"Host         = {(string.IsNullOrEmpty(o.Host) ? "(blank)" : o.Host)}",
            $"Port         = {o.Port}",
            $"EnableSsl    = {o.EnableSsl}",
            $"Username     = {(string.IsNullOrEmpty(o.Username) ? "(blank)" : o.Username)}",
            $"Password     = {Mask(o.Password)}",
            $"FromAddress  = {(string.IsNullOrEmpty(o.FromAddress) ? "(blank)" : o.FromAddress)}",
            $"FromName     = {o.FromName}",
        };
        return Content(string.Join("\n", lines), "text/plain");
    }

    /// <summary>
    /// Sends a single test message via the configured <see cref="IEmailSender"/>.
    /// Usage: <c>GET /Diagnostics/SendTestEmail?to=someone@example.com</c>.
    /// Returns plain text so it's easy to read in a browser tab.
    /// </summary>
    [HttpGet("SendTestEmail")]
    public async Task<IActionResult> SendTestEmail(string to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            return Content("Missing ?to= query parameter.", "text/plain");
        }

        var subject = "Attendance Monitoring — test email";
        var body =
            "This is a test email sent from the Attendance Monitoring app "
          + "via the configured IEmailSender.\r\n\r\n"
          + $"Sent at (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\r\n"
          + $"Triggered by: {User.Identity?.Name ?? "(unknown)"}\r\n";

        try
        {
            await _email.SendAsync(new[] { to.Trim() }, subject, body, ct);
            return Content(
                $"OK — handed off to email sender. Check {to} (and the SMTP "
              + "provider's sent log). If nothing arrives, see /Diagnostics/EmailConfig.",
                "text/plain");
        }
        catch (Exception ex)
        {
            // Surface the real exception text so we can diagnose SMTP auth
            // / sender-not-verified failures in one round-trip.
            return Content(
                "FAILED to send. " + ex.GetType().Name + ": " + ex.Message
                + (ex.InnerException is null
                    ? string.Empty
                    : "\nInner: " + ex.InnerException.Message),
                "text/plain");
        }
    }

    /// <summary>
    /// Dumps the last N rows of <c>notification_log</c> as plain text so we
    /// can confirm whether the offline-during-shift notifier is actually
    /// reaching each user's mailbox (or which SMTP error stopped it).
    /// Usage: <c>GET /Diagnostics/EmailLog</c> or <c>?take=200</c>.
    /// </summary>
    [HttpGet("EmailLog")]
    public async Task<IActionResult> EmailLog(int take = 50, CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        if (take > 500) take = 500;

        var rows = await Db.NotificationLogs
            .OrderByDescending(n => n.SentAt)
            .Take(take)
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return Content(
                "No notification_log rows yet. The offline-notifier writes a row "
                + "every time it tries (or skips) an email; if this is empty, no "
                + "user has triggered the 15-min offline threshold during a shift.",
                "text/plain");
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"{rows.Count} most recent notification_log rows (newest first).");
        sb.AppendLine(
            "Status legend: Sent / Failed / NoRecipients / Disabled.");
        sb.AppendLine(new string('-', 100));
        foreach (var r in rows)
        {
            var when = PhTime.Format(r.SentAt, "yyyy-MM-dd HH:mm");
            sb.AppendLine(
                $"{when} PHT  {r.Status,-13} {r.Level,-9} "
                + $"{r.OfflineMinutes,3}m  {r.UserFullName}");
            sb.AppendLine($"   subject : {r.Subject}");
            sb.AppendLine($"   to      : {r.Recipients}");
            if (!string.IsNullOrEmpty(r.ErrorMessage))
            {
                sb.AppendLine($"   error   : {r.ErrorMessage}");
            }
            sb.AppendLine();
        }
        return Content(sb.ToString(), "text/plain");
    }
}
