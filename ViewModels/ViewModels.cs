using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;

namespace AttendanceMonitoring.ViewModels;

public class AdminDashboardViewModel
{
    public List<TeamRowViewModel> Rows { get; set; } = new();
    public int OnlineCount { get; set; }
    public int AwayCount { get; set; }
    public int LunchCount { get; set; }
    public int OfflineCount { get; set; }
    public int PresentCount { get; set; }
    public int TotalUsers { get; set; }
    public Attendance? MyAttendance { get; set; }
    /// <summary>Late-arrival evaluation for the signed-in PM/PgM's own check-in;
    /// null when they don't have an open record or aren't in the personal section.</summary>
    public LateCheck.Result? MyLate { get; set; }
    public int OnlineThresholdSeconds { get; set; }

    // Personal (employee-style) section — populated for Project Managers
    // AND Program Managers so they see both the team table and their own
    // check-in / schedule / Break / Start-lunch controls. Field name kept
    // as IsPm for backwards compatibility; semantics now = "show personal
    // section" (true for pm and program_manager, false for pure admin).
    public bool IsPm { get; set; }
    public string FirstName { get; set; } = string.Empty;
    /// <summary>This-week schedule rows (Mon..Sun) keyed by calendar date.
    /// Dates with no row are implicitly "off".</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();
    /// <summary>The current PHT week's anchor (Monday).</summary>
    public DateOnly WeekStart { get; set; }
    /// <summary>Schedule row whose <c>WorkDate</c> matches today's PHT date, or null.</summary>
    public ScheduleEntry? TodaySchedule { get; set; }
    public int TodayIndex { get; set; }
    public int WorkingDays { get; set; }
    public double WeeklyHours { get; set; }
    public List<Attendance> Recent { get; set; } = new();
    public int RecentMinutes { get; set; }

    /// <summary>Holiday rows for the current week keyed by date.</summary>
    public Dictionary<DateOnly, Holiday> WeekHolidays { get; set; } = new();
}

public class WeeklyAttendanceViewModel
{
    public DateOnly WeekStart { get; set; }
    /// <summary>1 (single week) or 2 (two-week span). Drives column count.</summary>
    public int WeeksSpan { get; set; } = 1;
    public DateOnly CurrentWeekStart { get; set; }
    public DateOnly PrevWeekStart { get; set; }
    public DateOnly NextWeekStart { get; set; }
    public bool IsCurrentWeek { get; set; }
    public List<WeeklyAttendanceRow> Rows { get; set; } = new();
    public List<User> AllUsers { get; set; } = new();
    public int? FilterUserId { get; set; }
    public bool PresentOnly { get; set; }
    public int TeamPresentDays { get; set; }
    public int TeamTotalMinutes { get; set; }
}

public class WeeklyAttendanceRow
{
    public User User { get; set; } = null!;
    /// <summary>Day index 0..(WeeksSpan*7-1); null when there's no record that day.</summary>
    public Attendance?[] Days { get; set; } = new Attendance?[7];
    public int PresentDays { get; set; }
    public int TotalMinutes { get; set; }
}

public class TeamRowViewModel
{
    public User User { get; set; } = null!;
    public bool Online { get; set; }
    /// <summary>"online" | "away" | "offline" – derived presence state.</summary>
    public string State { get; set; } = "offline";
    /// <summary>Reason corresponding to the derived presence state.</summary>
    public string PresenceReason { get; set; } = "never_seen";
    public Attendance? Attendance { get; set; }
    /// <summary>Today's schedule row (PHT calendar date); null when no row exists.</summary>
    public ScheduleEntry? TodaySchedule { get; set; }
    /// <summary>Cached late-arrival evaluation for today's check-in; null when no check-in.</summary>
    public LateCheck.Result? Late { get; set; }
    /// <summary>Cumulative explicit offline time for the user's local day.</summary>
    public int OfflineSecondsToday { get; set; }
}

