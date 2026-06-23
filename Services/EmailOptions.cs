namespace AttendanceMonitoring.Services;

/// <summary>SMTP transport configuration for outbound email.</summary>
public class EmailOptions
{
    /// <summary>Email priority: 1 (High), 3 (Normal), 5 (Low). Default: 3.</summary>
    public int Priority { get; set; } = 3;
    /// <summary>
    /// Selects which <see cref="IEmailSender"/> implementation to use.
    /// "Smtp" (default) uses <see cref="SmtpEmailSender"/>; "Outlook" uses
    /// <c>OutlookInteropEmailSender</c> which automates the locally-installed
    /// Outlook desktop client (Windows + Outlook required).
    /// </summary>
    public string Provider { get; set; } = "Smtp";

    /// <summary>If false, emails are logged instead of being sent.</summary>
    public bool Enabled { get; set; } = false;

    public string Host { get; set; } = "smtp.office365.com";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Attendance Monitoring";
}

/// <summary>Settings for the offline-during-shift notifier.</summary>
public class NotifierOptions
{
    /// <summary>
    /// How long an employee must be offline before the first warning email.
    /// Warnings then re-fire every this-many minutes for as long as the
    /// user remains offline (default 30).
    /// </summary>
    public int OfflineThresholdMinutes { get; set; } = 30;

    /// <summary>
    /// How long an employee must be offline before the one-shot escalation
    /// email (salary-deduction notice) is sent. Set to 0 (the default) to
    /// disable the cutoff so the recurring warning keeps arriving every
    /// <see cref="OfflineThresholdMinutes"/> instead.
    /// </summary>
    public int EscalationThresholdMinutes { get; set; } = 0;

    /// <summary>How often the notifier scans for offline employees.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>Send to all admins / PMs (anyone with admin or pm role).</summary>
    public bool NotifyAdmins { get; set; } = true;

    /// <summary>Cc the affected employee.</summary>
    public bool NotifyEmployee { get; set; } = true;

    /// <summary>Optional extra recipients (e.g. shared inbox).</summary>
    public List<string> ExtraRecipients { get; set; } = new();
}
