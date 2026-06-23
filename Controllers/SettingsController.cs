using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin settings page. Most sections are read-only snapshots from
/// appsettings/environment, while selected operational policies are
/// editable at runtime and persisted in the database.
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("settings")]
public class SettingsController : AppController
{
    private readonly IConfiguration _config;

    public SettingsController(AppDbContext db, IConfiguration config) : base(db)
    {
        _config = config;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        var vm = new SettingsIndexViewModel
        {
            Sections = new()
            {
                ("Attendance", new()
                {
                    ("Online threshold",
                        FormatSeconds(_config["AttendanceMonitoring:OnlineThresholdSeconds"]),
                        "How long a user's last heartbeat may be stale before the dashboard considers them offline."),
                    ("Session lifetime",
                        FormatHours(_config["AttendanceMonitoring:SessionLifetimeHours"]),
                        "How long an authenticated browser session stays valid before requiring re-login."),
                    ("Require secure cookie",
                        _config["AttendanceMonitoring:RequireSecureCookie"] ?? "(unset)",
                        "When true, the auth cookie is only sent over HTTPS."),
                }),

                ("Idle / offline alerts (Step 5 rules engine)", new()
                {
                    ("Warning threshold",
                        FormatMinutes(_config["Notifier:OfflineThresholdMinutes"]),
                        "First offline warning email fires after the user has been offline this long, then re-fires every threshold."),
                    ("Deduction (escalation) threshold",
                        FormatMinutesOrDisabled(_config["Notifier:EscalationThresholdMinutes"]),
                        "One-shot escalation email after this many minutes offline. Set to 0 to disable the escalation and let warnings keep arriving."),
                    ("Check interval",
                        FormatSeconds(_config["Notifier:CheckIntervalSeconds"]),
                        "How often the background monitor scans open shifts for offline users."),
                    ("Notify admins",
                        _config["Notifier:NotifyAdmins"] ?? "(unset)",
                        "When true, every admin / PM with an email address is CC'd on offline alerts."),
                    ("Notify employee",
                        _config["Notifier:NotifyEmployee"] ?? "(unset)",
                        "When true, the offending employee is emailed directly."),
                    ("Extra recipients",
                        FormatList(_config.GetSection("Notifier:ExtraRecipients").Get<string[]>()),
                        "Additional always-CC'd email addresses (HR, payroll, etc.)."),
                }),

                ("Email delivery", new()
                {
                    ("Provider",
                        _config["Email:Provider"] ?? "(unset)",
                        "Which backend sends emails: Brevo (HTTP API), SMTP, or Outlook (interop)."),
                    ("Enabled",
                        _config["Email:Enabled"] ?? "(unset)",
                        "Master switch. When false, alerts are logged but no email is dispatched."),
                    ("From name",
                        _config["Email:FromName"] ?? "(unset)",
                        "Display name on outgoing alerts."),
                    ("From address",
                        Mask(_config["Email:FromAddress"]),
                        "Mailbox the alerts are sent from. Masked for privacy."),
                    ("SMTP host",
                        _config["Email:Host"] ?? "(unset)",
                        "Only used when Provider=Smtp."),
                    ("SMTP port",
                        _config["Email:Port"] ?? "(unset)",
                        "Only used when Provider=Smtp."),
                }),

                ("Logging", new()
                {
                    ("Default level",
                        _config["Logging:LogLevel:Default"] ?? "(unset)",
                        "Floor severity captured by the application log."),
                    ("ASP.NET Core level",
                        _config["Logging:LogLevel:Microsoft.AspNetCore"] ?? "(unset)",
                        "Severity filter for framework log messages."),
                }),
            },
            GraceOnsitePhMinutes = RuntimeConfig.GraceOnsitePh,
            GraceOffsitePhMinutes = RuntimeConfig.GraceOffsitePh,
            GraceIndiaMinutes = RuntimeConfig.GraceIndia,
            BusinessUnitsText = string.Join(Environment.NewLine, RuntimeConfig.GetBusinessUnits()),
        };

        return View("Index", vm);
    }

    [HttpPost("runtime")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRuntime(
        int graceOnsitePhMinutes,
        int graceOffsitePhMinutes,
        int graceIndiaMinutes,
        string? businessUnitsText)
    {
        var errors = new List<string>();

        graceOnsitePhMinutes = Math.Clamp(graceOnsitePhMinutes, 0, 240);
        graceOffsitePhMinutes = Math.Clamp(graceOffsitePhMinutes, 0, 240);
        graceIndiaMinutes = Math.Clamp(graceIndiaMinutes, 0, 240);

        var units = (businessUnitsText ?? string.Empty)
            .Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => Constants.NormaliseBusinessUnit(u) ?? u)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (units.Count == 0)
        {
            errors.Add("Add at least one Business Unit.");
        }

        if (units.Any(u => u.Length > 120))
        {
            errors.Add("Business Unit names must be 120 characters or fewer.");
        }

        if (errors.Count > 0)
        {
            TempData.Flash(string.Join(" ", errors), "danger");
            return RedirectToAction(nameof(Index));
        }

        await RuntimeConfig.SaveAsync(Db, new Dictionary<string, string>
        {
            [RuntimeConfig.GraceOnsitePhKey] = graceOnsitePhMinutes.ToString(),
            [RuntimeConfig.GraceOffsitePhKey] = graceOffsitePhMinutes.ToString(),
            [RuntimeConfig.GraceIndiaKey] = graceIndiaMinutes.ToString(),
            [RuntimeConfig.BusinessUnitsKey] = string.Join(';', units),
        });

        await RuntimeConfig.LoadFromDbAsync(Db, _config);
        TempData.Flash("Runtime policy saved.", "success");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Helpers \u2014 pretty-print raw config values without changing them.
    // ------------------------------------------------------------------

    private static string FormatSeconds(string? raw)
    {
        if (!int.TryParse(raw, out var s)) return raw ?? "(unset)";
        if (s >= 60 && s % 60 == 0)
        {
            return $"{s} s ({s / 60} min)";
        }
        return $"{s} s";
    }

    private static string FormatMinutes(string? raw)
    {
        if (!int.TryParse(raw, out var m)) return raw ?? "(unset)";
        if (m >= 60 && m % 60 == 0) return $"{m} min ({m / 60} h)";
        return $"{m} min";
    }

    private static string FormatMinutesOrDisabled(string? raw)
    {
        if (int.TryParse(raw, out var m) && m <= 0) return "0 (disabled)";
        return FormatMinutes(raw);
    }

    private static string FormatHours(string? raw)
    {
        if (!int.TryParse(raw, out var h)) return raw ?? "(unset)";
        return h == 1 ? "1 hour" : $"{h} hours";
    }

    private static string FormatList(string[]? list)
    {
        if (list is null || list.Length == 0) return "(none)";
        return string.Join(", ", list);
    }

    /// <summary>Mask the local-part of an email so the page is safe to screenshot.</summary>
    private static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(unset)";
        var at = value.IndexOf('@');
        if (at <= 0) return value;
        var local = value[..at];
        var domain = value[at..];
        if (local.Length <= 2) return new string('\u2022', local.Length) + domain;
        return local[0] + new string('\u2022', local.Length - 2) + local[^1] + domain;
    }
}