public class EmployeeDashboardViewModel
{
    /// <summary>Signed-in user; used for local-time/timezone label rendering.</summary>
    public User? Me { get; set; }
    /// <summary>This-week schedule rows ordered Mon..Sun.</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();
    /// <summary>This week's anchor (Monday).</summary>
    public DateOnly WeekStart { get; set; }
    /// <summary>Schedule row for today's PHT date; null if the user has no row for today.</summary>
    public ScheduleEntry? TodaySchedule { get; set; }
    public int TodayIndex { get; set; }
    public Attendance? MyAttendance { get; set; }
    /// <summary>Late-arrival evaluation for today's check-in; null when not applicable.</summary>
    public LateCheck.Result? MyLate { get; set; }
    public int WorkingDays { get; set; }
    public double WeeklyHours { get; set; }
    public List<Attendance> Recent { get; set; } = new();
    public int RecentMinutes { get; set; }
    public string FirstName { get; set; } = string.Empty;
    /// <summary>Display-ready list of the signed-in user's Business Units,
    /// derived from <c>User.BusinessUnitList</c>. Empty when unset.</summary>
    public List<string> BusinessUnits { get; set; } = new();
    /// <summary>When set, today's session is already closed but the
    /// user's next scheduled shift starts within the pre-shift grace
    /// window (e.g. clocking in at 23:55 PHT for a 00:00 shift). The
    /// dashboard surfaces an additional Check-in button so the
    /// cross-midnight back-to-back case works. Local time of the
    /// upcoming start; null otherwise.</summary>
    public DateTime? NextShiftEarlyStart { get; set; }

    /// <summary>Holiday rows for the current week keyed by date.</summary>
    public Dictionary<DateOnly, Holiday> WeekHolidays { get; set; } = new();
}

public class ScheduleViewModel
{
    public User Target { get; set; } = null!;
    /// <summary>All schedule rows for the active month (existing rows
    /// only; missing dates are implicitly "off").</summary>
    public List<ScheduleEntry> Entries { get; set; } = new();
    public bool Editable { get; set; }
    public List<User> AllUsers { get; set; } = new();
    public double WeeklyHours { get; set; }
    public bool ViewerIsAdmin { get; set; }
    /// <summary>True when the target user has the <c>admin</c> role.
    /// Admins don't have a working schedule, so the view renders a notice
    /// instead of the month editor.</summary>
    public bool IsAdminTarget { get; set; }
    /// <summary>True when the target user has the Support (24/7) flag.
    /// Support users are always-on; the month editor stays usable but
    /// allows wrap-around shift times.</summary>
    public bool IsSupportTarget { get; set; }

    // ---- Month / week navigation ---------------------------------------

    /// <summary>The first day of the displayed month (PHT).</summary>
    public DateOnly MonthStart { get; set; }
    /// <summary>The last day of the displayed month (PHT).</summary>
    public DateOnly MonthEnd { get; set; }
    /// <summary>1..5 to filter the editor to a single week of the month,
    /// or 0 to show every week.</summary>
    public int WeekFilter { get; set; }
    /// <summary>Week buckets: each tuple is (weekNumber, firstDate, lastDate, dates).</summary>
    public List<(int Week, DateOnly Start, DateOnly End, List<DateOnly> Dates)> WeekBuckets { get; set; } = new();
    /// <summary>Existing entries keyed by date for quick view lookup.</summary>
    public Dictionary<DateOnly, ScheduleEntry> EntriesByDate { get; set; } = new();

    /// <summary>
    /// Pending schedule-amendment requests for the target user, keyed by
    /// the calendar date. Used by the view to disable the "Request change"
    /// button on a date that already has an in-flight request.
    /// </summary>
    public Dictionary<DateOnly, ScheduleAmendment> PendingAmendmentsByDate { get; set; } = new();

    /// <summary>True when the current viewer is allowed to file an
    /// amendment request on this row (i.e. an employee/PM viewing their
    /// own non-support, non-admin schedule).</summary>
    public bool CanRequestAmendment { get; set; }
}

public class ScheduleAmendmentsViewModel
{
    public List<ScheduleAmendment> Pending { get; set; } = new();
    public List<ScheduleAmendment> Recent { get; set; } = new();
}

public class AttendanceHistoryViewModel
{
    public User Target { get; set; } = null!;
    public List<Attendance> Records { get; set; } = new();
    public List<User> AllUsers { get; set; } = new();
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public bool ViewerIsAdmin { get; set; }

    /// <summary>
    /// AttendanceId -> latest <see cref="AttendanceEditRequest"/> the
    /// viewer needs to know about (their own pending row, or any pending
    /// row when the viewer is an admin/PM). Used by Index.cshtml to
    /// disable the "Request edit" button while a request is in flight
    /// and to show a "pending review" badge on the row.
    /// </summary>
    public Dictionary<int, AttendanceEditRequest> PendingRequestsByAttendance { get; set; } = new();

    /// <summary>
    /// Target user's schedule rows for the displayed range, keyed by
    /// calendar date. Used to compute the per-row overtime / undertime
    /// variance against the scheduled shift duration.
    /// </summary>
    public Dictionary<DateOnly, ScheduleEntry> ScheduleByDate { get; set; } = new();

