using System.Globalization;
using System.Text;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

[Authorize]
[Route("attendance")]
public class AttendanceController : AppController
{
    // ------------------------------------------------------------------
    // User: Request missing entry (time-in/time-out)
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // Add Time Entry (formerly "Request missing entry")
    //   - Available Mon–Fri only (server-side reject Sat/Sun).
    //   - Admins / PMs / PgMs may file ON BEHALF of any employee in scope
    //     by supplying a `user_id` form field; regular employees can only
    //     file for themselves.
    //   - Submitting fires an email to all admins + same-BU PMs/PgMs so
    //     the queue isn't reliant on someone refreshing /attendance/edit-requests.
    // ------------------------------------------------------------------
    [HttpGet("request-missing-entry")]
    public async Task<IActionResult> RequestMissingEntry()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        var vm = new AddTimeEntryViewModel
        {
            ViewerIsAdmin = IsAdmin,
            CandidateUsers = IsAdmin
                ? await (await GetVisibleUsersAsync()).OrderBy(u => u.FullName).ToListAsync()
                : new List<User>(),
            EligibleDates = await GetEligibleAddTimeEntryDatesAsync(me),
        };
        return View(vm);
    }

    /// <summary>
    /// JSON endpoint used by the Add Time Entry form when an admin / PM
    /// picks a candidate employee from the dropdown — refreshes the
    /// work-date options to reflect that user's scheduled-but-unrecorded
    /// days. Scoped through <see cref="CanViewUserAsync"/> so a PM can't
    /// peek at someone outside their BU.
    /// </summary>
    [HttpGet("request-missing-entry/eligible-dates")]
    public async Task<IActionResult> EligibleAddTimeEntryDates([FromQuery(Name = "user_id")] int? userId)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        var target = me;
        if (userId is int tid && tid != me.Id)
        {
            if (!IsAdmin) return Forbid();
            if (!await CanViewUserAsync(tid)) return Forbid();
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == tid) ?? me;
        }
        var dates = await GetEligibleAddTimeEntryDatesAsync(target);
        return Json(dates.Select(d => d.ToString("yyyy-MM-dd")).ToList());
    }

    /// <summary>
    /// Returns the recent scheduled working dates for which
    /// <paramref name="user"/> has no attendance row yet. Looks back
    /// 30 days from the user's local "today" to keep the dropdown short
    /// and avoid surfacing far-future schedules.
    /// </summary>
    private async Task<List<DateOnly>> GetEligibleAddTimeEntryDatesAsync(User user)
    {
        var today = UserClock.TodayFor(user);
        var start = today.AddDays(-30);
        var schedRows = await Db.ScheduleEntries
            .Where(s => s.UserId == user.Id
                        && s.WorkDate >= start
                        && s.WorkDate <= today
                        && s.IsWorking)
            .ToListAsync();
        var workingDates = schedRows
            .Where(s => !ScheduleEntry.IsNonWorkingType(s.EffectiveWorkType))
            .Select(s => s.WorkDate)
            .ToHashSet();
        if (workingDates.Count == 0)
        {
            return new List<DateOnly>();
        }
        var existing = await Db.Attendances
            .Where(a => a.UserId == user.Id
                        && a.WorkDate >= start
                        && a.WorkDate <= today)
            .Select(a => a.WorkDate)
            .ToListAsync();
        foreach (var d in existing) workingDates.Remove(d);
        return workingDates.OrderByDescending(d => d).ToList();
    }

    [HttpPost("request-missing-entry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestMissingEntryPost(
        [FromForm] string work_date,
        [FromForm] string check_in,
        [FromForm] string? check_out,
        [FromForm] string reason,
        [FromForm] string? proof_photo,
        [FromForm(Name = "user_id")] int? targetUserId = null)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        // Resolve target user: admins / PMs / PgMs may file for another
        // user (scoped to their visibility); employees can only file for
        // themselves. Silently fall back to self if a non-admin sends a
        // user_id, but if an admin picks an out-of-scope id, hard-fail.
        var target = me;
        if (targetUserId is int tid && tid != me.Id)
        {
            if (!IsAdmin)
            {
                TempData.Flash("You can only file an Add Time Entry request for yourself.", "danger");
                return RedirectToAction(nameof(RequestMissingEntry));
            }
            if (!await CanViewUserAsync(tid))
            {
                TempData.Flash("You don't have access to that user.", "danger");
                return RedirectToAction(nameof(RequestMissingEntry));
            }
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == tid) ?? me;
        }

        if (!DateOnly.TryParse(work_date, out var wd))
        {
            TempData.Flash("Work date is required and must be valid.", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        // Policy: Add Time Entry must align with the target's schedule.
        // The user can only add an entry for a date they were actually
        // scheduled to work (i.e. there is a schedule row marked as
        // working / not Dayoff) AND there is no attendance record yet
        // on file for that date. Existing rows go through the regular
        // edit-request flow.
        var schedForDate = await DbInitializer.GetEffectiveScheduleForDateAsync(Db, target, wd);
        var isScheduledWorkDay = schedForDate is { IsWorking: true }
            && !ScheduleEntry.IsNonWorkingType(schedForDate.EffectiveWorkType);
        if (!isScheduledWorkDay)
        {
            TempData.Flash("That date is not a scheduled working day for this user.", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        var existingAtt = await Db.Attendances
            .FirstOrDefaultAsync(a => a.UserId == target.Id && a.WorkDate == wd);
        if (existingAtt is not null)
        {
            TempData.Flash("An attendance entry already exists for that date. Use the edit-request flow instead.", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        if (!TryParseUserDateTime(target, check_in, out var ciUtc))
        {
            TempData.Flash("Check-in time is required and must be valid.", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        DateTime? coUtc = null;
        if (!string.IsNullOrWhiteSpace(check_out))
        {
            if (!TryParseUserDateTime(target, check_out, out var v))
            {
                TempData.Flash("Check-out time is invalid.", "danger");
                return RedirectToAction(nameof(RequestMissingEntry));
            }
            if (v <= ciUtc)
            {
                TempData.Flash("Check-out must be after check-in.", "danger");
                return RedirectToAction(nameof(RequestMissingEntry));
            }
            coUtc = v;
        }
        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5)
        {
            TempData.Flash("A reason is required (at least 5 characters).", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        var safeProof = SanitizePhoto(proof_photo);
        if (!IsAdmin && safeProof is null)
        {
            TempData.Flash("Image proof is required for time adjustment requests.", "danger");
            return RedirectToAction(nameof(RequestMissingEntry));
        }
        // No existing attendance row for that scheduled date (we already
        // rejected duplicates above); create a placeholder Attendance the
        // edit-request can hang off.
        var att = new Attendance { UserId = target.Id, WorkDate = wd, CheckIn = ciUtc };
        Db.Attendances.Add(att);
        await Db.SaveChangesAsync();
        // Create edit request — RequestedByUserId records the filer (so a
        // PM-on-behalf filing is auditable), and the linked Attendance.UserId
        // identifies the affected employee.
        var req = new AttendanceEditRequest
        {
            AttendanceId = att.Id,
            RequestedByUserId = me.Id,
            RequestedAt = DateTime.UtcNow,
            RequestedWorkDate = wd,
            RequestedCheckIn = ciUtc,
            RequestedCheckOut = coUtc,
            Reason = reason,
            ProofPhoto = safeProof,
            Status = "Pending"
        };
        Db.AttendanceEditRequests.Add(req);
        await Db.SaveChangesAsync();

        // Fire a notification email to admins + same-BU PMs/PgMs so the
        // queue moves without anyone having to refresh the page. Failures
        // are swallowed so a temporary SMTP hiccup doesn't roll back the
        // user-visible submit.
        try
        {
            await SendAddTimeEntryNotificationAsync(target, me, req);
        }
        catch (Exception ex)
        {
            HttpContext.RequestServices
                .GetService(typeof(ILogger<AttendanceController>))
                ?.GetType()
                .GetMethod("LogError", new[] { typeof(Exception), typeof(string), typeof(object[]) })
                ?.Invoke(HttpContext.RequestServices.GetService(typeof(ILogger<AttendanceController>)),
                    new object[] { ex, "Add-time-entry email failed", Array.Empty<object>() });
        }

        var onBehalf = target.Id != me.Id ? $" for {target.FullName}" : string.Empty;
        TempData.Flash($"Add Time Entry request{onBehalf} submitted for review.", "success");
        return RedirectToAction("Index");
    }

    private async Task SendAddTimeEntryNotificationAsync(User target, User filer, AttendanceEditRequest req)
    {
        // Recipients: every admin/PgM, plus any PM whose BU overlaps the
        // target's BU. Empty result -> nothing to send.
        var targetBu = target.BusinessUnit;
        var notifiees = await Db.Users
            .Where(u => u.Email != null && u.Email != ""
                && (u.Role == Roles.Admin
                    || u.Role == Roles.ProgramManager
                    || (u.Role == Roles.Pm
                        && (targetBu != null && u.BusinessUnit == targetBu))))
            .Select(u => u.Email!)
            .ToListAsync();
        if (notifiees.Count == 0) return;

        var ciLocal = UserClock.Format(target, req.RequestedCheckIn, "yyyy-MM-dd HH:mm");
        var coLocal = req.RequestedCheckOut is { } co
            ? UserClock.Format(target, co, "yyyy-MM-dd HH:mm")
            : "(none)";
        var tz = UserClock.Label(target);
        var onBehalf = target.Id != filer.Id
            ? $" filed by {filer.FullName} on behalf of"
            : " filed by";
        var subject = $"[Attendance] Add Time Entry request{onBehalf} {target.FullName} ({req.RequestedWorkDate:yyyy-MM-dd})";
        var body =
            $"A new Add Time Entry request is awaiting review.\r\n\r\n" +
            $"Employee   : {target.FullName} ({target.EmployeeId ?? "-"})\r\n" +
            $"Business Unit : {target.BusinessUnit ?? "-"}\r\n" +
            $"Filed by   : {filer.FullName}\r\n" +
            $"Work date  : {req.RequestedWorkDate:yyyy-MM-dd}\r\n" +
            $"Check-in   : {ciLocal} {tz}\r\n" +
            $"Check-out  : {coLocal} {tz}\r\n" +
            $"Reason     : {req.Reason}\r\n\r\n" +
            "Review and approve / reject from /attendance/edit-requests.\r\n";

        var distinct = notifiees.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _email.SendAsync(distinct, subject, body);
    }

    private async Task SendEditRequestDecisionEmailAsync(
        AttendanceEditRequest req, User decider, bool approved)
    {
        if (req.Attendance is null) return;
        // The affected employee is the owner of the linked Attendance row
        // (NOT necessarily the filer when a PM filed on their behalf).
        var employee = await Db.Users.FirstOrDefaultAsync(u => u.Id == req.Attendance.UserId);
        if (employee?.Email is not { Length: > 0 } addr) return;

        var verb = approved ? "approved" : "rejected";
        var ciLocal = UserClock.Format(employee, req.RequestedCheckIn, "yyyy-MM-dd HH:mm");
        var coLocal = req.RequestedCheckOut is { } co
            ? UserClock.Format(employee, co, "yyyy-MM-dd HH:mm")
            : "(none)";
        var tz = UserClock.Label(employee);
        var noteLine = string.IsNullOrEmpty(req.DecisionNote)
            ? string.Empty
            : $"Reviewer note : {req.DecisionNote}\r\n";
        var subject = $"[Attendance] Your Add Time Entry request for {req.RequestedWorkDate:yyyy-MM-dd} was {verb}";
        var body =
            $"Hi {employee.FullName},\r\n\r\n" +
            $"Your Add Time Entry request for {req.RequestedWorkDate:yyyy-MM-dd} was {verb} " +
            $"by {decider.FullName}.\r\n\r\n" +
            $"Check-in   : {ciLocal} {tz}\r\n" +
            $"Check-out  : {coLocal} {tz}\r\n" +
            noteLine +
            "\r\n" +
            (approved
                ? "Your attendance record has been updated.\r\n"
                : "If you have questions, please reach out to your reviewer.\r\n") +
            "\r\n" +
            "This is an automated notification from the Attendance Monitoring System.\r\n";

        try
        {
            await _email.SendAsync(new[] { addr }, subject, body);
        }
        catch
        {
            // best-effort; the decision itself is already persisted.
        }
    }

    private const int HistoryLimit = 200;

    private readonly IEmailSender _email;

    public AttendanceController(AppDbContext db, IEmailSender email) : base(db)
    {
        _email = email;
    }

    // ------------------------------------------------------------------
    // History view
    // ------------------------------------------------------------------
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw)
    {
        var (target, start, end) = await ResolveFiltersAsync(userId, startRaw, endRaw);

        var records = await BuildQuery(target.Id, start, end).Take(HistoryLimit).ToListAsync();

        var allUsers = IsAdmin
            ? await (await GetVisibleUsersAsync()).OrderBy(u => u.FullName).ToListAsync()
            : new List<User>();

        // Surface any pending edit request on each row so the view can
        // disable the "Request edit" button and badge the row.
        var recordIds = records.Select(r => r.Id).ToList();
        var pending = await Db.AttendanceEditRequests
            .Where(r => recordIds.Contains(r.AttendanceId) && r.Status == "Pending")
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync();
        var pendingByAttendance = pending
            .GroupBy(r => r.AttendanceId)
            .ToDictionary(g => g.Key, g => g.First());

        // Pull the target's schedule rows for the range so the view can
        // render an overtime / undertime column without doing per-row
        // lookups. Per-date scheduling — missing dates are "off".
        var rangeStart = start ?? records.MinBy(r => r.WorkDate)?.WorkDate ?? PhTime.Today;
        var rangeEnd = end ?? records.MaxBy(r => r.WorkDate)?.WorkDate ?? PhTime.Today;
        var scheduleByDate = await Db.ScheduleEntries
            .Where(s => s.UserId == target.Id && s.WorkDate >= rangeStart && s.WorkDate <= rangeEnd)
            .ToDictionaryAsync(s => s.WorkDate);

        // Pull approved leave requests that overlap the displayed range
        // and explode them out one entry per calendar day. The view uses
        // this to render an "Approved leave" remark on those rows.
        var rangeStartDt = rangeStart.ToDateTime(TimeOnly.MinValue);
        var rangeEndDt = rangeEnd.ToDateTime(TimeOnly.MaxValue);
        var approvedLeaves = await Db.LeaveRequests
            .Where(l => l.UserId == target.Id
                        && l.Status == "Approved"
                        && l.StartDate <= rangeEndDt
                        && l.EndDate >= rangeStartDt)
            .ToListAsync();
        var approvedLeavesByDate = new Dictionary<DateOnly, LeaveRequest>();
        foreach (var lv in approvedLeaves)
        {
            var lvStart = DateOnly.FromDateTime(lv.StartDate);
            var lvEnd = DateOnly.FromDateTime(lv.EndDate);
            if (lvStart < rangeStart) lvStart = rangeStart;
            if (lvEnd > rangeEnd) lvEnd = rangeEnd;
            for (var d = lvStart; d <= lvEnd; d = d.AddDays(1))
            {
                approvedLeavesByDate[d] = lv;
            }
        }

        var vm = new AttendanceHistoryViewModel
        {
            Target = target,
            Records = records,
            AllUsers = allUsers,
            StartDate = start,
            EndDate = end,
            ViewerIsAdmin = IsAdmin,
            PendingRequestsByAttendance = pendingByAttendance,
            ScheduleByDate = scheduleByDate,
            ApprovedLeavesByDate = approvedLeavesByDate,
        };
        return View(vm);
    }

    // ------------------------------------------------------------------
    // CSV export
    // ------------------------------------------------------------------
    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw)
    {
        var (target, start, end) = await ResolveFiltersAsync(userId, startRaw, endRaw);
        var records = await BuildQuery(target.Id, start, end).ToListAsync();
        var tz = UserClock.Label(target);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[]
        {
            "Employee ID", "Username", "Full Name", "Email",
            $"Work Date ({tz})", $"Check-in ({tz})", $"Check-out ({tz})",
            "Duration (min)", "Status",
        }));

        foreach (var r in records)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                CsvField(target.EmployeeId ?? string.Empty),
                CsvField(target.Username),
                CsvField(target.FullName),
                CsvField(target.Email),
                CsvField(r.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                CsvField(UserClock.Format(target, r.CheckIn, "yyyy-MM-dd HH:mm")),
                CsvField(r.CheckOut is null ? string.Empty : UserClock.Format(target, r.CheckOut, "yyyy-MM-dd HH:mm")),
                CsvField(r.CheckOut is null ? string.Empty : r.DurationMinutes.ToString(CultureInfo.InvariantCulture)),
                CsvField(r.CheckOut is null ? "Open" : "Closed"),
            }));
        }

        var bits = new List<string> { target.Username };
        if (start is { } s) bits.Add(s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (end is { } e) bits.Add(e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var fileName = "attendance_" + string.Join("_", bits) + ".csv";

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", fileName);
    }

    // ------------------------------------------------------------------
    // Excel (.xlsx) export
    // ------------------------------------------------------------------
    [HttpGet("export.xlsx")]
    public async Task<IActionResult> ExportXlsx(
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw)
    {
        var (target, start, end) = await ResolveFiltersAsync(userId, startRaw, endRaw);
        var records = await BuildQuery(target.Id, start, end).ToListAsync();
        var tz = UserClock.Label(target);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Attendance");

        var headers = new[]
        {
            "Employee ID", "Username", "Full Name", "Email",
            $"Work Date ({tz})", $"Check-in ({tz})", $"Check-out ({tz})",
            "Duration (min)", "Status",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }
        var headerRow = ws.Row(1);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E50A2");
        headerRow.Style.Font.FontColor = XLColor.White;

        var r = 2;
        foreach (var rec in records)
        {
            ws.Cell(r, 1).Value = target.EmployeeId ?? string.Empty;
            ws.Cell(r, 2).Value = target.Username;
            ws.Cell(r, 3).Value = target.FullName;
            ws.Cell(r, 4).Value = target.Email;
            ws.Cell(r, 5).Value = rec.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ws.Cell(r, 6).Value = UserClock.Format(target, rec.CheckIn, "yyyy-MM-dd HH:mm");
            ws.Cell(r, 7).Value = rec.CheckOut is null ? string.Empty : UserClock.Format(target, rec.CheckOut, "yyyy-MM-dd HH:mm");
            if (rec.CheckOut is null)
            {
                ws.Cell(r, 8).Value = string.Empty;
            }
            else
            {
                ws.Cell(r, 8).Value = rec.DurationMinutes;
            }
            ws.Cell(r, 9).Value = rec.CheckOut is null ? "Open" : "Closed";
            r++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        var bits = new List<string> { target.Username };
        if (start is { } s) bits.Add(s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (end is { } e) bits.Add(e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var fileName = "attendance_" + string.Join("_", bits) + ".xlsx";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    // ------------------------------------------------------------------
    // Check-in / Check-out
    // ------------------------------------------------------------------
    [HttpPost("/check-in")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckIn(
        [FromForm] string? photo = null,
        [FromForm] bool force = false,
        [FromForm(Name = "lat")] double? lat = null,
        [FromForm(Name = "lng")] double? lng = null,
        [FromForm(Name = "accuracy")] double? accuracy = null,
        [FromForm(Name = "activity_tag")] string? activityTag = null)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var safePhoto = SanitizePhoto(photo);
        if (safePhoto is null)
        {
            // Selfie is required — reject the request and bounce back to
            // the dashboard with a clear error. The camera step happens
            // client-side so this should only fire if the user bypassed
            // the modal or the browser blocked the camera entirely.
            TempData.Flash(
                "A selfie photo is required to check in. Please allow camera access and try again.",
                "error");
            return RedirectToAction("Index", "Dashboard");
        }

        // ----- Schedule guardrail -------------------------------------
        // Refuse clock-in when the user is outside their scheduled window
        // (e.g. clocked-in on a day off, or hours before shift starts).
        // Pure admins are exempt because they don't run a fixed schedule.
        //
        // Support (24/7 rotation) users are NOT exempt: they get a
        // separate gate (`CanClockInNowForSupportAsync`) that accepts
        // multiple shifts per day and a 60-minute pre-shift grace, but
        // still rejects clock-ins more than 60 minutes before any
        // upcoming start. See task #5.
        //
        // For everyone else the user can override with a confirmation
        // (`force=true`) only when they're attempting an *early* clock-in
        // within the same calendar day; "off day" check-ins always require
        // an admin to edit the schedule first so the report figures stay
        // accurate.
        //
        // The policy also tells us which calendar date the new attendance
        // row should be recorded against (`EffectiveWorkDate`). For most
        // clock-ins that's today, but cross-midnight back-to-back shifts
        // (e.g. clocking in at 23:55 PHT for a 00:00 shift) post their
        // row against tomorrow's date so the closed-today guard, daily
        // report, and late-arrival evaluation all line up with the shift
        // the user is actually working.
        DateOnly? policyWorkDate = null;
        if (!string.Equals(me.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            if (me.IsSupport)
            {
                // Support: no force override; the 60-min window is the
                // only sanctioned early-clock-in path.
                var supportCheck = await CanClockInNowForSupportAsync(me);
                if (!supportCheck.Allowed)
                {
                    TempData.Flash(supportCheck.Message!, "warning");
                    return RedirectToAction("Index", "Dashboard");
                }
                policyWorkDate = supportCheck.EffectiveWorkDate;
            }
            else if (!force)
            {
                var scheduleCheck = await CanClockInNowAsync(me);
                if (!scheduleCheck.Allowed)
                {
                    TempData.Flash(scheduleCheck.Message!, "warning");
                    if (scheduleCheck.AllowForceOverride)
                    {
                        TempData["AllowForceClockIn"] = "1";
                    }
                    return RedirectToAction("Index", "Dashboard");
                }
                // Early clock-in allowed with a heads-up message.
                if (scheduleCheck.Message is not null)
                {
                    TempData.Flash(scheduleCheck.Message, "info");
                }
                policyWorkDate = scheduleCheck.EffectiveWorkDate;
            }
        }

        var today = PhTime.Today;
        // For non-Support, non-admin users the policy may redirect this
        // clock-in to tomorrow's calendar date (cross-midnight grace).
        // Fall back to today for admins / Support / force-overrides.
        var workDate = policyWorkDate ?? today;

        // Block a second check-in while ANY session is still open (today
        // or earlier). Carries-over and same-day double clock-ins both
        // funnel through this single check.
        var anyOpen = await Db.Attendances
            .Where(a => a.UserId == me.Id && a.CheckOut == null)
            .OrderByDescending(a => a.WorkDate)
            .ThenByDescending(a => a.CheckIn)
            .FirstOrDefaultAsync();
        if (anyOpen is not null)
        {
            if (anyOpen.WorkDate == today)
            {
                TempData.Flash("You're already checked in.", "info");
            }
            else
            {
                TempData.Flash(
                    $"You still have an open session from {anyOpen.WorkDate:yyyy-MM-dd}. " +
                    "Please check out of that first.",
                    "warning");
            }
            return RedirectToAction("Index", "Dashboard");
        }

        // Support: multiple closed sessions per day are fine — each shift
        // gets its own attendance row. Everyone else: still one-per-day,
        // but the "day" is the workDate the policy picked (today, unless
        // we're inside tomorrow's pre-shift grace window).
        if (!me.IsSupport)
        {
            var closedSame = await Db.Attendances
                .FirstOrDefaultAsync(a => a.UserId == me.Id && a.WorkDate == workDate && a.CheckOut != null);
            if (closedSame is not null)
            {
                TempData.Flash("You've already completed today's attendance.", "warning");
                return RedirectToAction("Index", "Dashboard");
            }
        }

        // ----- Geofence validation (Onsite only) ----------------------
        // Skip when there are no configured sites for the BU yet — lets
        // teams roll the feature out gradually. For Onsite Coalition
        // shifts (PH), an outside-radius coordinate blocks the clock-in
        // unless the user re-tries from the actual site.
        int? checkInSiteId = null;
        var todaySchedule = await Db.ScheduleEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == me.Id && s.WorkDate == workDate);
        var workType = todaySchedule?.EffectiveWorkType ?? "Unknown";
        if (string.Equals(workType, "Onsite", StringComparison.OrdinalIgnoreCase))
        {
            var fence = await GeofenceCheck.EvaluateAsync(Db, me, lat, lng);
            if (fence.Status == "NoSites")
            {
                // Sites not configured yet for this BU — allow.
            }
            else if (fence.Status == "MissingCoords")
            {
                TempData.Flash(
                    "Location is required for Onsite check-ins. Please enable location services and try again.",
                    "warning");
                return RedirectToAction("Index", "Dashboard");
            }
            else if (!fence.Ok)
            {
                var dist = fence.DistanceMeters is { } d ? $" ({(int)d}m from {fence.Site?.Name})" : "";
                TempData.Flash(
                    $"You're outside the allowed site radius{dist}. Please move closer to the office and retry.",
                    "warning");
                return RedirectToAction("Index", "Dashboard");
            }
            else
            {
                checkInSiteId = fence.Site?.Id;
            }
        }

        // ----- Face hash check (best-effort) -------------------------
        // Compute a perceptual hash of the submitted selfie and compare
        // against the enrolled hash on the user record. Mismatches do
        // NOT block check-ins — they are recorded so admins can audit.
        //
        // First-time auto-enrollment: when the user has no FaceHash yet
        // we treat THIS selfie as the enrollment photo. The status is
        // recorded as "AutoEnrolled" so admins can see which row was
        // used as the seed. Future check-ins compare against it.
        var newHash = FaceHash.Compute(safePhoto);
        string? faceStatus = null;
        int? faceDistance = null;
        if (string.IsNullOrEmpty(me.FaceHash))
        {
            if (newHash is not null)
            {
                me.FaceHash = newHash;
                me.FaceEnrolledAt = DateTime.UtcNow;
                faceStatus = "AutoEnrolled";
            }
            else
            {
                faceStatus = "NotEnrolled";
            }
        }
        else if (newHash is null)
        {
            faceStatus = "Unavailable";
        }
        else
        {
            faceDistance = FaceHash.Distance(me.FaceHash, newHash);
            faceStatus = faceDistance <= Constants.FaceMatchMaxDistance ? "Match" : "Mismatch";
        }

        // Normalise the activity tag — trim and cap at 64 chars, drop
        // empty strings so they don't pollute reports.
        var safeTag = string.IsNullOrWhiteSpace(activityTag)
            ? null
            : activityTag.Trim()[..Math.Min(64, activityTag.Trim().Length)];

        Db.Attendances.Add(new Attendance
        {
            UserId = me.Id,
            WorkDate = workDate,
            CheckIn = DateTime.UtcNow,
            CheckInPhoto = safePhoto,
            CheckInLatitude = lat,
            CheckInLongitude = lng,
            CheckInAccuracy = accuracy,
            CheckInSiteId = checkInSiteId,
            ActivityTag = safeTag,
            FaceMatchStatus = faceStatus,
            FaceMatchDistance = faceDistance,
        });
        await Db.SaveChangesAsync();

        var flash = "Checked in. Have a productive day!";
        if (faceStatus == "Mismatch")
            flash += " (Face check raised a flag — please re-enroll if this is unexpected.)";
        TempData.Flash(flash, faceStatus == "Mismatch" ? "warning" : "success");
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost("/check-out")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckOut([FromForm] string? photo = null)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        // Selfie capture is only required on check-in. On check-out the photo
        // input is optional — if one is supplied we still sanitise and store it,
        // but a missing photo is no longer an error.
        var safePhoto = SanitizePhoto(photo);
        if (!string.IsNullOrWhiteSpace(photo) && safePhoto is null)
        {
            TempData.Flash("The selfie image is invalid or too large.", "danger");
            return RedirectToAction("Index", "Dashboard");
        }

        // Find the most recent OPEN attendance row regardless of WorkDate.
        // Users who forget to check out at the end of the day still need to
        // close their previous session before they can check in again, so
        // we don't constrain this query to "today" anymore.
        var record = await Db.Attendances
            .Where(a => a.UserId == me.Id && a.CheckOut == null)
            .OrderByDescending(a => a.WorkDate)
            .ThenByDescending(a => a.CheckIn)
            .FirstOrDefaultAsync();

        if (record is null)
        {
            TempData.Flash("No active check-in to close.", "warning");
        }
        else
        {
            // Block the check-out if the user hasn't yet rendered the
            // required hours (8h Support / 9h everyone else), UNLESS an
            // approved partial-day leave request (HalfDay / Undertime /
            // EarlyCheckout) covers today's local calendar date.
            var requiredMinutes = LateCheck.RequiredRenderHours(me) * 60;
            var elapsedMinutes = (int)Math.Round(
                (DateTime.UtcNow - record.CheckIn).TotalMinutes);
            if (elapsedMinutes < requiredMinutes)
            {
                var localToday = UserClock.TodayFor(me);
                var localTodayStart = localToday.ToDateTime(TimeOnly.MinValue);
                var localTodayEnd = localToday.ToDateTime(TimeOnly.MaxValue);
                var hasApprovedPartialDayLeave = await Db.LeaveRequests.AnyAsync(l =>
                    l.UserId == me.Id
                    && (l.LeaveType == "HalfDay"
                        || l.LeaveType == "Undertime"
                        || l.LeaveType == "EarlyCheckout")
                    && l.Status == "Approved"
                    && l.StartDate <= localTodayEnd
                    && l.EndDate >= localTodayStart);

                if (!hasApprovedPartialDayLeave)
                {
                    var remain = requiredMinutes - elapsedMinutes;
                    var rh = remain / 60;
                    var rm = remain % 60;
                    TempData.Flash(
                        $"You need {requiredMinutes / 60}h of rendered time before checking out " +
                        $"({rh:D2}:{rm:D2} remaining). File a \"Half day\", \"Undertime\", or " +
                        $"\"Early checkout\" leave request to be allowed to check out sooner.",
                        "warning");
                    return RedirectToAction("Index", "Dashboard");
                }
            }

            record.CheckOut = DateTime.UtcNow;
            if (safePhoto is not null)
            {
                record.CheckOutPhoto = safePhoto;
            }
            await Db.SaveChangesAsync();
            TempData.Flash("Checked out. See you next time!", "success");
        }
        return RedirectToAction("Index", "Dashboard");
    }

    // ------------------------------------------------------------------
    // Admin: edit a single attendance row (Check-in / Check-out timestamps)
    // ------------------------------------------------------------------
    [HttpGet("{id:int}/edit")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Edit(int id)
    {
        var record = await Db.Attendances
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        if (!await CanViewUserAsync(record.UserId)) return Forbid();

        return View("Edit", record);
    }

    /// <summary>
    /// Admin-only edit of a single attendance row. Accepts PHT-formatted
    /// datetimes from the <c>&lt;input type="datetime-local"&gt;</c> control,
    /// converts back to UTC for storage. CheckOut is optional — clearing the
    /// field re-opens the row (the user can then check out normally).
    /// </summary>
    [HttpPost("{id:int}/edit")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPost(
        int id,
        [FromForm] string check_in,
        [FromForm] string? check_out,
        [FromForm] string? work_date,
        [FromForm(Name = "return_url")] string? returnUrl)
    {
        var record = await Db.Attendances
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        if (!await CanViewUserAsync(record.UserId)) return Forbid();

        if (!TryParseUserDateTime(record.User, check_in, out var ciUtc))
        {
            TempData.Flash("Check-in time is required and must be a valid date/time.", "danger");
            return View("Edit", record);
        }

        DateTime? coUtc = null;
        if (!string.IsNullOrWhiteSpace(check_out))
        {
            if (!TryParseUserDateTime(record.User, check_out, out var v))
            {
                TempData.Flash("Check-out time is invalid.", "danger");
                return View("Edit", record);
            }
            if (v <= ciUtc)
            {
                TempData.Flash("Check-out must be after check-in.", "danger");
                return View("Edit", record);
            }
            coUtc = v;
        }

        // Allow admins to fix the WorkDate too — typically the date in PHT
        // that the user was working, which can be one day before the
        // check-in's UTC date for late-night shifts.
        if (!string.IsNullOrWhiteSpace(work_date) &&
            DateOnly.TryParseExact(work_date, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var wd))
        {
            record.WorkDate = wd;
        }

        record.CheckIn = ciUtc;
        record.CheckOut = coUtc;
        await Db.SaveChangesAsync();
        TempData.Flash("Attendance updated.", "success");

        // Bounce back to where the admin came from (filtered attendance list).
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(Index), new { user_id = record.UserId });
    }

    /// <summary>
    /// Admin-only deletion of a single attendance row. Used to wipe accidental
    /// duplicate check-ins.
    /// </summary>
    [HttpPost("{id:int}/delete")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        int id,
        [FromForm(Name = "return_url")] string? returnUrl)
    {
        var record = await Db.Attendances.FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        if (!await CanViewUserAsync(record.UserId)) return Forbid();

        Db.Attendances.Remove(record);
        await Db.SaveChangesAsync();
        TempData.Flash("Attendance record deleted.", "success");

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(Index), new { user_id = record.UserId });
    }

    /// <summary>
    /// Parses a <c>&lt;input type="datetime-local"&gt;</c> value (always in the
    /// user's local timezone, here PHT) back into a UTC <see cref="DateTime"/>.
    /// Accepts both <c>yyyy-MM-ddTHH:mm</c> and <c>yyyy-MM-ddTHH:mm:ss</c>.
    /// </summary>
    /// <summary>
    /// Region-aware parser for the HTML <c>datetime-local</c> input
    /// format. Treats the supplied wall-clock value as being in the
    /// user's local zone (IST for India BUs, PHT otherwise) and
    /// converts it to UTC for storage.
    /// </summary>
    private static bool TryParseUserDateTime(User? user, string raw, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var formats = new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" };
        if (!DateTime.TryParseExact(raw.Trim(), formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            return false;
        }
        utc = DateTime.SpecifyKind(local - UserClock.DisplayOffset(user), DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Validates a posted selfie and trims it to a sane size. Accepts only
    /// a JPEG/PNG/WebP data URL up to ~4 MB; anything else is dropped.
    /// </summary>
    private static string? SanitizePhoto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length > 4 * 1024 * 1024) return null;

        var commaIdx = raw.IndexOf(',');
        if (commaIdx <= 0) return null;

        var mediaType = raw[..commaIdx].Trim().ToLowerInvariant();
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw[(commaIdx + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length == 0 || bytes.Length > 3 * 1024 * 1024) return null;

        var signatureMatches = mediaType switch
        {
            "data:image/jpeg;base64" => bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "data:image/png;base64" => bytes.Length >= 8
                && bytes.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "data:image/webp;base64" => bytes.Length >= 12
                && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };

        return signatureMatches
            ? $"{mediaType},{Convert.ToBase64String(bytes)}"
            : null;
    }

    /// <summary>
    /// Schedule guardrail for <see cref="CheckIn"/>. Returns whether the
    /// given user is allowed to clock in *right now* based on today's
    /// schedule row (and tomorrow's row when we're inside its pre-shift
    /// grace, so back-to-back shifts that cross midnight still work).
    /// The decision uses Philippine wall time:
    /// <list type="bullet">
    ///   <item>Current time within today's shift window (incl. 15-min
    ///         pre-shift grace) → allowed.</item>
    ///   <item>Inside tomorrow's 15-min pre-shift grace (e.g. clocking in
    ///         at 23:55 for a 00:00 shift) → allowed, attendance row is
    ///         recorded against tomorrow's date.</item>
    ///   <item>Day-off (or no <c>IsWorking</c> entry) and no upcoming
    ///         tomorrow-grace match → blocked, no override.</item>
    ///   <item>Working day, more than 15 minutes before start → allowed
    ///         with a heads-up; the daily report flags the variance.</item>
    ///   <item>Working day, after end-of-shift → allowed (user clocking
    ///         in late still happens; the report flags the variance).</item>
    /// </list>
    /// </summary>
    private async Task<ClockInPolicy> CanClockInNowAsync(User me)
    {
        const int graceMinutesBefore = 15;

        // Use the user's own local calendar / clock so India employees
        // are evaluated against IST (UTC+5:30) instead of PHT.
        var today = UserClock.TodayFor(me);
        var tzLabel = UserClock.Label(me);
        var nowLocal = UserClock.NowFor(me);
        var nowTime = TimeOnly.FromDateTime(nowLocal.DateTime);
        // Effective lookup: falls back to the CSI default (Mon–Fri
        // 09:00–18:00 Onsite, weekends Dayoff) when the admin hasn't
        // authored a specific row, so a newly approved employee isn't
        // locked out of clock-in on day one.
        var todaySched = await DbInitializer.GetEffectiveScheduleForDateAsync(Db, me, today);
        var todayIdx = ((int)today.DayOfWeek + 6) % 7;

        // ----- 1. Currently inside today's shift window? --------------
        // For wrap-around shifts (e.g. 22:00 -> 06:00) accept either side
        // of midnight without trying to map current time onto a 0..48
        // axis. The helper on ScheduleEntry already encodes this.
        if (todaySched is { IsWorking: true, StartTime: not null, EndTime: not null }
            && todaySched.CoversTime(nowTime))
        {
            return new ClockInPolicy(true, false, null, today);
        }

        // ----- 2. Inside today's 15-minute pre-shift grace? -----------
        if (todaySched is { IsWorking: true, StartTime: { } stToday })
        {
            var startWithGrace = stToday.AddMinutes(-graceMinutesBefore);
            if (nowTime >= startWithGrace && nowTime < stToday)
            {
                return new ClockInPolicy(true, false, null, today);
            }
        }

        // ----- 3. Inside TOMORROW's 15-minute pre-shift grace? --------
        // Handles back-to-back shifts that straddle midnight: today's
        // shift is already done (or it's a day off) and the next
        // scheduled shift starts within the last 15 minutes of the
        // current calendar day. The new attendance row is recorded
        // against tomorrow's date so the closed-today guard, daily
        // report, and late-arrival evaluation all align with the
        // shift the user is actually working.
        var tomorrow = today.AddDays(1);
        var tomorrowSched = await DbInitializer.GetEffectiveScheduleForDateAsync(Db, me, tomorrow);
        if (tomorrowSched is { IsWorking: true, StartTime: { } stTom })
        {
            var tomorrowStartLocal = tomorrow.ToDateTime(stTom);
            var minutesUntilTomorrow =
                (tomorrowStartLocal - nowLocal.DateTime).TotalMinutes;
            if (minutesUntilTomorrow >= 0 && minutesUntilTomorrow <= graceMinutesBefore)
            {
                return new ClockInPolicy(true, false, null, tomorrow);
            }
        }

        // ----- 4. No working shift today → blocked --------------------
        if (todaySched is null || !todaySched.IsWorking
            || todaySched.StartTime is null || todaySched.EndTime is null)
        {
            var dayLabel = today.ToString("dddd, MMM d");
            return new ClockInPolicy(
                Allowed: false,
                AllowForceOverride: false,
                Message: $"You don't have a shift scheduled for {dayLabel}. "
                       + "Please ask your administrator to add today to your "
                       + "schedule before clocking in.",
                EffectiveWorkDate: today);
        }

        var start = todaySched.StartTime.Value;

        // ----- 5. Outside the window: distinguish too-early vs after --
        var beforeShift = nowTime < start;
        if (beforeShift)
        {
            // Allow the clock-in but surface a heads-up so the user knows
            // they're early. The daily report flags the variance anyway.
            return new ClockInPolicy(
                Allowed: true,
                AllowForceOverride: false,
                Message: $"Heads-up: your shift starts at "
                       + $"{start:HH\\:mm} {tzLabel} (today is {Constants.WeekdayNames[todayIdx]}). "
                       + "You're clocking in early.",
                EffectiveWorkDate: today);
        }

        // After end-of-shift: allow but warn. The user may have legitimately
        // arrived late; the daily report will surface the variance.
        return new ClockInPolicy(true, false, null, today);
    }

    /// <summary>
    /// Support (24/7 rotation) variant of the clock-in policy. Unlike the
    /// regular gate this one explicitly supports multiple shifts per day
    /// and overnight wrap-around: it scans yesterday/today/tomorrow to
    /// find the active or next shift. Early clock-ins are allowed (with a
    /// heads-up message) to match the regular gate's behaviour — the only
    /// case still blocked is when there is no upcoming shift at all,
    /// since then there is no reference window for the report.
    /// </summary>
    private async Task<ClockInPolicy> CanClockInNowForSupportAsync(User me)
    {
        const int graceMinutesBefore = 60;

        var today = PhTime.Today;
        var nowPh = PhTime.Now;
        var nowTime = TimeOnly.FromDateTime(nowPh.DateTime);

        var schedYesterday = await DbInitializer.GetScheduleForDateAsync(Db, me, today.AddDays(-1));
        var schedToday     = await DbInitializer.GetScheduleForDateAsync(Db, me, today);
        var schedTomorrow  = await DbInitializer.GetScheduleForDateAsync(Db, me, today.AddDays(1));

        // Currently inside today's shift window?
        if (schedToday is { IsWorking: true, StartTime: not null, EndTime: not null }
            && schedToday.CoversTime(nowTime))
        {
            return new ClockInPolicy(true, false, null, today);
        }

        // Yesterday's overnight shift (e.g. 22:00 -> 06:00) may still be
        // covering today's early hours.
        if (schedYesterday is { IsWorking: true, StartTime: { } sy, EndTime: { } ey }
            && ey <= sy
            && nowTime < ey)
        {
            return new ClockInPolicy(true, false, null, today);
        }

        // Compute the next upcoming shift start (today first, then tomorrow).
        DateTime? nextStartPh = null;
        DateOnly nextStartDate = today;
        if (schedToday is { IsWorking: true, StartTime: { } stT })
        {
            var startPh = today.ToDateTime(stT);
            if (startPh > nowPh.DateTime)
            {
                nextStartPh = startPh;
                nextStartDate = today;
            }
        }
        if (nextStartPh is null
            && schedTomorrow is { IsWorking: true, StartTime: { } stTom })
        {
            nextStartPh = today.AddDays(1).ToDateTime(stTom);
            nextStartDate = today.AddDays(1);
        }

        if (nextStartPh is null)
        {
             // Support users can work ad-hoc coverage after a completed
             // shift, so don't hard-block them when there is no authored
             // upcoming row. Record the new session against today and let
             // reporting treat the missing schedule as not-applicable.
             return new ClockInPolicy(
              Allowed: true,
              AllowForceOverride: false,
              Message: "No upcoming shift is scheduled right now. "
                  + "Starting a new support session anyway.",
              EffectiveWorkDate: today);
        }

        var minutesEarly = (nextStartPh.Value - nowPh.DateTime).TotalMinutes;
        if (minutesEarly <= graceMinutesBefore)
        {
            // Inside the 60-minute pre-shift grace: silent allow.
            return new ClockInPolicy(true, false, null, nextStartDate);
        }

        // Earlier than the grace window: allow but surface a heads-up so
        // the user knows the start time. Mirrors CanClockInNowAsync — we
        // do not block early clock-ins, the daily report flags variances.
        var when = nextStartPh.Value;
        var whenLabel = nextStartDate == today
            ? "today"
            : $"on {when:MMM d}";
        return new ClockInPolicy(
            Allowed: true,
            AllowForceOverride: false,
            Message: $"Heads-up: your next shift starts at "
                   + $"{when:HH\\:mm} PHT {whenLabel}. You're clocking in early.",
            EffectiveWorkDate: nextStartDate);
    }

    private sealed record ClockInPolicy(
        bool Allowed,
        bool AllowForceOverride,
        string? Message,
        DateOnly EffectiveWorkDate);

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private async Task<(User target, DateOnly? start, DateOnly? end)>
        ResolveFiltersAsync(int? userId, string? startRaw, string? endRaw)
    {
        var me = (await GetCurrentUserAsync())!;

        var target = me;
        if (IsAdmin && userId is int uid && await CanViewUserAsync(uid))
        {
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == uid) ?? me;
        }

        var start = ParseDate(startRaw);
        var end = ParseDate(endRaw);
        return (target, start, end);
    }

    private IOrderedQueryable<Attendance> BuildQuery(int userId, DateOnly? start, DateOnly? end)
    {
        var q = Db.Attendances.AsQueryable().Where(a => a.UserId == userId);
        if (start is DateOnly s) q = q.Where(a => a.WorkDate >= s);
        if (end is DateOnly e) q = q.Where(a => a.WorkDate <= e);
        return q.OrderByDescending(a => a.WorkDate).ThenByDescending(a => a.CheckIn);
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    private static string CsvField(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        var v = value.Replace("\"", "\"\"");
        return needsQuote ? $"\"{v}\"" : v;
    }

    // ------------------------------------------------------------------
    // Employee edit-request workflow
    // ------------------------------------------------------------------
    // Employees who notice they forgot to clock in / out (or wrote down
    // the wrong time) can submit a proposed correction. Admins and PMs
    // review pending requests on /attendance/edit-requests and either
    // apply them onto the live Attendance row (Approve) or close them
    // out with a note (Reject). Admins / PMs editing a row through the
    // existing /attendance/{id}/edit page bypass this workflow entirely.

    /// <summary>
    /// Renders the "Request edit" form for one of the employee's own
    /// attendance rows. Reuses any in-flight pending request so the
    /// employee can amend their proposal before a reviewer sees it.
    /// </summary>
    [HttpGet("{id:int}/request-edit")]
    public async Task<IActionResult> RequestEdit(int id)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var record = await Db.Attendances
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        if (record.UserId != me.Id) return Forbid();

        var existing = await Db.AttendanceEditRequests
            .Where(r => r.AttendanceId == id && r.Status == "Pending")
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync();

        var vm = new AttendanceEditRequestFormViewModel
        {
            Record = record,
            Existing = existing,
            ReturnUrl = Request.Headers["Referer"].ToString(),
        };
        return View("RequestEdit", vm);
    }

    /// <summary>
    /// Saves a new pending edit request (or updates the user's existing
    /// pending one — there's only ever one open per attendance row to
    /// keep the reviewer queue tidy).
    /// </summary>
    [HttpPost("{id:int}/request-edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEditPost(
        int id,
        [FromForm] string check_in,
        [FromForm] string? check_out,
        [FromForm] string? work_date,
        [FromForm] string? reason,
        [FromForm] string? proof_photo,
        [FromForm(Name = "return_url")] string? returnUrl)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var record = await Db.Attendances
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (record is null) return NotFound();
        if (record.UserId != me.Id) return Forbid();

        async Task<IActionResult> Reshow(string flash)
        {
            TempData.Flash(flash, "danger");
            var existing = await Db.AttendanceEditRequests
                .Where(r => r.AttendanceId == id && r.Status == "Pending")
                .OrderByDescending(r => r.RequestedAt)
                .FirstOrDefaultAsync();
            return View("RequestEdit", new AttendanceEditRequestFormViewModel
            {
                Record = record,
                Existing = existing,
                ReturnUrl = returnUrl,
            });
        }

        if (!TryParseUserDateTime(me, check_in, out var ciUtc))
        {
            return await Reshow("Check-in time is required and must be a valid date/time.");
        }

        DateTime? coUtc = null;
        if (!string.IsNullOrWhiteSpace(check_out))
        {
            if (!TryParseUserDateTime(me, check_out, out var v))
            {
                return await Reshow("Check-out time is invalid.");
            }
            if (v <= ciUtc)
            {
                return await Reshow("Check-out must be after check-in.");
            }
            coUtc = v;
        }

        var wd = record.WorkDate;
        if (!string.IsNullOrWhiteSpace(work_date)
            && DateOnly.TryParseExact(work_date, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsedWd))
        {
            wd = parsedWd;
        }

        var reasonText = (reason ?? string.Empty).Trim();
        if (reasonText.Length < 5)
        {
            return await Reshow(
                "Please add a short reason (at least 5 characters) so your PM understands the change.");
        }
        if (reasonText.Length > 500) reasonText = reasonText[..500];

        var safeProof = SanitizePhoto(proof_photo);
        if (safeProof is null)
        {
            return await Reshow("Image proof is required for time adjustment requests.");
        }

        // Reuse the existing pending row if one's already on file so we
        // don't pile up duplicates in the reviewer queue.
        var pending = await Db.AttendanceEditRequests
            .Where(r => r.AttendanceId == id && r.Status == "Pending")
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync();

        if (pending is null)
        {
            pending = new AttendanceEditRequest
            {
                AttendanceId = id,
                RequestedByUserId = me.Id,
                RequestedAt = DateTime.UtcNow,
            };
            Db.AttendanceEditRequests.Add(pending);
        }
        else
        {
            pending.RequestedAt = DateTime.UtcNow;
        }

        pending.RequestedWorkDate = wd;
        pending.RequestedCheckIn = ciUtc;
        pending.RequestedCheckOut = coUtc;
        pending.Reason = reasonText;
        pending.ProofPhoto = safeProof;
        pending.Status = "Pending";
        pending.DecidedAt = null;
        pending.DecidedByUserId = null;
        pending.DecisionNote = null;

        await Db.SaveChangesAsync();
        TempData.Flash(
            "Edit request submitted. Your PM will review and either approve or reject it.",
            "success");

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Reviewer queue — pending edit requests visible to the current
    /// admin / PM, plus the 25 most recent decided ones for context.
    /// </summary>
    [HttpGet("edit-requests")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> EditRequests()
    {
        // Pure admins review every request, including requests submitted by
        // admin accounts. PM/PgM reviewers retain their normal user scope.
        var visible = await GetVisibleUsersAsync(includeAdmins: IsPureAdmin);
        var visibleIds = await visible.Select(u => u.Id).ToListAsync();

        var pending = await Db.AttendanceEditRequests
            .Include(r => r.Attendance)
            .Include(r => r.RequestedByUser)
            .Where(r => r.Status == "Pending" && visibleIds.Contains(r.RequestedByUserId))
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

        var recent = await Db.AttendanceEditRequests
            .Include(r => r.Attendance)
            .Include(r => r.RequestedByUser)
            .Include(r => r.DecidedByUser)
            .Where(r => r.Status != "Pending" && visibleIds.Contains(r.RequestedByUserId))
            .OrderByDescending(r => r.DecidedAt)
            .Take(25)
            .ToListAsync();

        return View("EditRequests", new EditRequestsIndexViewModel
        {
            Pending = pending,
            Recent = recent,
        });
    }

    /// <summary>
    /// Apply the pending request: copy the proposed times onto the live
    /// Attendance row, mark the request Approved, and stamp the reviewer.
    /// </summary>
    [HttpPost("edit-requests/{id:int}/approve")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveEditRequest(
        int id,
        [FromForm] string? note)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var req = await Db.AttendanceEditRequests
            .Include(r => r.Attendance)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (req is null) return NotFound();
        if (req.Status != "Pending")
        {
            TempData.Flash("This request was already decided.", "warning");
            return RedirectToAction(nameof(EditRequests));
        }
        if (!await CanViewUserAsync(req.RequestedByUserId)) return Forbid();
        if (req.Attendance is null) return NotFound();

        if (req.RequestedCheckOut is { } co && co <= req.RequestedCheckIn)
        {
            TempData.Flash(
                "Cannot approve: the proposed check-out is not after the check-in.",
                "danger");
            return RedirectToAction(nameof(EditRequests));
        }

        req.Attendance.WorkDate = req.RequestedWorkDate;
        req.Attendance.CheckIn = req.RequestedCheckIn;
        req.Attendance.CheckOut = req.RequestedCheckOut;

        req.Status = "Approved";
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedByUserId = me.Id;
        req.DecisionNote = string.IsNullOrWhiteSpace(note)
            ? null
            : note.Trim().Length > 500 ? note.Trim()[..500] : note.Trim();

        await Db.SaveChangesAsync();
        await SendEditRequestDecisionEmailAsync(req, me, approved: true);
        TempData.Flash(
            $"Approved — {req.RequestedByUser?.FullName ?? "user"}'s attendance has been updated.",
            "success");
        return RedirectToAction(nameof(EditRequests));
    }

    /// <summary>
    /// Close a pending request without applying it. The optional note is
    /// shown to the requester on their attendance page.
    /// </summary>
    [HttpPost("edit-requests/{id:int}/reject")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectEditRequest(
        int id,
        [FromForm] string? note)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var req = await Db.AttendanceEditRequests
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (req is null) return NotFound();
        if (req.Status != "Pending")
        {
            TempData.Flash("This request was already decided.", "warning");
            return RedirectToAction(nameof(EditRequests));
        }
        if (!await CanViewUserAsync(req.RequestedByUserId)) return Forbid();

        req.Status = "Rejected";
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedByUserId = me.Id;
        req.DecisionNote = string.IsNullOrWhiteSpace(note)
            ? null
            : note.Trim().Length > 500 ? note.Trim()[..500] : note.Trim();

        await Db.SaveChangesAsync();
        // Best-effort: include the linked Attendance so the helper can
        // look up the affected employee (decision email goes to them,
        // not necessarily to the filer when a PM filed on behalf).
        var withAtt = await Db.AttendanceEditRequests
            .Include(r => r.Attendance)
            .FirstOrDefaultAsync(r => r.Id == req.Id);
        if (withAtt is not null) await SendEditRequestDecisionEmailAsync(withAtt, me, approved: false);
        TempData.Flash("Request rejected.", "info");
        return RedirectToAction(nameof(EditRequests));
    }

    // ------------------------------------------------------------------
    // Bulk approve / reject for the pending edit-requests queue. The view
    // POSTs an array of selected request ids; we walk them sequentially
    // reusing the same validation as the single-row endpoints so anything
    // we can't act on (visibility, already decided, invalid times) is
    // skipped with a tally instead of failing the whole batch.
    // ------------------------------------------------------------------
    [HttpPost("edit-requests/bulk-approve")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkApproveEditRequests(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideEditRequestsAsync(ids, note, approve: true);

    [HttpPost("edit-requests/bulk-reject")]
    [Authorize(Policy = "AdminOnly")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkRejectEditRequests(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideEditRequestsAsync(ids, note, approve: false);

    private async Task<IActionResult> BulkDecideEditRequestsAsync(
        int[]? ids, string? note, bool approve)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var idList = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (idList.Count == 0)
        {
            TempData.Flash("Select at least one request to act on.", "warning");
            return RedirectToAction(nameof(EditRequests));
        }

        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length > 500) trimmed = trimmed[..500];
        var noteValue = trimmed.Length == 0 ? null : trimmed;

        var rows = await Db.AttendanceEditRequests
            .Include(r => r.Attendance)
            .Where(r => idList.Contains(r.Id))
            .ToListAsync();

        var applied = 0;
        var skipped = 0;
        foreach (var req in rows)
        {
            if (req.Status != "Pending") { skipped++; continue; }
            if (!await CanViewUserAsync(req.RequestedByUserId)) { skipped++; continue; }

            if (approve)
            {
                if (req.Attendance is null) { skipped++; continue; }
                if (req.RequestedCheckOut is { } co && co <= req.RequestedCheckIn)
                {
                    skipped++;
                    continue;
                }
                req.Attendance.WorkDate = req.RequestedWorkDate;
                req.Attendance.CheckIn = req.RequestedCheckIn;
                req.Attendance.CheckOut = req.RequestedCheckOut;
                req.Status = "Approved";
            }
            else
            {
                req.Status = "Rejected";
            }
            req.DecidedAt = DateTime.UtcNow;
            req.DecidedByUserId = me.Id;
            req.DecisionNote = noteValue;
            applied++;
        }

        await Db.SaveChangesAsync();

        var verb = approve ? "Approved" : "Rejected";
        var msg = skipped == 0
            ? $"{verb} {applied} request(s)."
            : $"{verb} {applied} request(s); skipped {skipped}.";
        TempData.Flash(msg, applied > 0 ? "success" : "warning");
        return RedirectToAction(nameof(EditRequests));
    }
}
