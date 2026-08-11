using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AttendanceMonitoring.Controllers;

[Authorize]
public class DashboardController : AppController
{
    private readonly int _onlineThreshold;

    public DashboardController(AppDbContext db, IConfiguration config) : base(db)
    {
        _onlineThreshold = config.GetValue(
            "AttendanceMonitoring:OnlineThresholdSeconds",
            Models.Constants.DefaultOnlineThresholdSeconds);
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        return IsAdmin || IsOperations
            ? await AdminViewAsync(me)
            : await EmployeeViewAsync(me);
    }

    // ------------------------------------------------------------------
    // Employee submits a break / lunch quota-reset request from the
    // dashboard panel. Lives on DashboardController (conventional
    // routing /Dashboard/RequestQuotaReset) so the form has no chance
    // of getting intercepted by anything on the /quota-reset/* path
    // (edge cache, proxy, etc.). The actual approval queue stays in
    // QuotaResetController.
    // ------------------------------------------------------------------
    [HttpGet]
    public IActionResult RequestQuotaReset()
        => RedirectToAction(nameof(Index));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestQuotaReset(
        [FromForm] string? kind,
        [FromForm] string? reason)
    {
        if (IsOperations) return Forbid();

        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        // Pure admins don't run a tracked shift, so they have no quota to reset.
        if (string.Equals(me.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("Admins don't have a break/lunch quota to reset.", "warning");
            return RedirectToAction(nameof(Index));
        }

        var normalised = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised != "break" && normalised != "lunch")
        {
            TempData.Flash("Invalid quota kind \u2014 must be \"break\" or \"lunch\".", "danger");
            return RedirectToAction(nameof(Index));
        }

        var today = PhTime.Today;

        var dup = await Db.QuotaResetRequests.AnyAsync(r =>
            r.UserId == me.Id
            && r.Kind == normalised
            && r.TargetDate == today
            && r.Status == "Pending");
        if (dup)
        {
            TempData.Flash(
                $"You already have a pending {normalised} reset request for today.",
                "info");
            return RedirectToAction(nameof(Index));
        }

        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length > 500) trimmedReason = trimmedReason[..500];

        Db.QuotaResetRequests.Add(new QuotaResetRequest
        {
            UserId = me.Id,
            RequestedByUserId = me.Id,
            RequestedAt = DateTime.UtcNow,
            Kind = normalised,
            TargetDate = today,
            Reason = trimmedReason,
            Status = "Pending",
        });
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Your {normalised} reset request was submitted \u2014 awaiting approval.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> AdminViewAsync(User me)
    {
        var today = PhTime.Today;

        var visibleQ = await GetVisibleUsersAsync();
        var users = await visibleQ
            .OrderBy(u => u.FullName)
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();
        var yesterday = today.AddDays(-1);
        var todaysRows = await Db.Attendances
            .Where(a => userIds.Contains(a.UserId)
                        && (a.WorkDate == today
                            || (a.WorkDate == yesterday && a.CheckOut == null)))
            .OrderByDescending(a => a.CheckIn)
            .ToListAsync();
        var todays = todaysRows
            // Data can have accidental duplicates per user/day; pick the most
            // actionable row (open row first, else latest check-in).
            .GroupBy(a => a.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.IsOpen)
                      .ThenByDescending(x => x.CheckIn)
                      .First());

        var relevantScheduleDates = new[] { yesterday, today };
        var todaysScheduleRows = await Db.ScheduleEntries
            .Where(s => relevantScheduleDates.Contains(s.WorkDate)
                        && userIds.Contains(s.UserId))
            .OrderByDescending(s => s.Id)
            .ToListAsync();
        var todaysSchedule = todaysScheduleRows
            .GroupBy(s => (s.UserId, s.WorkDate))
            .ToDictionary(g => g.Key, g => g.First());
        var todayHolidayRows = await Db.Holidays
            .Where(h => relevantScheduleDates.Contains(h.Date))
            .ToListAsync();

        var rows = new List<TeamRowViewModel>();
        var online = 0;
        var lunch = 0;
        var present = 0;