    /// <summary>
    /// Approved <see cref="LeaveRequest"/>s that overlap the displayed
    /// range, expanded one entry per calendar date the leave covers.
    /// Used by Index.cshtml so the row can surface a "Approved leave"
    /// remark alongside the regular attendance status.
    /// </summary>
    public Dictionary<DateOnly, LeaveRequest> ApprovedLeavesByDate { get; set; } = new();
}

public class LeaveRequestFormViewModel
{
    public List<User> CandidateUsers { get; set; } = new();
    public int TargetUserId { get; set; }
    public DateTime StartDate { get; set; } = PhTime.Today.ToDateTime(new TimeOnly(9, 0));
    public DateTime EndDate { get; set; } = PhTime.Today.ToDateTime(new TimeOnly(18, 0));
    public string LeaveType { get; set; } = "Full";
    public decimal HoursPerDay { get; set; } = 8m;
    public string Reason { get; set; } = string.Empty;
    public bool ViewerIsAdmin { get; set; }
}

public class LeaveIndexViewModel
{
    public List<LeaveRequest> Mine { get; set; } = new();
    public List<LeaveRequest> Pending { get; set; } = new();
    public List<LeaveRequest> Recent { get; set; } = new();
    public bool ViewerIsAdmin { get; set; }

    // ---- Shift-amendment ("Request a shift change") add-on -----------
    // Folded into the Leave page so the employee has a single Requests
    // hub: leave requests + shift-change requests in one place.

    /// <summary>
    /// The current user (or selected target). Used by the inline shift-
    /// change request form so it can post to <c>/schedule/amend</c> with
    /// the right <c>user_id</c>.
    /// </summary>
    public User? Me { get; set; }

    /// <summary>
    /// The current user's pending schedule amendments, keyed by the
    /// target calendar date.
    /// </summary>
    public Dictionary<DateOnly, ScheduleAmendment> MyPendingAmendmentsByDate { get; set; } = new();

    /// <summary>
    /// The current user's most recently decided / cancelled schedule
    /// amendments (newest first). Lets the employee track the outcome
    /// of a shift-change request without bothering an admin.
    /// </summary>
    public List<ScheduleAmendment> MyRecentAmendments { get; set; } = new();

    /// <summary>
    /// True when the current user is allowed to submit a shift-change
    /// request (regular employee or PM acting on their own schedule, not
    /// an admin and not a Support user with direct edit rights).
    /// </summary>
    public bool CanRequestShiftChange { get; set; }
}

public class AttendanceEditRequestFormViewModel
{
    public Attendance Record { get; set; } = null!;
    public AttendanceEditRequest? Existing { get; set; }
    public string? ReturnUrl { get; set; }
}

public class EditRequestsIndexViewModel
{
    public List<AttendanceEditRequest> Pending { get; set; } = new();
    public List<AttendanceEditRequest> Recent { get; set; } = new();
}

public class AddTimeEntryViewModel
{
    /// <summary>True when the viewer can file on behalf of another user
    /// (admin / PM / PgM). Drives the candidate dropdown in the view.</summary>
    public bool ViewerIsAdmin { get; set; }
    public List<User> CandidateUsers { get; set; } = new();
    /// <summary>Scheduled working dates (within a recent window) for which
    /// the viewer (default = themselves) has no attendance row yet. The
    /// view uses this to populate the work-date dropdown so a user can
    /// only file an Add Time Entry for an actual missing scheduled day.
    /// Admins filing on behalf of someone else will see this list
    /// refreshed asynchronously per pick — initial render is for the
    /// signed-in viewer's own gaps.</summary>
    public List<DateOnly> EligibleDates { get; set; } = new();
}

public class QuotaResetIndexViewModel
{
    public List<QuotaResetRequest> Pending { get; set; } = new();
    public List<QuotaResetRequest> Recent { get; set; } = new();
}

public class ViolationsIndexViewModel
{
    public List<NotificationLog> Rows { get; set; } = new();
    public bool IsReadOnly { get; set; }
    /// <summary>"pending" (default), "investigate", "needaction", "closed", or "all".</summary>
    public string Filter { get; set; } = "pending";
    public int PendingCount { get; set; }
    public int InvestigateCount { get; set; }
    public int NeedActionCount { get; set; }
    public int ClosedCount { get; set; }
}

public class ViolationsInboxViewModel
{
    public List<NotificationLog> Rows { get; set; } = new();
}

