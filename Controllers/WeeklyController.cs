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
/// Admin / PM weekly attendance view. Mon..Sun grid for every team member
/// with filters (user, week span, present-only) and an Excel export.
/// The grid covers either 1 week (7 days) or 2 weeks (14 days).
/// </summary>
[Authorize(Policy = "AdminOnly")]
[Route("weekly")]
public class WeeklyController : AppController
{
    public WeeklyController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "week")] string? weekRaw,
        [FromQuery(Name = "weeks")] int weeks = 1,
        [FromQuery(Name = "user_id")] int? userId = null,
        [FromQuery(Name = "present_only")] bool presentOnly = false)
    {
        var vm = await BuildAsync(weekRaw, weeks, userId, presentOnly);
        return View(vm);
    }

    [HttpGet("export.xlsx")]
    public async Task<IActionResult> ExportXlsx(
        [FromQuery(Name = "week")] string? weekRaw,
        [FromQuery(Name = "weeks")] int weeks = 1,
        [FromQuery(Name = "user_id")] int? userId = null,
        [FromQuery(Name = "present_only")] bool presentOnly = false)
    {
        var vm = await BuildAsync(weekRaw, weeks, userId, presentOnly);
        var totalDays = vm.WeeksSpan * 7;
        var viewer = await GetCurrentUserAsync();

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet($"Week {vm.WeekStart:yyyy-MM-dd}");

        // Header rows
        ws.Cell(1, 1).Value = $"Weekly attendance - {vm.WeekStart:MMM d} - {vm.WeekStart.AddDays(totalDays - 1):MMM d, yyyy}";
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
                    ws.Cell(r, 4 + i).Value = $"{UserClock.Format(viewer, att.CheckIn, "HH:mm")} (open)";
                }
                else
                {
                    ws.Cell(r, 4 + i).Value =
                        $"{UserClock.Format(viewer, att.CheckIn, "HH:mm")}-{UserClock.Format(viewer, att.CheckOut, "HH:mm")} ({att.DurationMinutes}m)";
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

    // ------------------------------------------------------------------
    // Shared builder
    // ------------------------------------------------------------------
    private async Task<WeeklyAttendanceViewModel> BuildAsync(
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

        // Attendance for the selected range
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

        // Step the prev/next links by the same span the user is viewing.
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

