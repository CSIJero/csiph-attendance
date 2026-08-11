using System.Globalization;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Daily attendance report for admins and PMs. One row per user per workday
/// in the selected range, with computed columns matching the existing Excel
/// layout (Date, Key, Employee, BU, Role, times, hours rendered, attendance
/// status, violations, time difference vs. schedule, remarks).
///
/// Columns the system doesn't track natively (Online Status_Availability,
/// Response Compliance, Daily Report Submitted, Remarks) are present in the
/// export as empty cells so downstream tooling / managers can fill them in
/// without disturbing the column layout.
/// </summary>
[Authorize(Policy = "AdminOrOperations")]
[Route("reports")]
public class ReportsController : AppController
{
    public ReportsController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Daily(
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw,
        [FromQuery(Name = "bu")] string? bu,
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "include_off")] bool includeOff = false)
    {
        var vm = await BuildAsync(startRaw, endRaw, bu, userId, includeOff);
        return View("Daily", vm);
    }

    [HttpGet("daily.xlsx")]
    public async Task<IActionResult> DailyXlsx(
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw,
        [FromQuery(Name = "bu")] string? bu,
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "include_off")] bool includeOff = false)
    {
        var vm = await BuildAsync(startRaw, endRaw, bu, userId, includeOff);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Daily Report");

        // Column layout: dropped the three blank legacy columns (Online
        // Status_Availability / Response Compliance / Daily Report Submitted)
        // since the system doesn't capture those. Their slot is replaced by a
        // single "Email Notifications" column populated from NotificationLog.
        // Role is also omitted from the report (still tracked in the user
        // record); add it back here + in the row writes below if it's ever
        // needed by downstream tooling again.
        var headers = new[]
        {
            "Date", "Key", "Employee Name", "Employee ID", "Business Unit",
            "Start Time", "End Time", "Hours Rendered",
            "Attendance Status", "Late", "Email Notifications", "Time Difference", "Remarks",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }
        var head = ws.Row(1);
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#C6E0B4");
        head.Style.Font.FontColor = XLColor.Black;
        head.Style.Alignment.WrapText = true;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var r = 2;
        foreach (var row in vm.Rows)
        {
            ws.Cell(r, 1).Value = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ws.Cell(r, 2).Value = row.Key;
            ws.Cell(r, 3).Value = row.EmployeeName;
            ws.Cell(r, 4).Value = row.EmployeeId;
            ws.Cell(r, 5).Value = row.BusinessUnit;
            ws.Cell(r, 6).Value = string.IsNullOrEmpty(row.StartTimeDisplay)
                ? string.Empty
                : $"{row.StartTimeDisplay} {row.TimeZoneLabel}";
            ws.Cell(r, 7).Value = string.IsNullOrEmpty(row.EndTimeDisplay)
                ? string.Empty
                : $"{row.EndTimeDisplay} {row.TimeZoneLabel}";

            if (row.HoursRendered > 0 || row.StartTime is not null)
            {
                ws.Cell(r, 8).Value = Math.Round(row.HoursRendered, 2);
            }

            ws.Cell(r, 9).Value = row.AttendanceStatus;
            ws.Cell(r, 10).Value = row.LateStatus;
            ws.Cell(r, 11).Value = row.EmailNotifications; // always write, including 0
            ws.Cell(r, 12).Value = Math.Round(row.TimeDifference, 2);
            ws.Cell(r, 13).Value = row.Remarks ?? string.Empty;

            // Highlight incomplete rows the same way the source sheet does.
            if (string.Equals(row.AttendanceStatus, "Incomplete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.AttendanceStatus, "Incomplete Hours", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.AttendanceStatus, "Absent", StringComparison.OrdinalIgnoreCase))
            {
                ws.Cell(r, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#F4B084");
                ws.Cell(r, 9).Style.Font.Bold = true;
            }
            else if (string.Equals(row.AttendanceStatus, "Holiday", StringComparison.OrdinalIgnoreCase))
            {
                ws.Cell(r, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
            }
            else if (string.Equals(row.AttendanceStatus, "Onleave", StringComparison.OrdinalIgnoreCase))
            {
                ws.Cell(r, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");
            }
            else if (string.Equals(row.AttendanceStatus, "Complete", StringComparison.OrdinalIgnoreCase))
            {
                ws.Cell(r, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
            }

            // Tint Late cells so PMs can scan quickly.
            if (row.LateMinutes > 0)
            {
                ws.Cell(r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#F4B084");
                ws.Cell(r, 10).Style.Font.Bold = true;
            }
            else if (!string.IsNullOrEmpty(row.LateStatus))
            {
                ws.Cell(r, 10).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
            }

            // Flag any row that triggered offline alerts so it stands out
            // alongside the attendance status when scanning the sheet.
            if (row.EmailNotifications > 0)
            {
                ws.Cell(r, 11).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFD966");
                ws.Cell(r, 11).Style.Font.Bold = true;
            }

            r++;
        }

        ws.Columns().AdjustToContents();
        ws.Column(2).Width = 24; // Key column
        ws.Column(3).Width = 24; // Employee name
        ws.Column(10).Width = 14; // Late
        ws.Column(11).Width = 18; // Email Notifications
        ws.SheetView.FreezeRows(1);

        var fileName =
            $"Daily Attendance Report ({vm.StartDate:yyyy-MM-dd} - {vm.EndDate:yyyy-MM-dd}).xlsx";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    // ------------------------------------------------------------------
    private async Task<DailyReportViewModel> BuildAsync(
        string? startRaw, string? endRaw, string? bu, int? userId, bool includeOff)
    {
        var today = PhTime.Today;
        var defaultStart = today.AddDays(-13); // last 14 days inclusive

        var start = ParseDate(startRaw) ?? defaultStart;
        var end = ParseDate(endRaw) ?? today;
        if (end < start) (start, end) = (end, start);

        var visible = await GetVisibleUsersAsync();

        // Apply UI filters
        var filtered = visible;
        if (!string.IsNullOrWhiteSpace(bu))
        {
            filtered = filtered.Where(u => u.BusinessUnit == bu);
        }
        if (userId is int uid)
        {
            filtered = filtered.Where(u => u.Id == uid);
        }

        var users = await filtered
            .OrderBy(u => u.BusinessUnit)
            .ThenBy(u => u.FullName)
            .Include(u => u.ScheduleEntries)
            .ToListAsync();

        var userCountryById = users.ToDictionary(u => u.Id, HolidayHelper.CountryFor);
        var countries = userCountryById.Values.Distinct().ToList();

        var userIds = users.Select(u => u.Id).ToList();

        // Holidays overlapping the report window for all visible countries
        // plus global (ALL) rows.
        var holidayRows = await Db.Holidays
            .Where(h => h.Date >= start
                        && h.Date <= end
                        && (h.Country == "ALL" || countries.Contains(h.Country)))
            .ToListAsync();
        var holidayAllDates = holidayRows
            .Where(h => h.Country == "ALL")
            .Select(h => h.Date)
            .ToHashSet();
        var holidayDatesByCountry = holidayRows
            .Where(h => h.Country != "ALL")
            .GroupBy(h => h.Country)
            .ToDictionary(
                g => g.Key,
                g => g.Select(h => h.Date).ToHashSet());

        var attendances = await Db.Attendances
            .Where(a => userIds.Contains(a.UserId)
                        && a.WorkDate >= start
                        && a.WorkDate <= end)
            .ToListAsync();

        var byUserDate = attendances
            .GroupBy(a => (a.UserId, a.WorkDate))
            .ToDictionary(g => g.Key, AggregateDailyAttendance);

        // Violation counts per (user, date) from the notification audit log.
        var startUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtc = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var notifs = await Db.NotificationLogs
            .Where(n => n.UserId != null
                        && userIds.Contains(n.UserId.Value)
                        && n.SentAt >= startUtc
                        && n.SentAt < endUtc
                        && (n.Level == "Warning" || n.Level == "Deduction")
                        && n.Status == "Sent")
            .Select(n => new { n.UserId, n.SentAt, n.OfflineMinutes })
            .ToListAsync();

        // Bucket by user + PH calendar date.
        var violationsByUserDate = notifs
            .GroupBy(n =>
            {
                var phDate = DateOnly.FromDateTime(
                    PhTime.ToPh(DateTime.SpecifyKind(n.SentAt, DateTimeKind.Utc))!.Value.DateTime);
                return (n.UserId!.Value, phDate);
            })
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var bucket = new DailyNotificationBucket
                    {
                        Count = g.Count(),
                    };
                    foreach (var item in g.OrderBy(x => x.SentAt))
                    {
                        var stamp = PhTime.Format(item.SentAt, "HH:mm");
                        var marker = $"{stamp} ({item.OfflineMinutes} min)";
                        if (item.OfflineMinutes > 60)
                        {
                            bucket.Over60.Add(marker);
                        }
                        else if (item.OfflineMinutes >= 30)
                        {
                            bucket.From30To60.Add(marker);
                        }
                    }
                    return bucket;
                });

        // Approved leave requests overlapping the report window. We expand
        // each one into a per-day lookup so the Remarks column can carry
        // the leave type / hours on every covered calendar date.
        var startDt = start.ToDateTime(TimeOnly.MinValue);
        var endDt = end.ToDateTime(TimeOnly.MaxValue);
        var approvedLeaves = await Db.LeaveRequests
            .Where(l => userIds.Contains(l.UserId)
                        && l.Status == "Approved"
                        && l.StartDate <= endDt
                        && l.EndDate >= startDt)
            .ToListAsync();
        var leavesByUserDate = new Dictionary<(int UserId, DateOnly Date), LeaveRequest>();
        foreach (var lv in approvedLeaves)
        {
            var lvStart = DateOnly.FromDateTime(lv.StartDate);
            var lvEnd = DateOnly.FromDateTime(lv.EndDate);
            if (lvStart < start) lvStart = start;
            if (lvEnd > end) lvEnd = end;
            for (var d = lvStart; d <= lvEnd; d = d.AddDays(1))
            {
                leavesByUserDate[(lv.UserId, d)] = lv;
            }
        }

        var rows = new List<DailyReportRow>();
        foreach (var u in users)
        {
            var scheduleByDate = u.ScheduleEntries
                .Where(s => s.WorkDate >= start && s.WorkDate <= end)
                .ToDictionary(s => s.WorkDate);

            for (var d = start; d <= end; d = d.AddDays(1))
            {
                scheduleByDate.TryGetValue(d, out var sched);

                var isWorkingDay = sched?.IsWorking ?? false;
                var isHoliday = holidayAllDates.Contains(d)
                    || (userCountryById.TryGetValue(u.Id, out var cc)
                        && holidayDatesByCountry.TryGetValue(cc, out var dates)
                        && dates.Contains(d));
                var att = byUserDate.TryGetValue((u.Id, d), out var a) ? a : null;
                var hasApprovedLeave = leavesByUserDate.TryGetValue((u.Id, d), out var leaveOnDay);

                // Skip non-working days unless the operator opted in or
                // there is actually attendance recorded for that day.
                if (!isWorkingDay && att is null && !includeOff) continue;

                violationsByUserDate.TryGetValue((u.Id, d), out var notifBucket);

                var hoursRendered = att is null
                    ? 0.0
                    : Math.Round(att.TotalMinutes / 60.0, 2);
                var scheduledHours = sched?.Hours ?? 0;

                string status;
                if (hasApprovedLeave)
                {
                    status = "Onleave";
                }
                else if (isWorkingDay && isHoliday)
                {
                    status = "Holiday";
                }
                else if (att is null)
                {
                    if (!isWorkingDay)
                    {
                        status = string.Empty;
                    }
                    else
                    {
                        var todayForUser = UserClock.TodayFor(u);
                        if (d < todayForUser)
                        {
                            status = "Absent";
                        }
                        else if (d > todayForUser)
                        {
                            status = string.Empty;
                        }
                        else if (sched?.StartTime is not TimeOnly startTime)
                        {
                            status = string.Empty;
                        }
                        else
                        {
                            var grace = LateCheck.GraceMinutes(u, sched.EffectiveWorkType);
                            var absentAfter = d.ToDateTime(startTime).AddMinutes(grace);
                            status = UserClock.NowFor(u).DateTime >= absentAfter
                                ? "Absent"
                                : string.Empty;
                        }
                    }
                }
                else if (att.HasOpen)
                {
                    status = "Incomplete";
                }
                else if (isWorkingDay && hoursRendered + 0.01 < scheduledHours)
                {
                    status = "Incomplete Hours";
                }
                else
                {
                    status = "Complete";
                }

                var workType = sched?.EffectiveWorkType ?? (isWorkingDay ? "Onsite" : "Dayoff");
                if (hasApprovedLeave)
                {
                    workType = "Onleave";
                }
                else if (isWorkingDay && isHoliday)
                {
                    workType = "Holiday";
                }

                var suppressLate = hasApprovedLeave || (isWorkingDay && isHoliday);

                rows.Add(new DailyReportRow
                {
                    Date = d,
                    Key = string.IsNullOrWhiteSpace(u.EmployeeId)
                        ? $"{u.Username}_{d:yyyy-MM-dd}"
                        : $"{u.EmployeeId}_{d:yyyy-MM-dd}",
                    EmployeeName = u.FullName,
                    EmployeeId = u.EmployeeId ?? string.Empty,
                    BusinessUnit = u.BusinessUnit ?? string.Empty,
                    Role = u.Role,
                    StartTime = att?.CheckIn,
                    EndTime = att?.CheckOut,
                    StartTimeDisplay = att is null
                        ? string.Empty
                        : UserClock.Format(u, att.CheckIn, "HH:mm"),
                    EndTimeDisplay = att?.CheckOut is null
                        ? string.Empty
                        : UserClock.Format(u, att.CheckOut, "HH:mm"),
                    TimeZoneLabel = UserClock.Label(u),
                    HoursRendered = hoursRendered,
                    ScheduledHours = scheduledHours,
                    AttendanceStatus = status,
                    WorkType = workType,
                    LateStatus = suppressLate
                        ? string.Empty
                        : BuildLateLabel(u, sched, att?.CheckIn, isHoliday: isHoliday),
                    LateMinutes = att is null
                        ? 0
                        : suppressLate
                            ? 0
                            : LateCheck.Evaluate(u, sched, att.CheckIn, isHoliday: isHoliday).LateMinutes,
                    EmailNotifications = notifBucket?.Count ?? 0,
                    Notification30To60Details = notifBucket is null
                        ? string.Empty
                        : string.Join("\n", notifBucket.From30To60),
                    NotificationOver60Details = notifBucket is null
                        ? string.Empty
                        : string.Join("\n", notifBucket.Over60),
                    TimeDifference = Math.Round(hoursRendered - scheduledHours, 2),
                    Remarks = hasApprovedLeave
                        ? $"Approved leave: {leaveOnDay?.LeaveType} ({leaveOnDay?.HoursPerDay}h)"
                        : string.Empty,
                });
            }
        }

        var allBus = await visible
            .Where(u => u.BusinessUnit != null && u.BusinessUnit != "")
            .Select(u => u.BusinessUnit!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();

        var allUsers = await visible
            .OrderBy(u => u.FullName)
            .ToListAsync();

        return new DailyReportViewModel
        {
            StartDate = start,
            EndDate = end,
            BusinessUnitFilter = bu,
            UserIdFilter = userId,
            IncludeNonWorkingDays = includeOff,
            Rows = rows,
            Users = allUsers,
            BusinessUnits = allBus,
        };
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    private static DailyAttendanceAggregate AggregateDailyAttendance(
        IGrouping<(int UserId, DateOnly WorkDate), Attendance> group)
    {
        var rows = group
            .OrderBy(a => a.CheckIn)
            .ToList();

        var hasOpen = rows.Any(a => a.IsOpen);
        var latestClosedCheckOut = rows
            .Where(a => a.CheckOut is not null)
            .Select(a => a.CheckOut!.Value)
            .DefaultIfEmpty()
            .Max();

        return new DailyAttendanceAggregate
        {
            CheckIn = rows[0].CheckIn,
            CheckOut = hasOpen ? null : latestClosedCheckOut,
            HasOpen = hasOpen,
            TotalMinutes = rows.Sum(a => a.DurationMinutes),
        };
    }

    /// <summary>
    /// Build the human-readable late label for the Daily Report cell.
    /// Returns "" when not applicable (no check-in, Dayoff, Support),
    /// "On time" when within the grace window, or "Late HH:MM" when over.
    /// </summary>
    private static string BuildLateLabel(User user, ScheduleEntry? sched, DateTime? checkInUtc, bool isHoliday = false)
    {
        if (checkInUtc is null) return string.Empty;
        var r = LateCheck.Evaluate(user, sched, checkInUtc.Value, isHoliday: isHoliday);
        if (r.Status == LateCheck.LateStatus.NotApplicable) return string.Empty;
        if (r.Status == LateCheck.LateStatus.OnTime) return "On time";
        var h = r.LateMinutes / 60;
        var m = r.LateMinutes % 60;
        return h > 0 ? $"Late {h:D2}:{m:D2}" : $"Late {m} min";
    }

    private sealed class DailyAttendanceAggregate
    {
        public DateTime CheckIn { get; set; }
        public DateTime? CheckOut { get; set; }
        public bool HasOpen { get; set; }
        public int TotalMinutes { get; set; }
    }

    private sealed class DailyNotificationBucket
    {
        public int Count { get; set; }
        public List<string> From30To60 { get; } = new();
        public List<string> Over60 { get; } = new();
    }

    // ==================================================================
    // Weekly view (folded in from the old WeeklyController so admins/PMs
    // can flip between Daily and Weekly under a single Reports area).
    // ==================================================================

    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly(
        [FromQuery(Name = "week")] string? weekRaw,
        [FromQuery(Name = "weeks")] int weeks = 1,
        [FromQuery(Name = "user_id")] int? userId = null,
        [FromQuery(Name = "present_only")] bool presentOnly = false)
    {
        var vm = await BuildWeeklyAsync(weekRaw, weeks, userId, presentOnly);
        return View("Weekly", vm);
    }

    [HttpGet("weekly.xlsx")]
    public async Task<IActionResult> WeeklyXlsx(
        [FromQuery(Name = "week")] string? weekRaw,
        [FromQuery(Name = "weeks")] int weeks = 1,
        [FromQuery(Name = "user_id")] int? userId = null,
        [FromQuery(Name = "present_only")] bool presentOnly = false)
    {
        var vm = await BuildWeeklyAsync(weekRaw, weeks, userId, presentOnly);
        var totalDays = vm.WeeksSpan * 7;

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet($"Week {vm.WeekStart:yyyy-MM-dd}");

        ws.Cell(1, 1).Value =
            $"Weekly attendance - {vm.WeekStart:MMM d} - {vm.WeekStart.AddDays(totalDays - 1):MMM d, yyyy}";
        ws.Range(1, 1, 1, totalDays + 5).Merge().Style.Font.Bold = true;

        var shortDays = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        var headers = new List<string> { "Employee ID", "Username", "Full name" };
        for (var i = 0; i < totalDays; i++)
        {
            var d = vm.WeekStart.AddDays(i);
            headers.Add($"{shortDays[i % 7]} {d:MMM d}");
        }
        headers.Add("Days present");
        headers.Add("Total hours");

        for (var i = 0; i < headers.Count; i++)
        {
            ws.Cell(2, i + 1).Value = headers[i];
        }
        var headerRow = ws.Row(2);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E50A2");
        headerRow.Style.Font.FontColor = XLColor.White;

        var r = 3;
        foreach (var row in vm.Rows)
        {
            var u = row.User;
            ws.Cell(r, 1).Value = u.EmployeeId ?? string.Empty;
            ws.Cell(r, 2).Value = u.Username;
            ws.Cell(r, 3).Value = u.FullName;

            for (var i = 0; i < totalDays; i++)
            {
                var att = row.Days[i];
                if (att is null)
                {
                    ws.Cell(r, 4 + i).Value = string.Empty;
                }
                else if (att.IsOpen)
                {
                    ws.Cell(r, 4 + i).Value =
                        $"{UserClock.Format(u, att.CheckIn, "HH:mm")} {UserClock.Label(u)} (open)";
                }
                else
                {
                    ws.Cell(r, 4 + i).Value =
                        $"{UserClock.Format(u, att.CheckIn, "HH:mm")}-{UserClock.Format(u, att.CheckOut, "HH:mm")} " +
                        $"{UserClock.Label(u)} ({att.DurationMinutes}m)";
                }
            }

            ws.Cell(r, 4 + totalDays).Value = row.PresentDays;
            ws.Cell(r, 5 + totalDays).Value = Math.Round(row.TotalMinutes / 60.0, 2);
            r++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(2);

        var fileName = vm.WeeksSpan == 2
            ? $"Weekly Attendance ({vm.WeekStart:yyyy-MM-dd} - {vm.WeekStart.AddDays(13):yyyy-MM-dd}).xlsx"
            : $"Weekly Attendance ({vm.WeekStart:yyyy-MM-dd}).xlsx";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private async Task<WeeklyAttendanceViewModel> BuildWeeklyAsync(
        string? weekRaw, int weeks, int? userId, bool presentOnly)
    {
        var today = PhTime.Today;

        // Clamp the span to 1 or 2 weeks (anything else falls back to 1).
        var span = weeks == 2 ? 2 : 1;
        var totalDays = span * 7;

        DateOnly weekRef = today;
        if (!string.IsNullOrWhiteSpace(weekRaw)
            && DateOnly.TryParseExact(weekRaw.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            weekRef = parsed;
        }

        static DateOnly StartOfWeek(DateOnly d)
        {
            var dow = ((int)d.DayOfWeek + 6) % 7; // Mon = 0 ... Sun = 6
            return d.AddDays(-dow);
        }

        var weekStart = StartOfWeek(weekRef);
        var rangeEnd = weekStart.AddDays(totalDays - 1);
        var currentWeekStart = StartOfWeek(today);

        // Users - apply the viewer's Business-Unit scope, then user filter.
        var scopedUsers = await GetVisibleUsersAsync();
        var usersQ = scopedUsers;
        if (userId is int uid)
        {
            usersQ = usersQ.Where(u => u.Id == uid);
        }
        var users = await usersQ.OrderBy(u => u.FullName).ToListAsync();
        var allUsers = await scopedUsers.OrderBy(u => u.FullName).ToListAsync();

        var rangeAttendances = await Db.Attendances
            .Where(a => a.WorkDate >= weekStart && a.WorkDate <= rangeEnd)
            .ToListAsync();

        var byUserDay = rangeAttendances
            .GroupBy(a => (a.UserId, a.WorkDate))
            .ToDictionary(
                g => g.Key,
                g => new WeeklyAttendanceAggregate
                {
                    Display = BuildWeeklyDisplayAttendance(g),
                    TotalMinutes = g.Sum(a => a.DurationMinutes),
                });

        var rows = new List<WeeklyAttendanceRow>(users.Count);
        var teamPresent = 0;
        var teamMinutes = 0;
        foreach (var u in users)
        {
            var row = new WeeklyAttendanceRow { Days = new Attendance?[totalDays], User = u };
            for (var i = 0; i < totalDays; i++)
            {
                var date = weekStart.AddDays(i);
                if (byUserDay.TryGetValue((u.Id, date), out var day))
                {
                    row.Days[i] = day.Display;
                    row.PresentDays++;
                    row.TotalMinutes += day.TotalMinutes;
                }
            }
            if (presentOnly && row.PresentDays == 0) continue;

            teamPresent += row.PresentDays;
            teamMinutes += row.TotalMinutes;
            rows.Add(row);
        }

        return new WeeklyAttendanceViewModel
        {
            WeekStart = weekStart,
            WeeksSpan = span,
            CurrentWeekStart = currentWeekStart,
            PrevWeekStart = weekStart.AddDays(-totalDays),
            NextWeekStart = weekStart.AddDays(totalDays),
            IsCurrentWeek = weekStart == currentWeekStart,
            Rows = rows,
            AllUsers = allUsers,
            FilterUserId = userId,
            PresentOnly = presentOnly,
            TeamPresentDays = teamPresent,
            TeamTotalMinutes = teamMinutes,
        };
    }

    private static Attendance BuildWeeklyDisplayAttendance(IEnumerable<Attendance> rows)
    {
        var ordered = rows.OrderBy(a => a.CheckIn).ToList();
        var first = ordered[0];
        var hasOpen = ordered.Any(a => a.IsOpen);
        var lastClosedCheckOut = ordered
            .Where(a => a.CheckOut is not null)
            .Select(a => a.CheckOut!.Value)
            .DefaultIfEmpty(first.CheckIn)
            .Max();

        return new Attendance
        {
            UserId = first.UserId,
            WorkDate = first.WorkDate,
            CheckIn = first.CheckIn,
            CheckOut = hasOpen ? null : lastClosedCheckOut,
        };
    }

    private sealed class WeeklyAttendanceAggregate
    {
        public Attendance Display { get; set; } = null!;
        public int TotalMinutes { get; set; }
    }
}
