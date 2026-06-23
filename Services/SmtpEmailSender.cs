using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AttendanceMonitoring.Services;

/// <summary>
/// SMTP-backed email sender using MailKit. MailKit is preferred over the
/// built-in <see cref="System.Net.Mail.SmtpClient"/> because:
///   1. <see cref="System.Net.Mail.SmtpClient"/> is marked "not recommended
///      for new development" by Microsoft.
///   2. It only tries the first DNS result. On hosts where DNS returns an
///      IPv6 address but the container has no IPv6 route (e.g. Render's
///      free tier), the connect fails with "Network is unreachable" and
///      there's no fallback. MailKit iterates through every address
///      returned by DNS, so an unreachable IPv6 transparently falls back
///      to IPv4.
/// When <see cref="EmailOptions.Enabled"/> is false (or the host /
/// from-address is blank) the message is written to the log instead of
/// being sent — useful for testing without real credentials.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<EmailOptions> _options;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptionsMonitor<EmailOptions> options, ILogger<SmtpEmailSender> log)
    {
        _options = options;
        _log = log;
    }

    public async Task SendAsync(
        IEnumerable<string> to,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        var recipients = (to ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            _log.LogDebug("Email send skipped: no recipients.");
            return;
        }

        var opts = _options.CurrentValue;

        if (!opts.Enabled
            || string.IsNullOrWhiteSpace(opts.Host)
            || string.IsNullOrWhiteSpace(opts.FromAddress))
        {
            // When the operator *meant* to enable email but the credentials
            // aren't filled in, raise the log level so it's obvious why no
            // mail is going out — otherwise this looks identical to the
            // "intentionally disabled" case.
            if (opts.Enabled
                && (string.IsNullOrWhiteSpace(opts.FromAddress)
                    || string.IsNullOrWhiteSpace(opts.Host)))
            {
                _log.LogWarning(
                    "Email is Enabled=true but Host/FromAddress is blank in appsettings.json — alert NOT sent. subject='{Subject}' to=[{To}]",
                    subject, string.Join(", ", recipients));
            }
            else
            {
                _log.LogInformation(
                    "[Email DISABLED] subject='{Subject}' to=[{To}]\n{Body}",
                    subject, string.Join(", ", recipients), body);
            }
            return;
        }

        // SMTP relays (Brevo, SendGrid, Office 365, Gmail, ...) reject
        // anonymous submission. If the operator forgot to wire up the
        // Username/Password env vars, surface a clear warning instead of
        // letting the SMTP server respond with "5.7.0 Must issue STARTTLS"
        // or "5.7.1 Authentication required" — those error texts are easy
        // to miss in a busy log.
        if (string.IsNullOrWhiteSpace(opts.Username)
            || string.IsNullOrWhiteSpace(opts.Password))
        {
            _log.LogWarning(
                "Email is Enabled=true but Email:Username / Email:Password are blank "
                + "(check Render env vars Email__Username / Email__Password). "
                + "Message NOT sent. subject='{Subject}' to=[{To}]",
                subject, string.Join(", ", recipients));
            return;
        }

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(opts.FromName ?? string.Empty, opts.FromAddress));
        foreach (var r in recipients)
        {
            msg.To.Add(MailboxAddress.Parse(r));
        }
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        // Set priority header if configured
        if (opts.Priority == 1)
        {
            msg.Headers.Add("X-Priority", "1"); // High
            msg.Headers.Add("Priority", "urgent");
            msg.Headers.Add("Importance", "high");
        }
        else if (opts.Priority == 5)
        {
            msg.Headers.Add("X-Priority", "5"); // Low
            msg.Headers.Add("Priority", "non-urgent");
            msg.Headers.Add("Importance", "low");
        }
        // Default (3) is normal, no extra headers needed

        // Pick the right TLS mode for the configured port:
        //   587 → STARTTLS (the server speaks plain text, then upgrades).
        //   465 → SSL on connect (TLS handshake first, then SMTP).
        //   25  → no encryption (only used for internal relays).
        var tls = opts.Port switch
        {
            465 => SecureSocketOptions.SslOnConnect,
            587 => SecureSocketOptions.StartTls,
            25  => SecureSocketOptions.None,
            _   => opts.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
        };

        using var client = new SmtpClient
        {
            Timeout = 30_000,
        };

        try
        {
            await client.ConnectAsync(opts.Host, opts.Port, tls, ct);
            await client.AuthenticateAsync(opts.Username, opts.Password, ct);
            await client.SendAsync(msg, ct);
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, ct); } catch { /* ignore */ }
            }
        }

        _log.LogInformation(
            "Sent email '{Subject}' to {Count} recipient(s) via {Host}:{Port}",
            subject, recipients.Count, opts.Host, opts.Port);
    }
}
