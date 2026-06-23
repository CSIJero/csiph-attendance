using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Sends mail by automating the locally-installed Outlook desktop client.
/// This bypasses SMTP entirely so it works on Microsoft 365 tenants that
/// have disabled SMTP AUTH (the default since 2020), as long as Outlook is
/// installed and signed in on the same Windows machine as the app.
/// </summary>
/// <remarks>
/// We use late-bound COM (Type.GetTypeFromProgID + dynamic) so the project
/// doesn't have to take a hard reference to the Outlook PIA. The very first
/// send may show Outlook's "A program is trying to send mail on your behalf"
/// security prompt — clicking Allow once is enough.
/// </remarks>
[SupportedOSPlatform("windows")]
public class OutlookInteropEmailSender : IEmailSender
{
    private readonly ILogger<OutlookInteropEmailSender> _log;
    private readonly IOptionsMonitor<EmailOptions> _options;

    // 0 = olMailItem in Outlook's OlItemType enum.
    private const int OlMailItem = 0;

    public OutlookInteropEmailSender(
        IOptionsMonitor<EmailOptions> options,
        ILogger<OutlookInteropEmailSender> log)
    {
        _options = options;
        _log = log;
    }

    public Task SendAsync(
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
            _log.LogDebug("Outlook send skipped: no recipients.");
            return Task.CompletedTask;
        }

        if (!_options.CurrentValue.Enabled)
        {
            _log.LogInformation(
                "[Email DISABLED] subject='{Subject}' to=[{To}]\n{Body}",
                subject, string.Join(", ", recipients), body);
            return Task.CompletedTask;
        }

        // Outlook's COM API is STA-only; spin up a dedicated single-thread
        // apartment for each send. Sends are infrequent (one alert per shift
        // per level) so the overhead is fine.
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                SendCore(recipients, subject, body);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "OutlookInteropSender",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private void SendCore(List<string> recipients, string subject, string body)
    {
        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException(
                "Outlook is not installed on this machine (ProgID 'Outlook.Application' not found).");
        }

        dynamic? outlook = null;
        dynamic? mail = null;

        try
        {
            outlook = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    "Failed to start Outlook COM application.");

            mail = outlook.CreateItem(OlMailItem);
            mail.Subject = subject;
            mail.Body = body;
            // Outlook accepts ';' as a separator for multiple recipients.
            mail.To = string.Join(";", recipients);

            // Send() drops the message into the Outlook Outbox; Outlook then
            // delivers it through whatever account the user is signed in to.
            mail.Send();

            _log.LogInformation(
                "Sent Outlook email '{Subject}' to {Count} recipient(s)",
                subject, recipients.Count);
        }
        finally
        {
            if (mail is not null) Marshal.FinalReleaseComObject(mail);
            if (outlook is not null) Marshal.FinalReleaseComObject(outlook);
        }
    }
}
