using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Read-only "My Profile" page — surfaces the signed-in user's own
/// account details (name, email, employee id, business unit, role,
/// manager, last sign-in info) plus quick stats from their recent
/// attendance and weekly schedule. Editable fields stay behind the
/// admin-only Users page; this controller just lets the user *see*
/// what's on record for them and link to Change password.
/// </summary>
[Authorize]
[Route("profile")]
public class ProfileController : AppController
{
    public ProfileController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var me = await Db.Users
            .Include(u => u.Manager)
            .Include(u => u.RoleLabel)
            .FirstOrDefaultAsync(u => u.Id == CurrentUserId);
        if (me is null) return Challenge();

        var today = PhTime.Today;
        var todayIdx = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-todayIdx);
        var weekEnd = weekStart.AddDays(6);

        var dbRows = await Db.ScheduleEntries
            .Where(s => s.UserId == me.Id && s.WorkDate >= weekStart && s.WorkDate <= weekEnd)
            .ToDictionaryAsync(s => s.WorkDate);
        var weekHolidays = await HolidayHelper.RangeForAsync(Db, me, weekStart, weekEnd);
        var schedule = Enumerable.Range(0, 7)
            .Select(i =>
            {
                var d = weekStart.AddDays(i);
                return dbRows.TryGetValue(d, out var row)
                    ? row
                    : ScheduleEntry.CsiDefaultFor(me.Id, d);
            })
            .ToList();
        foreach (var entry in schedule)
        {
            if (!weekHolidays.ContainsKey(entry.WorkDate)) continue;
            entry.WorkType = "Holiday";
            entry.IsWorking = false;
            entry.StartTime = null;
            entry.EndTime = null;
        }
        var workingDays = schedule.Count(s => s.IsWorking);
        var weeklyHours = System.Math.Round(schedule.Sum(s => s.Hours), 2);

        var recent = await Db.Attendances
            .Where(a => a.UserId == me.Id)
            .OrderByDescending(a => a.WorkDate)
            .Take(7)
            .ToListAsync();
        var recentMinutes = recent.Where(r => r.CheckOut is not null).Sum(r => r.DurationMinutes);

        var vm = new ProfileViewModel
        {
            Me = me,
            Schedule = schedule,
            WeekStart = weekStart,
            TodayIndex = todayIdx,
            WorkingDays = workingDays,
            WeeklyHours = weeklyHours,
            Recent = recent,
            RecentMinutes = recentMinutes,
            WeekHolidays = weekHolidays,
        };
        return View(vm);
    }
}