        foreach (var u in users)
        {
            todays.TryGetValue(u.Id, out var att);
            var state = u.EffectiveDashboardState(att, _onlineThreshold);
            var isOnline = state != "offline";
            if (isOnline) online++;
            if (state == "lunch") lunch++;
            var activeWorkDate = att?.WorkDate ?? today;
            todaysSchedule.TryGetValue((u.Id, activeWorkDate), out var sched);
            var isHoliday = IsHolidayForUser(
                u, todayHolidayRows.Where(h => h.Date == activeWorkDate));
            if (isHoliday)
            {
                sched = new ScheduleEntry
                {
                    UserId = u.Id,
                    WorkDate = activeWorkDate,
                    WorkType = "Holiday",
                    IsWorking = false,
                };
            }
            if (att is not null) present++;

            // Evaluate the late-arrival policy once for the team row so the
            // dashboard can show a badge next to today's clock-in time.
            LateCheck.Result? late = att is null
                ? null
                : LateCheck.Evaluate(u, sched, att.CheckIn, isHoliday: isHoliday);

            rows.Add(new TeamRowViewModel
            {
                User = u,
                Online = isOnline,
                State = state,
                Attendance = att,
                TodaySchedule = sched,
                Late = late,
            });
        }

        todays.TryGetValue(me.Id, out var myAtt);

        // Show the Check-out button as long as the user has any open record,
        // even if it was opened on a previous day (forgot to clock out).
        if (myAtt is null || !myAtt.IsOpen)
        {
            var openCarry = await Db.Attendances
                .Where(a => a.UserId == me.Id && a.CheckOut == null)
                .OrderByDescending(a => a.WorkDate)
                .ThenByDescending(a => a.CheckIn)
                .FirstOrDefaultAsync();
            if (openCarry is not null) myAtt = openCarry;
        }

        var vm = new AdminDashboardViewModel
        {
            Rows = rows,
            OnlineCount = online,
            AwayCount = 0, // Away tier removed; policy is Online unless locked
            LunchCount = lunch,
            OfflineCount = users.Count - online,
            PresentCount = present,
            TotalUsers = users.Count,
            MyAttendance = myAtt,
            OnlineThresholdSeconds = _onlineThreshold,
        };

        // Project Managers AND Program Managers also see the employee-style
        // personal section (own check-in/out, schedule, recent attendance,
        // plus the Break and Start-lunch buttons). Pure admins do not get
        // this block because they aren't on a monitored shift.
        if (string.Equals(me.Role, Models.Roles.Pm, StringComparison.OrdinalIgnoreCase)
            || string.Equals(me.Role, Models.Roles.ProgramManager, StringComparison.OrdinalIgnoreCase))
        {
            var todayIdx = ((int)today.DayOfWeek + 6) % 7; // Mon = 0 … Sun = 6
            var weekStart = today.AddDays(-todayIdx);
            var weekEnd = weekStart.AddDays(6);

            var dbRows = await Db.ScheduleEntries
                .Where(s => s.UserId == me.Id && s.WorkDate >= weekStart && s.WorkDate <= weekEnd)
                .OrderByDescending(s => s.Id)
                .ToListAsync();
            var weekRowsByDate = dbRows
                .GroupBy(s => s.WorkDate)
                .ToDictionary(g => g.Key, g => g.First());
            var weekHolidays = await HolidayHelper.RangeForAsync(Db, me, weekStart, weekEnd);
            var schedule = Enumerable.Range(0, 7)
                .Select(i =>
                {
                    var d = weekStart.AddDays(i);
                    return weekRowsByDate.TryGetValue(d, out var row)
                        ? row
                        : ScheduleEntry.CsiDefaultFor(me.Id, d);
                })
                .ToList();
            ApplyHolidayWorkTypeOverrides(schedule, weekHolidays);

            var recent = await Db.Attendances
                .Where(a => a.UserId == me.Id)
                .OrderByDescending(a => a.WorkDate)
                .Take(7)
                .ToListAsync();

            vm.IsPm = true;
            vm.FirstName = (me.FullName ?? string.Empty).Split(' ', 2)[0];
            vm.Schedule = schedule;
            vm.WeekStart = weekStart;
            vm.TodaySchedule = schedule[todayIdx].IsWorking ? schedule[todayIdx] : null;
            vm.TodayIndex = todayIdx;
            vm.WorkingDays = schedule.Count(s => s.IsWorking);
            vm.WeeklyHours = Math.Round(schedule.Sum(s => s.Hours), 2);
            vm.Recent = recent;
            vm.RecentMinutes = recent.Where(r => r.CheckOut is not null).Sum(r => r.DurationMinutes);
            vm.WeekHolidays = weekHolidays;

            // Late-arrival evaluation for the PM/PgM's own check-in so the
            // Admin dashboard personal section can render the badge the
            // same way the Employee dashboard does. Skipped for closed /
            // missing rows since the badge is only shown while open.
            if (myAtt is not null)
            {
                vm.MyLate = LateCheck.Evaluate(
                    me,
                    vm.TodaySchedule ?? schedule[todayIdx],
                    myAtt.CheckIn);
            }
        }