public class SettingsIndexViewModel
{
    /// <summary>Section title \u2192 list of (key, value, note) rows.</summary>
    public List<(string Section, List<(string Key, string Value, string? Note)> Items)> Sections { get; set; } = new();
    public int GraceOnsitePhMinutes { get; set; } = 30;
    public int GraceOffsitePhMinutes { get; set; } = 0;
    public int GraceIndiaMinutes { get; set; } = 60;
    public string BusinessUnitsText { get; set; } = string.Empty;
}

public class LoginViewModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ForgotPasswordViewModel
{
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordViewModel
{
    public string Token { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class ChangePasswordViewModel
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class NewUserViewModel
{
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? EmployeeId { get; set; }
    public string? BusinessUnit { get; set; }
    /// <summary>Semicolon-separated BU list when Role = program_manager.</summary>
    public string? BusinessUnits { get; set; }
    public string Role { get; set; } = "employee";
    /// <summary>Optional admin-managed role label (FK into <c>role_definitions</c>).</summary>
    public int? RoleLabelId { get; set; }
    /// <summary>
    /// FK to the user this account reports to. For an Employee this is
    /// their Project Manager; for a PM this is their Program Manager.
    /// </summary>
    public int? ManagerId { get; set; }
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Approved Project Managers eligible to be picked as the direct
    /// manager for an Employee being created.
    /// </summary>
    public List<User> ProjectManagerChoices { get; set; } = new();

    /// <summary>
    /// Approved Program Managers / Admins eligible to be picked as the
    /// direct manager for a Project Manager being created.
    /// </summary>
    public List<User> ProgramManagerChoices { get; set; } = new();

    /// <summary>Picklist of admin-managed role labels for the "Role label" dropdown.</summary>
    public List<RoleDefinition> RoleLabelChoices { get; set; } = new();

    // Kept for backwards compatibility with existing callers that still
    // populate this single list. New code uses the role-specific lists
    // above.
    public List<User> ManagerChoices { get; set; } = new();

    /// <summary>Runtime-managed list of selectable Business Units.</summary>
    public List<string> BusinessUnitChoices { get; set; } = new();
}

public class UsersListViewModel
{
    public List<User> Users { get; set; } = new();
    /// <summary>Users with <c>Approved=false</c>, awaiting admin sign-off.</summary>
    public List<User> Pending { get; set; } = new();
    /// <summary>
    /// Approved Project Managers used to assign an Employee to a PM during
    /// the Approve flow.
    /// </summary>
    public List<User> ProjectManagerChoices { get; set; } = new();
    /// <summary>
    /// Approved Program Managers / Admins used to assign a PM to a PgM
    /// during the Approve flow.
    /// </summary>
    public List<User> ProgramManagerChoices { get; set; } = new();
    /// <summary>
    /// Catalogue of role definitions (built-ins + custom) for the Role
    /// dropdown in the Approve modal. Each entry maps a label to a
    /// canonical permission tier via <see cref="RoleDefinition.BaseRole"/>.
    /// </summary>
    public List<RoleDefinition> RoleLabelChoices { get; set; } = new();
    /// <summary>Runtime-managed list of selectable Business Units.</summary>
    public List<string> BusinessUnitChoices { get; set; } = new();
    // Kept for backwards compatibility.
    public List<User> ManagerChoices { get; set; } = new();
}

public class DailyReportRow
{
    public DateOnly Date { get; set; }
    public string Key { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string BusinessUnit { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }   // UTC; format with PhTime for display
    public DateTime? EndTime { get; set; }     // UTC; null when still open
    public string StartTimeDisplay { get; set; } = string.Empty;
    public string EndTimeDisplay { get; set; } = string.Empty;
    public string TimeZoneLabel { get; set; } = string.Empty;
    public double HoursRendered { get; set; }  // 0 when no check-in or open
    public string AttendanceStatus { get; set; } = string.Empty; // Complete | Incomplete | Incomplete Hours | Absent
    /// <summary>Onsite | Offsite | Dayoff — the schedule's working type.</summary>
    public string WorkType { get; set; } = string.Empty;
    /// <summary>
    /// "" when not applicable (Dayoff / Support / absent), "On time", or
    /// "Late by HH:MM". Computed via <see cref="Services.LateCheck"/>.
    /// </summary>
    public string LateStatus { get; set; } = string.Empty;
    /// <summary>Minutes late after applying the user's grace period. 0 when not late.</summary>
    public int LateMinutes { get; set; }
    /// <summary>
    /// Number of offline-during-shift email notifications successfully sent
    /// for this user on this date (from NotificationLog where Status="Sent").
    /// </summary>
    public int EmailNotifications { get; set; }
    /// <summary>Cumulative explicit offline time for this user's local date.</summary>
    public int OfflineSeconds { get; set; }
    /// <summary>Per-send timestamps where offline minutes were 30..60.</summary>
    public string Notification30To60Details { get; set; } = string.Empty;
    /// <summary>Per-send timestamps where offline minutes were above 60.</summary>
    public string NotificationOver60Details { get; set; } = string.Empty;
    /// <summary>Hours actually rendered minus scheduled hours (negative = short).</summary>
    public double TimeDifference { get; set; }
    public double ScheduledHours { get; set; }
    public string Remarks { get; set; } = string.Empty;
}

public class DailyReportViewModel
{
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? BusinessUnitFilter { get; set; }
    public int? UserIdFilter { get; set; }
    public bool IncludeNonWorkingDays { get; set; }
    public List<DailyReportRow> Rows { get; set; } = new();
    public List<User> Users { get; set; } = new();
    public List<string> BusinessUnits { get; set; } = new();
}

public class EditUserViewModel
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? EmployeeId { get; set; }
    public string? BusinessUnit { get; set; }
    /// <summary>Semicolon-separated BU list when Role = program_manager.</summary>
    public string? BusinessUnits { get; set; }
    public string Role { get; set; } = "employee";
    /// <summary>Optional admin-managed role label (FK into <c>role_definitions</c>).</summary>
    public int? RoleLabelId { get; set; }
    /// <summary>
    /// FK to the user this account reports to. For an Employee this is
    /// their Project Manager; for a PM this is their Program Manager.
    /// </summary>
    public int? ManagerId { get; set; }
    /// <summary>
    /// True when the user is on 24/7 Support rotation. Hides the weekly
    /// schedule editor and exempts them from "scheduled but absent" alerts.
    /// </summary>
    public bool IsSupport { get; set; }

    /// <summary>
    /// True when employee-targeted reminder emails should be skipped.
    /// Admin/PM recipients continue to receive alerts.
    /// </summary>
    public bool OptOutReminderEmails { get; set; }

    /// <summary>Approved Project Managers eligible to be picked for an Employee.</summary>
    public List<User> ProjectManagerChoices { get; set; } = new();

    /// <summary>Approved Program Managers / Admins eligible to be picked for a PM.</summary>
    public List<User> ProgramManagerChoices { get; set; } = new();

    /// <summary>Picklist of admin-managed role labels for the "Role label" dropdown.</summary>
    public List<RoleDefinition> RoleLabelChoices { get; set; } = new();

    /// <summary>Runtime-managed list of selectable Business Units.</summary>
    public List<string> BusinessUnitChoices { get; set; } = new();

    // Kept for backwards compatibility with existing callers.
    public List<User> ManagerChoices { get; set; } = new();
}

public class ErrorViewModel
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Backs the read-only "My profile" page (Views/Profile/Index.cshtml).
/// Bundles the user's own record together with the current week's
/// schedule rows and the last few attendance entries so the page can
/// render without extra round-trips.
/// </summary>
public class ProfileViewModel
{
    public User Me { get; set; } = null!;
    /// <summary>This-week schedule rows ordered Mon..Sun.</summary>
    public List<ScheduleEntry> Schedule { get; set; } = new();
    /// <summary>This week's anchor (Monday).</summary>
    public DateOnly WeekStart { get; set; }
    public int TodayIndex { get; set; }
    public int WorkingDays { get; set; }
    public double WeeklyHours { get; set; }
    public List<Attendance> Recent { get; set; } = new();
    public int RecentMinutes { get; set; }

    /// <summary>Holiday rows for the current week keyed by date.</summary>
    public Dictionary<DateOnly, Holiday> WeekHolidays { get; set; } = new();
}

/// <summary>Form view-model for the face enrollment page.</summary>
public class FaceEnrollVm
{
    /// <summary>True once the user has an enrolled hash.</summary>
    public bool IsEnrolled { get; set; }
    public DateTime? EnrolledAt { get; set; }

    /// <summary>
    /// Recent check-in selfies the user can pick from to enroll
    /// retroactively (no need to take a new photo).
    /// </summary>
    public List<FaceEnrollHistoryItem> RecentSelfies { get; set; } = new();
}

/// <summary>One row in the "pick a past selfie" grid.</summary>
public class FaceEnrollHistoryItem
{
    public int AttendanceId { get; set; }
    public DateTime CheckIn { get; set; }
    public DateOnly WorkDate { get; set; }
    public string? FaceMatchStatus { get; set; }
    /// <summary>Data-URL JPEG (already trimmed at capture time).</summary>
    public string Photo { get; set; } = string.Empty;
}
