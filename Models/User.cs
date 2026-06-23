using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

public class User
{
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? EmployeeId { get; set; }

    [MaxLength(120)]
    public string? BusinessUnit { get; set; }

    /// <summary>
    /// Semicolon-separated list of Business Units a Program Manager is
    /// responsible for (e.g. <c>"BU2 (PH);BU2 (IN)"</c>). Only meaningful
    /// when <see cref="Role"/> = <c>program_manager</c>; ignored for every
    /// other role. See <see cref="BusinessUnitList"/> for the parsed view.
    /// </summary>
    [MaxLength(500)]
    public string? BusinessUnits { get; set; }

    /// <summary>
    /// Parsed view of <see cref="BusinessUnits"/>. Returns an empty list
    /// when the column is null/empty. Not mapped — splits on read only.
    /// </summary>
    [NotMapped]
    public List<string> BusinessUnitList => string.IsNullOrWhiteSpace(BusinessUnits)
        ? new List<string>()
        : BusinessUnits
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    [Required, MaxLength(255)]
    public string PasswordHash { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string Role { get; set; } = "employee"; // admin | pm | employee

    /// <summary>
    /// True once an admin (or PM) has approved this account. Newly
    /// registered users are created with <c>Approved = false</c> and
    /// cannot sign in until somebody on the admin side flips this. The
    /// seeded built-in admin is approved on creation so the app is
    /// never locked out of itself.
    /// </summary>
    public bool Approved { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Tri-state presence reported by the browser:
    ///   "online"  – user is active (or just minimized; minimize is NOT away).
    ///   "away"    – system has been idle for &gt; 15 min (Idle Detection API).
    ///   "lunch"   – user manually started a lunch break. Offline-alert
    ///                emails are suppressed for the duration. Auto-expires
    ///                after <see cref="Constants.LunchBreakMinutes"/>.
    ///   "offline" – Windows is locked (Idle Detection API screenState=locked)
    ///               or the page is closing. Default = offline so a freshly
    ///               provisioned account doesn't appear online.
    /// Effective state on the dashboard also factors in LastSeen age: if the
    /// last heartbeat is older than the OnlineThreshold, the user is treated
    /// as offline regardless of what this column says.
    /// </summary>
    [MaxLength(16)]
    public string PresenceState { get; set; } = "offline";

    /// <summary>
    /// When the user's <see cref="PresenceState"/> last transitioned to
    /// <c>"offline"</c> (UTC). Used by the offline-notifier to count
    /// continuous offline duration based on workstation-lock state rather
    /// than heartbeat staleness. Cleared (set to null) whenever the user
    /// reports any non-offline state (online / lunch).
    /// </summary>
    public DateTime? OfflineSince { get; set; }

    /// <summary>
    /// When the current lunch break started (UTC). Null when not on lunch.
    /// Used by <see cref="OfflineNotifierService"/> to skip alerts and by
    /// the dashboard to render a countdown.
    /// </summary>
    public DateTime? LunchStartedAt { get; set; }

    /// <summary>
    /// When the current 15-minute short break started (UTC). Null when not
    /// on a break. Auto-expires after <see cref="Constants.ShortBreakMinutes"/>.
    /// </summary>
    public DateTime? BreakStartedAt { get; set; }

    /// <summary>
    /// Count of short breaks already consumed during <see cref="BreaksUsedDate"/>.
    /// Reset to 0 when the PHT calendar day rolls over. Capped at
    /// <see cref="Constants.MaxShortBreaksPerDay"/>.
    /// </summary>
    public int BreaksUsedToday { get; set; } = 0;

    /// <summary>
    /// PHT calendar date that <see cref="BreaksUsedToday"/> refers to.
    /// When a break is started on a later date, the counter is reset first.
    /// </summary>
    public DateOnly? BreaksUsedDate { get; set; }

    /// <summary>
    /// Count of lunches already consumed during <see cref="LunchUsedDate"/>.
    /// Reset to 0 when the PHT calendar day rolls over. Capped at
    /// <see cref="Constants.MaxLunchesPerDay"/> (currently 1), so the
    /// Start lunch button is disabled once it's been used today.
    /// </summary>
    public int LunchesUsedToday { get; set; } = 0;

    /// <summary>
    /// PHT calendar date that <see cref="LunchesUsedToday"/> refers to.
    /// When a lunch is started on a later date, the counter is reset first.
    /// </summary>
    public DateOnly? LunchesUsedDate { get; set; }

    // ----- Login telemetry -------------------------------------------------
    // Captured on successful sign-in so admins can see which workstation a
    // user is signing in from. Hostname is best-effort (reverse DNS); on
    // public networks it's often null and only the IP is recorded.
    [MaxLength(64)]
    public string? LastLoginIp { get; set; }

    [MaxLength(128)]
    public string? LastLoginHost { get; set; }

    /// <summary>
    /// Best-effort geo-resolved location for <see cref="LastLoginIp"/>,
    /// captured at sign-in via <c>ClientInfo.ResolveLocationAsync</c>.
    /// Typical format is <c>"City, Country"</c> (e.g. <c>"Quezon City, Philippines"</c>).
    /// Null when the IP is private/loopback or the upstream geo service
    /// failed; the dashboard falls back to showing the raw IP in that case.
    /// </summary>
    [MaxLength(128)]
    public string? LastLoginLocation { get; set; }

    /// <summary>
    /// Origin of <see cref="LastLoginLocation"/>: <c>"gps"</c> when the
    /// browser supplied <c>navigator.geolocation</c> coordinates and the
    /// server reverse-geocoded them, or <c>"ip"</c> when the value came
    /// from the IP-based fallback (Resolve­LocationAsync). Null on legacy
    /// rows. The admin Team-status table renders a small indicator so
    /// viewers can tell precise vs. approximate locations apart.
    /// </summary>
    [MaxLength(8)]
    public string? LastLoginLocationSource { get; set; }

    /// <summary>Browser-reported latitude (WGS-84). Null when GPS unavailable.</summary>
    public double? LastLoginLatitude { get; set; }

    /// <summary>Browser-reported longitude (WGS-84). Null when GPS unavailable.</summary>
    public double? LastLoginLongitude { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Compact browser/OS label derived from the User-Agent header at
    /// sign-in (e.g. <c>"Chrome on Windows 10"</c>). Lets admins tell
    /// apart two users who share a public IP because they're behind the
    /// same office wifi gateway. Best-effort: null when the User-Agent
    /// is missing or unparseable. The server cannot read the local
    /// Windows username / machine name from a browser request \u2014
    /// surface that limitation to admins instead of pretending otherwise.
    /// </summary>
    [MaxLength(256)]
    public string? LastLoginAgent { get; set; }

    // ----- Forgot-password reset ------------------------------------------
    // Stores a SHA-256 hash of a single-use reset token. The raw token only
    // ever lives in the user's email. <see cref="PasswordResetExpiresAt"/>
    // is set ~1 hour ahead of issue time; verification rejects expired or
    // missing hashes. Successfully consuming the link clears both columns.
    [MaxLength(128)]
    public string? PasswordResetTokenHash { get; set; }

    public DateTime? PasswordResetExpiresAt { get; set; }

    /// <summary>
    /// True when an admin has issued a temporary password via the Users
    /// page. The user is redirected to <c>/Account/ChangePassword</c> on
    /// their next sign-in and can't reach any other page until they pick a
    /// new password (which clears this flag).
    /// </summary>
    public bool MustChangePassword { get; set; } = false;

    /// <summary>
    /// True when the user is on the 24/7 Support rotation — they're
    /// considered always on shift, so the weekly schedule editor is hidden
    /// and they're never flagged as "scheduled but absent". Off by default;
    /// flip from the Edit user form.
    /// </summary>
    public bool IsSupport { get; set; } = false;

    /// <summary>
    /// When true, reminder emails addressed directly to this employee are
    /// skipped (shift/missed-check-in/offline/clock-out reminders). Admin/
    /// PM recipients still receive alerts; this only mutes the employee copy.
    /// </summary>
    public bool OptOutReminderEmails { get; set; } = false;

    /// <summary>
    /// Optional FK into <see cref="RoleDefinition"/> — an admin-managed
    /// label (e.g. "Senior Engineer", "QA Lead") shown alongside the
    /// canonical <see cref="Role"/>. Authorization decisions still flow
    /// through <see cref="Role"/>; this column is display-only.
    /// </summary>
    public int? RoleLabelId { get; set; }

    [ForeignKey(nameof(RoleLabelId))]
    public RoleDefinition? RoleLabel { get; set; }

    /// <summary>
    /// Optional FK to another user (typically a Project Manager) that this
    /// employee reports to. Self-referencing; set to null on the manager's
    /// deletion so we don't orphan the row. Used by the registration flow
    /// so a new hire can be assigned to a PM up-front, and by the Users
    /// list so admins can see direct-reports at a glance.
    /// </summary>
    public int? ManagerId { get; set; }

    [ForeignKey(nameof(ManagerId))]
    public User? Manager { get; set; }

    /// <summary>
    /// Enrolled face fingerprint (16-character hex = 64-bit perceptual
    /// hash) computed from a reference selfie. On check-in the captured
    /// selfie's hash is compared via Hamming distance; results above
    /// <see cref="Constants.FaceMatchMaxDistance"/> bits are flagged for
    /// admin review. Null = user has not enrolled a reference selfie
    /// yet; check-in falls back to the existing heuristic face check.
    /// </summary>
    [MaxLength(32)]
    public string? FaceHash { get; set; }

    /// <summary>UTC timestamp the reference selfie was enrolled.</summary>
    public DateTime? FaceEnrolledAt { get; set; }

    public List<Attendance> Attendances { get; set; } = new();
    public List<ScheduleEntry> ScheduleEntries { get; set; } = new();

    /// <summary>True for both "admin" and "pm" (Project Manager).</summary>
    [NotMapped]
    public bool IsAdmin => Roles.IsAdminRole(Role);

    /// <summary>
    /// True when the user is considered online for dashboard display.
    /// Policy: a logged-in user is Online unless their stored
    /// <see cref="PresenceState"/> is <c>"offline"</c> (which the client
    /// only sets when Windows locks, on logout, or on tab close), or
    /// <see cref="LastSeen"/> is null (never seen). The
    /// <paramref name="thresholdSeconds"/> parameter is accepted for
    /// backwards compatibility but no longer participates in the
    /// decision — stale heartbeats do not mark the user offline.
    /// </summary>
    public bool IsOnline(int thresholdSeconds = 60)
    {
        if (LastSeen is null) return false;
        return !string.Equals(PresenceState, "offline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes the effective presence state for dashboard rendering.
    /// Returns "online", "lunch", or "offline".
    /// <para>
    /// Policy: a user shows as <c>"offline"</c> only when the client has
    /// reported a locked workstation (or browser-close / logout), or
    /// <see cref="LastSeen"/> is null. A stale heartbeat alone is NOT
    /// enough to display the user as offline — the dashboard trusts the
    /// stored <see cref="PresenceState"/>. Lunch auto-expires
    /// <see cref="Constants.LunchBreakMinutes"/> minutes after
    /// <see cref="LunchStartedAt"/>.
    /// </para>
    /// <para>
    /// Note: the offline-alert email pipeline still uses
    /// <see cref="LastSeen"/> staleness directly so a missing heartbeat
    /// during shift hours still escalates to notifications — only the
    /// on-screen badge is affected by this method.
    /// </para>
    /// </summary>
    public string EffectiveState(int thresholdSeconds = 60)
    {
        if (LastSeen is null) return "offline";
        return PresenceState switch
        {
            "lunch"   => IsLunchActive() ? "lunch" : "online",
            "break"   => IsBreakActive() ? "break" : "online",
            "offline" => "offline",
            _         => "online", // "online", legacy "away", or anything else
        };
    }

    /// <summary>True when lunch was started within the past
    /// <see cref="Constants.LunchBreakMinutes"/> minutes.</summary>
    public bool IsLunchActive()
    {
        if (LunchStartedAt is null) return false;
        var start = DateTime.SpecifyKind(LunchStartedAt.Value, DateTimeKind.Utc);
        return (DateTime.UtcNow - start).TotalMinutes < Constants.LunchBreakMinutes;
    }

    /// <summary>True when a short break was started within the past
    /// <see cref="Constants.ShortBreakMinutes"/> minutes.</summary>
    public bool IsBreakActive()
    {
        if (BreakStartedAt is null) return false;
        var start = DateTime.SpecifyKind(BreakStartedAt.Value, DateTimeKind.Utc);
        return (DateTime.UtcNow - start).TotalMinutes < Constants.ShortBreakMinutes;
    }
}
