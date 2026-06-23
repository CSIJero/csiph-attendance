using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Sends email via Brevo's HTTPS transactional API
/// (<c>POST https://api.brevo.com/v3/smtp/email</c>).
/// <para>
/// We need this because Render's free tier blocks all outbound SMTP
/// (ports 25/465/587). Brevo's HTTP API is the cheapest way around the
/// block while keeping the existing Brevo account.
/// </para>
/// <para>
/// Configuration:
/// <list type="bullet">
///   <item><c>Email:Provider = "Brevo"</c></item>
///   <item><c>Email:Password = &lt;v3 API key from app.brevo.com/settings/keys/api&gt;</c>
///         — note this is the long <c>xkeysib-...</c> key, NOT the SMTP password.</item>
///   <item><c>Email:FromAddress</c> / <c>Email:FromName</c> — must be a verified
///         sender in Brevo (Senders &amp; IP → Senders).</item>
/// </list>
/// </para>
/// </summary>
public class BrevoEmailSender : IEmailSender
{
    private const string Endpoint = "https://api.brevo.com/v3/smtp/email";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<EmailOptions> _options;
    private readonly ILogger<BrevoEmailSender> _log;

    public BrevoEmailSender(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<EmailOptions> options,
        ILogger<BrevoEmailSender> log)
    {
        _httpFactory = httpFactory;
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
            _log.LogDebug("Brevo send skipped: no recipients.");
            return;
        }

        var opts = _options.CurrentValue;

        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.FromAddress))
        {
            _log.LogInformation(
                "[Email DISABLED] subject='{Subject}' to=[{To}]\n{Body}",
                subject, string.Join(", ", recipients), body);
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Password))
        {
            _log.LogWarning(
                "Brevo provider selected but Email:Password (API key) is blank "
                + "— check Render env var Email__Password. Message NOT sent. "
                + "subject='{Subject}'", subject);
            return;
        }

        // Brevo's JSON shape — kept anonymous because each call builds it
        // fresh and the schema is small.
        var payload = new
        {
            sender = new { email = opts.FromAddress, name = opts.FromName },
            to = recipients.Select(r => new { email = r }).ToArray(),
            subject,
            textContent = body,
        };

        var http = _httpFactory.CreateClient("brevo");
        http.Timeout = TimeSpan.FromSeconds(30);

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Brevo uses a custom header for the API key (NOT "Authorization").
        req.Headers.TryAddWithoutValidation("api-key", opts.Password);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Brevo HTTP call failed before getting a response. subject='{Subject}'",
                subject);
            throw;
        }

        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Bubble the Brevo error text up so /Diagnostics/SendTestEmail
            // can show it in the browser.
            throw new HttpRequestException(
                $"Brevo API returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {respBody}");
        }

        _log.LogInformation(
            "Sent email '{Subject}' to {Count} recipient(s) via Brevo HTTP API. response={Resp}",
            subject, recipients.Count, respBody);
    }
}