        return View("Admin", vm);
    }

    private async Task<IActionResult> EmployeeViewAsync(User me)
    {
        var today = PhTime.Today;

        var myAttendance = await Db.Attendances
            .FirstOrDefaultAsync(a => a.UserId == me.Id && a.WorkDate == today);

        // Carry-over: if today has no row (or today's row is already closed)
        // but the user left an older session open, surface that one so the
        // Check-out button stays visible until they close it.
        if (myAttendance is null || !myAttendance.IsOpen)
        {
            var openCarry = await Db.Attendances
                .Where(a => a.UserId == me.Id && a.CheckOut == null)
                .OrderByDescending(a => a.WorkDate)
                .ThenByDescending(a => a.CheckIn)
                .FirstOrDefaultAsync();
            if (openCarry is not null) myAttendance = openCarry;
        }

        // Build this week's seven schedule rows by overlaying any rows
        // the admin has actually authored on top of an empty Mon..Sun
        // strip. Missing dates render as "off".
        var todayIdx = ((int)today.DayOfWeek + 6) % 7; // Mon = 0 … Sun = 6
        var weekStart = today.AddDays(-todayIdx);
        var weekEnd = weekStart.AddDays(6);

        var dbRows = await Db.ScheduleEntries
            .Where(s => s.UserId == me.Id && s.WorkDate >= weekStart && s.WorkDate <= weekEnd)
            .OrderByDescending(s => s.Id)
            .ToListAsync();
        var weekRowsByDate = dbRows
            .GroupBy(s => s.WorkDate)
            .ToDictionary(g => g.Key, g => g.First());
        var weekHolidays = await HolidayHelper.RangeForAsync(Db, me, weekStart, weekEnd);
        var schedule = Enumerable.Range(0, 7)
            .Select(i =>
            {
                var d = weekStart.AddDays(i);
                return weekRowsByDate.TryGetValue(d, out var row)
                    ? row
                    : ScheduleEntry.CsiDefaultFor(me.Id, d);
            })
            .ToList();
        ApplyHolidayWorkTypeOverrides(schedule, weekHolidays);
        var todaySchedule = schedule[todayIdx].IsWorking ? schedule[todayIdx] : null;

        var workingDays = schedule.Count(s => s.IsWorking);
        var weeklyHours = Math.Round(schedule.Sum(s => s.Hours), 2);

        var recent = await Db.Attendances
            .Where(a => a.UserId == me.Id)
            .OrderByDescending(a => a.WorkDate)
            .Take(7)
            .ToListAsync();
        var recentMinutes = recent.Where(r => r.CheckOut is not null).Sum(r => r.DurationMinutes);

        var firstName = (me.FullName ?? string.Empty).Split(' ', 2)[0];

        // Cross-midnight back-to-back: if today's session is closed (or
        // none exists) and the user's NEXT scheduled shift starts within
        // the pre-shift grace window, surface a Check-in button so they
        // can clock in early for the upcoming shift. Skipped for admins
        // (no fixed schedule). Mirrors the policies in AttendanceController
        // (regular 15-min grace, Support 60-min grace). For Support we
        // also consider today's later start (multiple shifts per day),
        // not just tomorrow's first shift.
        DateTime? nextShiftEarlyStart = null;
        if (!string.Equals(me.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase)
            && (myAttendance is null || !myAttendance.IsOpen))
        {
            var graceMinutesBefore = me.IsSupport ? 60 : 15;
            var nowLocal = UserClock.NowFor(me).DateTime;

            DateTime? candidate = null;
            if (me.IsSupport)
            {
                // Today's shift may still be ahead (e.g. closed an earlier
                // session, waiting for the next one). Support users keep
                // bespoke rows so no CSI fallback.
                var schedToday = await DbInitializer.GetScheduleForDateAsync(Db, me, today);
                if (schedToday is { IsWorking: true, StartTime: { } stToday })
                {
                    var todayStart = today.ToDateTime(stToday);
                    if (todayStart > nowLocal) candidate = todayStart;
                }
                if (candidate is null)
                {
                    var tomorrow = today.AddDays(1);
                    var schedTomorrow = await DbInitializer.GetScheduleForDateAsync(Db, me, tomorrow);
                    if (schedTomorrow is { IsWorking: true, StartTime: { } stTom })
                    {
                        candidate = tomorrow.ToDateTime(stTom);
                    }
                }
            }
            else
            {
                // Non-Support: only the cross-midnight tomorrow case is
                // relevant — today's row is one-per-day, so the only
                // possible upcoming start is tomorrow's first shift.
                var tomorrow = today.AddDays(1);
                var schedTomorrow = await DbInitializer.GetEffectiveScheduleForDateAsync(Db, me, tomorrow);
                if (schedTomorrow is { IsWorking: true, StartTime: { } stTom })
                {
                    candidate = tomorrow.ToDateTime(stTom);
                }
            }

            if (candidate is { } nsv)
            {
                var minutesUntil = (nsv - nowLocal).TotalMinutes;
                if (minutesUntil >= 0 && minutesUntil <= graceMinutesBefore)
                {
                    nextShiftEarlyStart = nsv;
                }
            }
        }

        ScheduleEntry? attendanceSchedule = null;
        Holiday? attendanceHoliday = null;
        if (myAttendance is not null)
        {
            attendanceSchedule = await DbInitializer.GetEffectiveScheduleForDateAsync(
                Db, me, myAttendance.WorkDate);
            attendanceHoliday = await HolidayHelper.GetAsync(Db, me, myAttendance.WorkDate);
        }

        var vm = new EmployeeDashboardViewModel
        {
            Me = me,
            Schedule = schedule,
            WeekStart = weekStart,
            TodaySchedule = todaySchedule,
            TodayIndex = todayIdx,
            MyAttendance = myAttendance,
            MyLate = myAttendance is null
                ? null
                : LateCheck.Evaluate(
                    me,
                    attendanceSchedule,
                    myAttendance.CheckIn,
                    isHoliday: attendanceHoliday is not null),
            WorkingDays = workingDays,
            WeeklyHours = weeklyHours,
            Recent = recent,
            RecentMinutes = recentMinutes,
            FirstName = firstName,
            BusinessUnits = me.BusinessUnitList,
            NextShiftEarlyStart = nextShiftEarlyStart,
            WeekHolidays = weekHolidays,
        };

        // Surface today's holiday (if any) so the view can show a banner.
        ViewBag.TodayHoliday = await HolidayHelper.GetAsync(Db, me, today);

        return View("Employee", vm);
    }

    private static bool IsHolidayForUser(User user, IEnumerable<Holiday> rows)
    {
        var country = HolidayHelper.CountryFor(user);
        return rows.Any(h => h.Country == country || h.Country == "ALL");
    }

    private static void ApplyHolidayWorkTypeOverrides(
        List<ScheduleEntry> schedule,
        Dictionary<DateOnly, Holiday> holidaysByDate)
    {
        foreach (var entry in schedule)
        {
            if (!holidaysByDate.ContainsKey(entry.WorkDate)) continue;
            entry.WorkType = "Holiday";
            entry.IsWorking = false;
            entry.StartTime = null;
            entry.EndTime = null;
        }
    }
}
