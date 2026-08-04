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
[Route("schedule")]
public class ScheduleController : AppController
{
    private sealed record BulkScheduleRow(
        int UserId,
        DateOnly WorkDate,
        bool IsWorking,
        TimeOnly? StartTime,
        TimeOnly? EndTime,
        string WorkType,
        string? Note);

    public ScheduleController(AppDbContext db) : base(db) { }

    [HttpGet("bulk-template.csv")]
    public async Task<IActionResult> BulkTemplate([FromQuery(Name = "month")] string? monthRaw)
    {
        if (!IsAdmin) return Forbid();

        var monthStart = ParseMonth(monthRaw) ?? FirstOfMonth(PhTime.Today);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var monthEnd = monthStart.AddDays(daysInMonth - 1);
        var me = await GetCurrentUserAsync();
        var visibleUsers = await (await GetVisibleUsersAsync())
            .Where(u => u.Role != Roles.Admin && u.EmployeeId != null && u.EmployeeId != "")
            .OrderBy(u => me != null && u.Id == me.Id ? 0 : 1)
            .ThenBy(u => u.FullName)
            .ToListAsync();

        var userIds = visibleUsers.Select(u => u.Id).ToList();
        var existing = userIds.Count == 0
            ? new List<ScheduleEntry>()
            : await Db.ScheduleEntries
                .Where(s => userIds.Contains(s.UserId)
                            && s.WorkDate >= monthStart
                            && s.WorkDate <= monthEnd)
                .ToListAsync();
        var existingByKey = existing
            .GroupBy(s => (s.UserId, s.WorkDate))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First());

        var sb = new StringBuilder();
        sb.AppendLine("EmployeeId,FullName,Role,WorkDate,Day,IsWorking,StartTime,EndTime,WorkType,Notes");
        if (visibleUsers.Count == 0)
        {
            sb.AppendLine("E000001,Sample Employee,employee," + monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",Mon,true,09:00,18:00,Onsite,Regular shift");
            sb.AppendLine("E000001,Sample Employee,employee," + monthStart.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",Tue,false,,,Dayoff,Rest day");
        }
        else
        {
            foreach (var u in visibleUsers)
            {
                for (var i = 0; i < daysInMonth; i++)
                {
                    var workDate = monthStart.AddDays(i);
                    existingByKey.TryGetValue((u.Id, workDate), out var entry);

                    var isWeekend = workDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                    var workType = entry?.WorkType
                                   ?? (isWeekend ? "Dayoff" : "Onsite");
                    var isWorking = entry?.IsWorking
                                    ?? !ScheduleEntry.IsNonWorkingType(workType);
                    var start = isWorking
                        ? (entry?.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00")
                        : string.Empty;
                    var end = isWorking
                        ? (entry?.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "18:00")
                        : string.Empty;
                    var note = entry?.Note
                               ?? (isWorking ? "Regular shift" : "Rest day");

                    sb.AppendLine(
                        string.Join(",",
                            CsvField(u.EmployeeId ?? string.Empty),
                            CsvField(u.FullName),
                            CsvField(u.Role),
                            workDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            workDate.ToString("ddd", CultureInfo.InvariantCulture),
                            isWorking ? "true" : "false",
                            start,
                            end,
                            workType,
                            CsvField(note)));
                }
            }
        }

        var csv = sb.ToString();
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv", $"schedule_bulk_template_{monthStart:yyyy_MM}.csv");
    }

    [HttpGet("schedule-template.xlsx")]
    [HttpGet("schedule-template-v2.xlsx")]
    public async Task<IActionResult> ScheduleTemplate([FromQuery(Name = "month")] string? monthRaw)
    {
        if (!IsAdmin) return Forbid();

        var monthStart = ParseMonth(monthRaw) ?? FirstOfMonth(PhTime.Today);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var monthEnd = monthStart.AddDays(daysInMonth - 1);
        var me = await GetCurrentUserAsync();
        var visibleUsers = await (await GetVisibleUsersAsync())
            .Where(u => u.Role != Roles.Admin && u.EmployeeId != null && u.EmployeeId != "")
            .OrderBy(u => me != null && u.Id == me.Id ? 0 : 1)
            .ThenBy(u => u.FullName)
            .ToListAsync();

        var userIds = visibleUsers.Select(u => u.Id).ToList();
        var existing = userIds.Count == 0
            ? new List<ScheduleEntry>()
            : await Db.ScheduleEntries
                .Where(s => userIds.Contains(s.UserId)
                            && s.WorkDate >= monthStart
                            && s.WorkDate <= monthEnd)
                .ToListAsync();
        var existingByKey = existing
            .GroupBy(s => (s.UserId, s.WorkDate))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First());

        using var wb = new XLWorkbook();

        var calendar = wb.Worksheets.Add("Schedule Template");
        var totalCols = 2 + daysInMonth;
        calendar.Cell(1, 1).Value = $"Schedule Template - {monthStart:MMMM yyyy}";
        calendar.Range(1, 1, 1, totalCols).Merge();
        calendar.Range(1, 1, 1, totalCols).Style.Font.SetBold();
        calendar.Range(1, 1, 1, totalCols).Style.Font.FontSize = 16;
        calendar.Range(1, 1, 1, totalCols).Style.Fill.BackgroundColor = XLColor.FromHtml("#123B5D");
        calendar.Range(1, 1, 1, totalCols).Style.Font.FontColor = XLColor.White;
        calendar.Range(1, 1, 1, totalCols).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        calendar.Range(1, 1, 1, totalCols).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        calendar.Cell(2, 1).Value = "Employee ID";
        calendar.Cell(2, 2).Value = "Employee Name";

        for (var i = 0; i < daysInMonth; i++)
        {
            var d = monthStart.AddDays(i);
            var col = 3 + i;
            calendar.Cell(2, col).Value = d.Day;
            calendar.Cell(3, col).Value = d.ToString("ddd", CultureInfo.InvariantCulture);
            calendar.Cell(2, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            calendar.Cell(3, col).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                calendar.Range(2, col, Math.Max(4, visibleUsers.Count + 3), col)
                    .Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
            }
        }

        calendar.Range(2, 1, 3, totalCols).Style.Font.SetBold();
        calendar.Range(2, 1, 3, totalCols).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F1F8");
        calendar.Range(2, 1, 3, totalCols).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

        calendar.Column(1).Width = 14;
        calendar.Column(2).Width = 24;
        for (var col = 3; col <= totalCols; col++)
        {
            calendar.Column(col).Width = 12;
        }

        var row = 4;
        if (visibleUsers.Count == 0)
        {
            calendar.Cell(row, 1).Value = "E000001";
            calendar.Cell(row, 2).Value = "Sample Employee";
        }
        else
        {
            foreach (var u in visibleUsers)
            {
                calendar.Cell(row, 1).Value = u.EmployeeId;
                calendar.Cell(row, 2).Value = u.FullName;

                for (var i = 0; i < daysInMonth; i++)
                {
                    var workDate = monthStart.AddDays(i);
                    var col = 3 + i;
                    existingByKey.TryGetValue((u.Id, workDate), out var entry);

                    var isWeekend = workDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                    var workType = entry?.WorkType ?? (isWeekend ? "Dayoff" : "Onsite");
                    var isWorking = entry?.IsWorking ?? !ScheduleEntry.IsNonWorkingType(workType);
                    var start = isWorking
                        ? (entry?.StartTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00")
                        : string.Empty;
                    var end = isWorking
                        ? (entry?.EndTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "18:00")
                        : string.Empty;

                    var text = isWorking ? $"{start}-{end}\n{workType}" : workType;
                    var cell = calendar.Cell(row, col);
                    cell.Value = text;
                    cell.Style.Alignment.WrapText = true;
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    if (isWorking)
                    {
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E7F7EE");
                    }
                    else
                    {
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4E6");
                    }
                }

                row++;
            }
        }

        calendar.SheetView.FreezeRows(3);
        calendar.SheetView.FreezeColumns(2);
        calendar.Range(2, 1, Math.Max(4, row - 1), totalCols).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        calendar.Range(2, 1, Math.Max(4, row - 1), totalCols).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        calendar.Rows().AdjustToContents();

        using var stream = new MemoryStream();
        wb.SaveAs(stream);

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        return File(stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Schedule_Template_{monthStart:yyyy_MM}_v2.xlsx");
    }

    [HttpPost("bulk-upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkUpload(
        IFormFile? file,
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "month")] string? monthRaw,
        [FromQuery(Name = "week")] int? weekRaw)
    {
        if (!IsAdmin) return Forbid();
        if (file is null || file.Length == 0)
        {
            TempData.Flash("Please choose a CSV file to upload.", "danger");
            return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
        }

        var visibleUsers = await (await GetVisibleUsersAsync())
            .Where(u => u.Role != Roles.Admin && u.EmployeeId != null && u.EmployeeId != "")
            .ToListAsync();
        var visibleByEmployeeId = visibleUsers
            .GroupBy(u => (u.EmployeeId ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var monthStart = ParseMonth(monthRaw) ?? FirstOfMonth(PhTime.Today);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var errors = new List<string>();
        var parsed = new Dictionary<(int UserId, DateOnly WorkDate), BulkScheduleRow>();
        var ext = (Path.GetExtension(file.FileName) ?? string.Empty).ToLowerInvariant();
        if (ext == ".xlsx")
        {
            using var stream = file.OpenReadStream();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault(w =>
                         w.Name.Equals("Schedule Template", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheet(1);

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow < 4 || lastCol < 3)
            {
                TempData.Flash("Uploaded Excel template has no schedule rows.", "danger");
                return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
            }

            var dayByCol = new Dictionary<int, DateOnly>();
            for (var col = 3; col <= lastCol; col++)
            {
                var dayRaw = ws.Cell(2, col).GetValue<string>().Trim();
                if (!int.TryParse(dayRaw, out var day) || day < 1 || day > daysInMonth) continue;
                dayByCol[col] = new DateOnly(monthStart.Year, monthStart.Month, day);
            }

            if (dayByCol.Count == 0)
            {
                TempData.Flash("Uploaded Excel template is missing calendar day headers.", "danger");
                return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
            }

            for (var row = 4; row <= lastRow; row++)
            {
                var empId = ws.Cell(row, 1).GetValue<string>().Trim();
                if (string.IsNullOrWhiteSpace(empId)) continue;

                if (!visibleByEmployeeId.TryGetValue(empId, out var user))
                {
                    errors.Add($"Row {row}: EmployeeId '{empId}' is unknown or out of your scope.");
                    if (errors.Count >= 25) break;
                    continue;
                }

                foreach (var (col, workDate) in dayByCol)
                {
                    var rawCell = ws.Cell(row, col).GetValue<string>().Trim();
                    if (string.IsNullOrWhiteSpace(rawCell)) continue;

                    if (!TryParseCalendarCell(rawCell,
                            out var isWorking,
                            out var start,
                            out var end,
                            out var workType,
                            out var note))
                    {
                        errors.Add($"Row {row}, day {workDate.Day}: invalid cell format '{rawCell}'. Use '09:00-18:00\\nOnsite' or 'Dayoff'.");
                        if (errors.Count >= 25) break;
                        continue;
                    }

                    if (isWorking && (start is null || end is null))
                    {
                        errors.Add($"Row {row}, day {workDate.Day}: StartTime and EndTime are required for working shifts.");
                        if (errors.Count >= 25) break;
                        continue;
                    }

                    if (isWorking && !IsRoundTheClockScheduleUser(user)
                        && start is not null && end is not null && end.Value <= start.Value)
                    {
                        errors.Add($"Row {row}, day {workDate.Day}: EndTime must be after StartTime for {user.FullName}.");
                        if (errors.Count >= 25) break;
                        continue;
                    }

                    parsed[(user.Id, workDate)] = new BulkScheduleRow(
                        user.Id,
                        workDate,
                        isWorking,
                        isWorking ? start : null,
                        isWorking ? end : null,
                        workType,
                        note);
                }

                if (errors.Count >= 25)
                {
                    continue;
                }
            }
        }
        else
        {
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            var headerLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                TempData.Flash("Uploaded CSV is empty.", "danger");
                return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
            }

            var headerCells = ParseCsvLine(headerLine);
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headerCells.Count; i++)
            {
                var key = (headerCells[i] ?? string.Empty).Trim();
                if (key.Length == 0) continue;
                index[key] = i;
            }

            if (!index.ContainsKey("EmployeeId") || !index.ContainsKey("WorkDate"))
            {
                TempData.Flash("CSV must include EmployeeId and WorkDate columns.", "danger");
                return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
            }

            var lineNumber = 1;
            while (!reader.EndOfStream)
            {
                var rawLine = await reader.ReadLineAsync();
                lineNumber++;
                if (string.IsNullOrWhiteSpace(rawLine)) continue;

                var cells = ParseCsvLine(rawLine);

                string Value(string key)
                {
                    if (!index.TryGetValue(key, out var idx)) return string.Empty;
                    return idx < cells.Count ? (cells[idx] ?? string.Empty).Trim() : string.Empty;
                }

                var empId = Value("EmployeeId");
                var workDateRaw = Value("WorkDate");
                var isWorkingRaw = Value("IsWorking");
                var startRaw = Value("StartTime");
                var endRaw = Value("EndTime");
                var workTypeRaw = Value("WorkType");
                var noteRaw = Value("Notes");

                if (!visibleByEmployeeId.TryGetValue(empId, out var user))
                {
                    errors.Add($"Line {lineNumber}: EmployeeId '{empId}' is unknown or out of your scope.");
                    if (errors.Count >= 25) break;
                    continue;
                }

                if (!DateOnly.TryParseExact(workDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var workDate))
                {
                    errors.Add($"Line {lineNumber}: WorkDate '{workDateRaw}' must be yyyy-MM-dd.");
                    if (errors.Count >= 25) break;
                    continue;
                }

                var workType = workTypeRaw switch
                {
                    _ when workTypeRaw.Equals("Onsite", StringComparison.OrdinalIgnoreCase) => "Onsite",
                    _ when workTypeRaw.Equals("Offsite", StringComparison.OrdinalIgnoreCase) => "Offsite",
                    _ when workTypeRaw.Equals("Dayoff", StringComparison.OrdinalIgnoreCase) => "Dayoff",
                    _ when workTypeRaw.Equals("Onleave", StringComparison.OrdinalIgnoreCase) => "Onleave",
                    _ when workTypeRaw.Equals("Holiday", StringComparison.OrdinalIgnoreCase) => "Holiday",
                    _ => string.Empty,
                };

                var hasBool = TryParseBool(isWorkingRaw, out var boolWorking);
                var isWorking = !string.IsNullOrEmpty(workType)
                    ? !ScheduleEntry.IsNonWorkingType(workType)
                    : (hasBool ? boolWorking : true);
                if (string.IsNullOrEmpty(workType))
                {
                    workType = isWorking ? "Onsite" : "Dayoff";
                }

                var start = ParseTime(startRaw);
                var end = ParseTime(endRaw);

                if (isWorking && (start is null || end is null))
                {
                    errors.Add($"Line {lineNumber}: StartTime and EndTime are required when IsWorking is true.");
                    if (errors.Count >= 25) break;
                    continue;
                }

                if (isWorking && !IsRoundTheClockScheduleUser(user)
                    && start is not null && end is not null && end.Value <= start.Value)
                {
                    errors.Add($"Line {lineNumber}: EndTime must be after StartTime for {user.FullName}.");
                    if (errors.Count >= 25) break;
                    continue;
                }

                var note = string.IsNullOrWhiteSpace(noteRaw) ? null : noteRaw.Trim();
                if (note is { Length: > 200 }) note = note[..200];

                parsed[(user.Id, workDate)] = new BulkScheduleRow(
                    user.Id,
                    workDate,
                    isWorking,
                    isWorking ? start : null,
                    isWorking ? end : null,
                    workType,
                    note);
            }
        }

        if (errors.Count > 0)
        {
            TempData.Flash(
                "Bulk upload failed: " + string.Join(" ", errors),
                "danger");
            return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
        }

        if (parsed.Count == 0)
        {
            TempData.Flash("No valid schedule rows found in uploaded file.", "warning");
            return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
        }

        var userIds = parsed.Keys.Select(k => k.UserId).Distinct().ToList();
        var dates = parsed.Keys.Select(k => k.WorkDate).Distinct().ToList();
        var existing = await Db.ScheduleEntries
            .Where(s => userIds.Contains(s.UserId) && dates.Contains(s.WorkDate))
            .ToListAsync();
        var existingByKey = existing.ToDictionary(s => (s.UserId, s.WorkDate));

        var inserted = 0;
        var updated = 0;
        foreach (var row in parsed.Values)
        {
            if (!existingByKey.TryGetValue((row.UserId, row.WorkDate), out var entry))
            {
                entry = new ScheduleEntry
                {
                    UserId = row.UserId,
                    WorkDate = row.WorkDate,
                };
                Db.ScheduleEntries.Add(entry);
                inserted++;
            }
            else
            {
                updated++;
            }

            entry.IsWorking = row.IsWorking;
            entry.StartTime = row.IsWorking ? row.StartTime : null;
            entry.EndTime = row.IsWorking ? row.EndTime : null;
            entry.WorkType = row.WorkType;
            if (row.Note is not null) entry.Note = row.Note;
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await Db.SaveChangesAsync();
        TempData.Flash(
            $"Bulk schedule upload applied. Rows: {parsed.Count}, inserted: {inserted}, updated: {updated}.",
            "success");
        return RedirectToAction(nameof(Edit), new { user_id = userId, month = monthRaw, week = weekRaw });
    }

    // ------------------------------------------------------------------
    // Default landing redirects to the per-user editor. The legacy
    // Coalition roster grid (BU2 PH Schedule) has been retired; admins
    // and PMs now manage every user schedule through the Edit page.
    // ------------------------------------------------------------------
    [HttpGet("")]
    public IActionResult Index([FromQuery(Name = "month")] string? monthRaw)
        => RedirectToAction(nameof(Edit), new { month = monthRaw });

    // Month editor with optional week filter. Query params:
    //   user_id : target user (admin-only override; defaults to self)
    //   month   : YYYY-MM (defaults to current PHT month)
    //   week    : 1..5    (filters editor to a single week; 0/missing = show all)
    [HttpGet("edit")]
    public async Task<IActionResult> Edit(
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "month")] string? monthRaw,
        [FromQuery(Name = "week")] int? weekRaw)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var target = me;
        if (userId is int uid && IsAdmin && await CanViewUserAsync(uid))
        {
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == uid) ?? me;
        }
        else if (userId is null
                 && IsAdmin
                 && string.Equals(me.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            // Pure admins don't carry a schedule of their own, so landing
            // on /schedule with no user_id would show a confusing
            // "No schedule required" notice while the dropdown silently
            // displays the first non-admin name. Pre-select that same
            // first non-admin so the page content matches the dropdown.
            var firstTarget = await Db.Users
                .Where(u => u.Role != Roles.Admin)
                .OrderBy(u => u.FullName)
                .FirstOrDefaultAsync();
            if (firstTarget is not null) target = firstTarget;
        }

        // Admin accounts don't carry a working schedule. Surface a notice
        // instead of pretending to assign one.
        var isAdminTarget = string.Equals(target.Role, Roles.Admin,
            StringComparison.OrdinalIgnoreCase);

        // Support users DO get a weekly grid \u2014 they just have free reign to
        // pick any times (including cross-midnight like 16:00 \u2192 00:00 or
        // 22:00 \u2192 06:00). The Save handler skips the end > start check for
        // them; the model's Hours calculation already handles the wrap.
        var isSupportTarget = target.IsSupport;
        var isRoundTheClockTarget = IsRoundTheClockScheduleUser(target);

        // Anchor month: query param wins, else current PHT month.
        var monthStart = ParseMonth(monthRaw) ?? FirstOfMonth(PhTime.Today);
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var monthEnd = monthStart.AddDays(daysInMonth - 1);
        var weekFilter = weekRaw is int w && w >= 1 && w <= 5 ? w : 0;

        // Only rows the admin actually authored exist in the DB; everything
        // else renders as an "off" placeholder in the view.
        var entries = isAdminTarget
            ? new List<ScheduleEntry>()
            : await DbInitializer.GetScheduleForRangeAsync(Db, target, monthStart, monthEnd);
        var entriesByDate = entries.ToDictionary(e => e.WorkDate);

        // Holidays override the displayed work type for the month grid so
        // the schedule editor reflects statutory non-working dates.
        if (!isAdminTarget)
        {
            var holidaysByDate = await HolidayHelper.RangeForAsync(Db, target, monthStart, monthEnd);
            foreach (var (date, _) in holidaysByDate)
            {
                entriesByDate.TryGetValue(date, out var existingRow);
                entriesByDate[date] = new ScheduleEntry
                {
                    Id = existingRow?.Id ?? 0,
                    UserId = target.Id,
                    WorkDate = date,
                    WorkType = "Holiday",
                    IsWorking = false,
                    Note = existingRow?.Note,
                    Source = existingRow?.Source,
                    UpdatedAt = existingRow?.UpdatedAt ?? DateTime.UtcNow,
                };
            }
            entries = entriesByDate.Values.OrderBy(e => e.WorkDate).ToList();
        }

        // Bucket month's dates by week-of-month (1..N).
        var buckets = new List<(int Week, DateOnly Start, DateOnly End, List<DateOnly> Dates)>();
        var currentBucket = new List<DateOnly>();
        var bucketIndex = 1;
        for (var i = 0; i < daysInMonth; i++)
        {
            var d = monthStart.AddDays(i);
            var wk = ((d.Day - 1) / 7) + 1;
            if (wk != bucketIndex)
            {
                buckets.Add((bucketIndex, currentBucket[0], currentBucket[^1], currentBucket));
                currentBucket = new List<DateOnly>();
                bucketIndex = wk;
            }
            currentBucket.Add(d);
        }
        if (currentBucket.Count > 0)
        {
            buckets.Add((bucketIndex, currentBucket[0], currentBucket[^1], currentBucket));
        }
        var visibleBuckets = weekFilter > 0
            ? buckets.Where(b => b.Week == weekFilter).ToList()
            : buckets;

        // Admins are excluded from the schedule picker \u2014 they don't have a
        // working schedule, so there's nothing to assign for them. Support
        // users stay in the picker so admins can manage their 24/7 hours.
        var allUsers = IsAdmin
            ? await (await GetVisibleUsersAsync())
                .Where(u => u.Role != Roles.Admin)
                .OrderBy(u => u.FullName)
                .ToListAsync()
            : new List<User>();

        var monthHours = Math.Round(entries.Sum(e => e.Hours), 2);

        // Support users own their schedule \u2014 admins still pick it for
        // everyone else. A support user viewing their own row can edit it
        // directly; viewing someone else's row keeps the read-only stance.
        var viewingSelf = target.Id == me.Id;
        var editable = (IsAdmin && !isAdminTarget)
            || (me.IsSupport && viewingSelf && !isAdminTarget);

        // Regular employees viewing their own schedule can file change
        // requests through the amendment workflow. PMs viewing their own
        // row also use the workflow (they are still subject to admin
        // approval). Support users edit directly, so no request needed.
        var canRequestAmendment = viewingSelf
            && !isAdminTarget
            && !isSupportTarget
            && !editable;

        // Pull the target user's in-flight requests so the view can
        // disable the per-day request button on dates already pending.
        var pendingAmends = await Db.ScheduleAmendments
            .Where(a => a.UserId == target.Id
                        && a.Status == "Pending"
                        && a.WorkDate >= monthStart
                        && a.WorkDate <= monthEnd)
            .ToListAsync();
        var pendingByDate = pendingAmends
            .GroupBy(a => a.WorkDate)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.RequestedAt).First());

        var vm = new ScheduleViewModel
        {
            Target = target,
            Entries = entries,
            // Only admins (and Support for their own row) can change
            // schedules directly. Everyone else goes through the
            // amendment-request workflow.
            Editable = editable,
            AllUsers = allUsers,
            WeeklyHours = monthHours,
            ViewerIsAdmin = IsAdmin,
            IsAdminTarget = isAdminTarget,
            IsSupportTarget = isSupportTarget,
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            WeekFilter = weekFilter,
            WeekBuckets = visibleBuckets,
            EntriesByDate = entriesByDate,
            PendingAmendmentsByDate = pendingByDate,
            CanRequestAmendment = canRequestAmendment,
        };
        return View(vm);
    }

    // Per-date save. The form posts back only the dates rendered in the
    // current week filter, each as `date_<ISO>` plus optional companion
    // fields working_<ISO>, start_<ISO>, end_<ISO> (and legacy note_<ISO>
    // when older views are still in use).
    [HttpPost("edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(
        [FromQuery(Name = "user_id")] int? userId,
        [FromQuery(Name = "month")] string? monthRaw,
        [FromQuery(Name = "week")] int? weekRaw,
        IFormCollection form)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var target = me;
        if (userId is int uid && IsAdmin)
        {
            if (!await CanViewUserAsync(uid)) return Forbid();
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == uid) ?? me;
        }

        // Schedule editing is admin-only, with one exception: a Support
        // user editing their own row. Everyone else (regular employees,
        // PMs working on someone else's row, etc.) must go through the
        // amendment-request workflow.
        var selfSupportSave = !IsAdmin && me.IsSupport && target.Id == me.Id;
        if (!IsAdmin && !selfSupportSave) return Forbid();

        if (string.Equals(target.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("Admin accounts don't have a working schedule.", "info");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }

        var isSupportTarget = target.IsSupport;
        var isRoundTheClockTarget = IsRoundTheClockScheduleUser(target);

        // Collect submitted dates from the form's date_* keys.
        var dates = new List<DateOnly>();
        foreach (var key in form.Keys)
        {
            if (!key.StartsWith("date_", StringComparison.Ordinal)) continue;
            var iso = form[key].ToString();
            if (DateOnly.TryParseExact(iso, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                dates.Add(d);
            }
        }
        dates = dates.Distinct().OrderBy(d => d).ToList();

        var existing = dates.Count == 0
            ? new Dictionary<DateOnly, ScheduleEntry>()
            : await Db.ScheduleEntries
                .Where(s => s.UserId == target.Id && dates.Contains(s.WorkDate))
                .ToDictionaryAsync(s => s.WorkDate);

        foreach (var date in dates)
        {
            var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var working = string.Equals(form[$"working_{iso}"].ToString(), "on",
                StringComparison.OrdinalIgnoreCase);
            var start = ParseTime(form[$"start_{iso}"].ToString());
            var end = ParseTime(form[$"end_{iso}"].ToString());
            var noteKey = $"note_{iso}";
            var hasNote = form.ContainsKey(noteKey);
            var note = (form[noteKey].ToString() ?? string.Empty).Trim();
            if (note.Length > 200) note = note[..200];

            // Working Type drives IsWorking on save: "Dayoff" overrides
            // the hidden working_<iso> flag, while "Onsite" / "Offsite"
            // both enable working hours. Unknown / missing values fall
            // back to the legacy IsWorking flag for backwards compat.
            var workTypeRaw = (form[$"worktype_{iso}"].ToString() ?? string.Empty).Trim();
            string? workType = workTypeRaw switch
            {
                _ when workTypeRaw.Equals("Onsite",  StringComparison.OrdinalIgnoreCase) => "Onsite",
                _ when workTypeRaw.Equals("Offsite", StringComparison.OrdinalIgnoreCase) => "Offsite",
                _ when workTypeRaw.Equals("Dayoff",  StringComparison.OrdinalIgnoreCase) => "Dayoff",
                _ when workTypeRaw.Equals("Onleave", StringComparison.OrdinalIgnoreCase) => "Onleave",
                _ when workTypeRaw.Equals("Holiday", StringComparison.OrdinalIgnoreCase) => "Holiday",
                _ => null,
            };
            if (workType is not null)
            {
                working = !ScheduleEntry.IsNonWorkingType(workType);
            }

            if (working && !isRoundTheClockTarget
                && start is not null && end is not null && end.Value <= start.Value)
            {
                TempData.Flash(
                    $"{date:ddd, MMM d}: end time must be after start time.",
                    "danger");
                return RedirectToAction(nameof(Edit),
                    new { user_id = target.Id, month = monthRaw, week = weekRaw });
            }

            if (!existing.TryGetValue(date, out var entry))
            {
                entry = new ScheduleEntry
                {
                    UserId = target.Id,
                    WorkDate = date,
                };
                Db.ScheduleEntries.Add(entry);
            }

            entry.IsWorking = working;
            entry.StartTime = working ? start : null;
            entry.EndTime = working ? end : null;
            if (hasNote)
            {
                entry.Note = note;
            }
            entry.WorkType = workType ?? (working ? "Onsite" : "Dayoff");
            entry.UpdatedAt = DateTime.UtcNow;
        }
        await Db.SaveChangesAsync();

        TempData.Flash("Schedule updated.", "success");
        return RedirectToAction(nameof(Edit),
            new { user_id = IsAdmin ? (int?)target.Id : null, month = monthRaw, week = weekRaw });
    }

    private static DateOnly? ParseMonth(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return FirstOfMonth(d);
        return null;
    }

    private static DateOnly FirstOfMonth(DateOnly d) => new(d.Year, d.Month, 1);

    private static TimeOnly? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (TimeOnly.TryParseExact(raw.Trim(), new[] { "HH:mm", "H:mm", "HH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var t))
            return t;
        if (TimeOnly.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out t))
            return t;
        return null;
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        var s = (raw ?? string.Empty).Trim();
        if (bool.TryParse(s, out value)) return true;
        if (s.Equals("1", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (s.Equals("0", StringComparison.OrdinalIgnoreCase)
            || s.Equals("no", StringComparison.OrdinalIgnoreCase)
            || s.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryParseCalendarCell(
        string raw,
        out bool isWorking,
        out TimeOnly? start,
        out TimeOnly? end,
        out string workType,
        out string? note)
    {
        isWorking = false;
        start = null;
        end = null;
        workType = "Dayoff";
        note = null;

        var lines = (raw ?? string.Empty)
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return false;

        var first = lines[0];
        if (first.Contains('-', StringComparison.Ordinal))
        {
            var pair = first.Split('-', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) return false;
            start = ParseTime(pair[0]);
            end = ParseTime(pair[1]);
            if (start is null || end is null) return false;

            isWorking = true;
            workType = lines.Length > 1
                ? (NormalizeWorkType(lines[1]) ?? "Onsite")
                : "Onsite";
            if (ScheduleEntry.IsNonWorkingType(workType)) workType = "Onsite";
            if (lines.Length > 2)
            {
                note = string.Join(" ", lines.Skip(2)).Trim();
                if (note.Length == 0) note = null;
                if (note is { Length: > 200 }) note = note[..200];
            }
            return true;
        }

        var normalized = NormalizeWorkType(first);
        if (normalized is null) return false;
        workType = normalized;
        isWorking = !ScheduleEntry.IsNonWorkingType(workType);
        if (isWorking)
        {
            start = ParseTime("09:00");
            end = ParseTime("18:00");
        }

        if (lines.Length > 1)
        {
            note = string.Join(" ", lines.Skip(1)).Trim();
            if (note.Length == 0) note = null;
            if (note is { Length: > 200 }) note = note[..200];
        }
        return true;
    }

    private static string? NormalizeWorkType(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        if (s.Equals("Onsite", StringComparison.OrdinalIgnoreCase)) return "Onsite";
        if (s.Equals("Offsite", StringComparison.OrdinalIgnoreCase)) return "Offsite";
        if (s.Equals("Dayoff", StringComparison.OrdinalIgnoreCase)) return "Dayoff";
        if (s.Equals("Onleave", StringComparison.OrdinalIgnoreCase)) return "Onleave";
        if (s.Equals("Holiday", StringComparison.OrdinalIgnoreCase)) return "Holiday";
        return null;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }
            if (c == ',' && !inQuotes)
            {
                cells.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    private static string CsvField(string value)
    {
        if (value.Contains('"')) value = value.Replace("\"", "\"\"");
        return value.Contains(',') || value.Contains('\n') || value.Contains('\r') || value.Contains('"')
            ? $"\"{value}\""
            : value;
    }

    // -------------------------------------------------------------------
    // Shift-amendment workflow
    //
    // Employees (and PMs) who can't edit a schedule directly can file a
    // request to change one weekday. Admins and PMs approve or reject
    // those requests; on approval, the proposed values are applied to
    // the live ScheduleEntry row.
    // -------------------------------------------------------------------

    /// <summary>
    /// Employee submits an amendment request for ONE calendar date of
    /// their own schedule. Admins/PMs can also file on behalf of users
    /// they already manage.
    /// </summary>
    [HttpPost("amend")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Amend(
        string workDate,
        bool working,
        string? start,
        string? end,
        string? note,
        string? reason,
        [FromForm(Name = "proof_photo")] string? proofPhoto,
        [FromQuery(Name = "user_id")] int? userId)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var target = me;
        if (userId is int uid && uid != me.Id)
        {
            // Admin/PM filing on behalf is allowed only when they could
            // already view this person.
            if (!IsAdmin || !await CanViewUserAsync(uid))
                return Forbid();
            target = await Db.Users.FirstOrDefaultAsync(u => u.Id == uid) ?? me;
        }

        // Admins don't carry schedules; support users edit directly.
        if (string.Equals(target.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("Admin accounts don't have a working schedule.", "info");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }
        if (target.IsSupport && target.Id == me.Id)
        {
            TempData.Flash("Support users edit their schedule directly \u2014 no request needed.", "info");
            return RedirectToAction(nameof(Edit));
        }

        if (!DateOnly.TryParseExact(workDate ?? string.Empty, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            TempData.Flash("Pick a valid date.", "danger");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }
        reason = (reason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            TempData.Flash("Please add a short reason for the change.", "danger");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }
        if (reason.Length > 500) reason = reason[..500];

        var startT = working ? ParseTime(start) : null;
        var endT = working ? ParseTime(end) : null;
        if (working && (startT is null || endT is null))
        {
            TempData.Flash("Please pick both a start and end time for a working day.", "danger");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }
        // For regular users we still require end > start; Support, Program
        // Managers, and Project Managers may save cross-midnight shifts.
        if (working && !IsRoundTheClockScheduleUser(target)
            && startT is not null && endT is not null && endT.Value <= startT.Value)
        {
            TempData.Flash("End time must be after start time.", "danger");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }

        note = (note ?? string.Empty).Trim();
        if (note.Length > 200) note = note[..200];

        // Block stacking requests for the same date.
        var existing = await Db.ScheduleAmendments
            .Where(a => a.UserId == target.Id && a.WorkDate == date && a.Status == "Pending")
            .FirstOrDefaultAsync();
        if (existing is not null)
        {
            TempData.Flash($"You already have a pending request for {date:ddd, MMM d, yyyy}.", "warning");
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }

        var safeProof = SanitizePhoto(proofPhoto);
        if (safeProof is null)
        {
            TempData.Flash("Image proof is required for shift-change requests.", "danger");
            if (target.Id == me.Id && !IsAdmin)
                return RedirectToAction("Index", "Leave", new { tab = "shift" });
            return RedirectToAction(nameof(Edit), new { user_id = target.Id });
        }

        var amend = new ScheduleAmendment
        {
            UserId = target.Id,
            RequestedByUserId = me.Id,
            RequestedAt = DateTime.UtcNow,
            WorkDate = date,
            ProposedIsWorking = working,
            ProposedStartTime = startT,
            ProposedEndTime = endT,
            ProposedNote = string.IsNullOrEmpty(note) ? null : note,
            Reason = reason,
            ProofPhoto = safeProof,
            Status = "Pending",
        };
        Db.ScheduleAmendments.Add(amend);
        await Db.SaveChangesAsync();

        TempData.Flash("Shift-change request submitted. A PM/admin will review it shortly.", "success");
        // Employees file shift-change requests from the Leave page (the
        // unified Requests hub for employees). Admins/PMs still land back
        // on the schedule editor since they may have been filing on the
        // user's behalf.
        if (target.Id == me.Id && !IsAdmin)
        {
            return RedirectToAction("Index", "Leave", new { tab = "shift" });
        }
        return RedirectToAction(nameof(Edit), new { user_id = target.Id });
    }

    /// <summary>
    /// Admin/PM queue of pending amendment requests, scoped to the
    /// users the viewer can see (BU for PMs, everyone for pure admins).
    /// </summary>
    [HttpGet("amendments")]
    public async Task<IActionResult> Amendments()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        if (!IsAdmin) return Forbid();

        var visibleIds = await (await GetVisibleUsersAsync(includeAdmins: IsPureAdmin))
            .Select(u => u.Id)
            .ToListAsync();

        var pending = await Db.ScheduleAmendments
            .Include(a => a.User)
            .Include(a => a.RequestedByUser)
            .Where(a => a.Status == "Pending" && visibleIds.Contains(a.UserId))
            .OrderBy(a => a.RequestedAt)
            .ToListAsync();

        var recent = await Db.ScheduleAmendments
            .Include(a => a.User)
            .Include(a => a.RequestedByUser)
            .Include(a => a.DecidedByUser)
            .Where(a => a.Status != "Pending" && visibleIds.Contains(a.UserId))
            .OrderByDescending(a => a.DecidedAt)
            .Take(30)
            .ToListAsync();

        return View(new ScheduleAmendmentsViewModel { Pending = pending, Recent = recent });
    }

    /// <summary>Admin/PM approves a pending amendment and applies it.</summary>
    [HttpPost("amendments/{id:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAmendment(int id, string? decisionNote)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        if (!IsAdmin) return Forbid();

        var amend = await Db.ScheduleAmendments
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (amend is null) return NotFound();
        if (!await CanViewUserAsync(amend.UserId)) return Forbid();
        if (!string.Equals(amend.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("That request is no longer pending.", "warning");
            return RedirectToAction(nameof(Amendments));
        }

        // Locate / create the matching schedule row and apply the
        // proposed values verbatim.
        var target = amend.User!;
        var entry = await Db.ScheduleEntries
            .FirstOrDefaultAsync(e => e.UserId == target.Id && e.WorkDate == amend.WorkDate);
        if (entry is null)
        {
            entry = new ScheduleEntry
            {
                UserId = target.Id,
                WorkDate = amend.WorkDate,
            };
            Db.ScheduleEntries.Add(entry);
        }

        entry.IsWorking = amend.ProposedIsWorking;
        entry.StartTime = amend.ProposedIsWorking ? amend.ProposedStartTime : null;
        entry.EndTime = amend.ProposedIsWorking ? amend.ProposedEndTime : null;
        if (amend.ProposedNote is not null) entry.Note = amend.ProposedNote;
        entry.UpdatedAt = DateTime.UtcNow;

        amend.Status = "Approved";
        amend.DecidedByUserId = me.Id;
        amend.DecidedAt = DateTime.UtcNow;
        var note = (decisionNote ?? string.Empty).Trim();
        amend.DecisionNote = note.Length == 0 ? null : (note.Length > 500 ? note[..500] : note);

        await Db.SaveChangesAsync();
        TempData.Flash($"Approved {target.FullName}'s {amend.WorkDate:ddd, MMM d} request.", "success");
        return RedirectToAction(nameof(Amendments));
    }

    /// <summary>Admin/PM rejects a pending amendment.</summary>
    [HttpPost("amendments/{id:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectAmendment(int id, string? decisionNote)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        if (!IsAdmin) return Forbid();

        var amend = await Db.ScheduleAmendments
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (amend is null) return NotFound();
        if (!await CanViewUserAsync(amend.UserId)) return Forbid();
        if (!string.Equals(amend.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("That request is no longer pending.", "warning");
            return RedirectToAction(nameof(Amendments));
        }

        amend.Status = "Rejected";
        amend.DecidedByUserId = me.Id;
        amend.DecidedAt = DateTime.UtcNow;
        var note = (decisionNote ?? string.Empty).Trim();
        amend.DecisionNote = note.Length == 0 ? null : (note.Length > 500 ? note[..500] : note);

        await Db.SaveChangesAsync();
        TempData.Flash($"Rejected {amend.User!.FullName}'s {amend.WorkDate:ddd, MMM d} request.", "warning");
        return RedirectToAction(nameof(Amendments));
    }

    // ------------------------------------------------------------------
    // Bulk approve / reject for the pending schedule-amendment queue.
    // Single-row internals mirrored here so per-row validation (visibility,
    // already-decided) still gates each id; skipped rows are tallied.
    // ------------------------------------------------------------------
    [HttpPost("amendments/bulk-approve")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkApproveAmendments(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? decisionNote)
        => BulkDecideAmendmentsAsync(ids, decisionNote, approve: true);

    [HttpPost("amendments/bulk-reject")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> BulkRejectAmendments(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? decisionNote)
        => BulkDecideAmendmentsAsync(ids, decisionNote, approve: false);

    private async Task<IActionResult> BulkDecideAmendmentsAsync(
        int[]? ids, string? decisionNote, bool approve)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        if (!IsAdmin) return Forbid();

        var idList = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (idList.Count == 0)
        {
            TempData.Flash("Select at least one request to act on.", "warning");
            return RedirectToAction(nameof(Amendments));
        }

        var note = (decisionNote ?? string.Empty).Trim();
        if (note.Length > 500) note = note[..500];
        var noteValue = note.Length == 0 ? null : note;

        var rows = await Db.ScheduleAmendments
            .Include(a => a.User)
            .Where(a => idList.Contains(a.Id))
            .ToListAsync();

        var applied = 0;
        var skipped = 0;
        foreach (var amend in rows)
        {
            if (!string.Equals(amend.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            if (!await CanViewUserAsync(amend.UserId)) { skipped++; continue; }

            if (approve)
            {
                var target = amend.User!;
                var entry = await Db.ScheduleEntries
                    .FirstOrDefaultAsync(e => e.UserId == target.Id && e.WorkDate == amend.WorkDate);
                if (entry is null)
                {
                    entry = new ScheduleEntry
                    {
                        UserId = target.Id,
                        WorkDate = amend.WorkDate,
                    };
                    Db.ScheduleEntries.Add(entry);
                }
                entry.IsWorking = amend.ProposedIsWorking;
                entry.StartTime = amend.ProposedIsWorking ? amend.ProposedStartTime : null;
                entry.EndTime = amend.ProposedIsWorking ? amend.ProposedEndTime : null;
                if (amend.ProposedNote is not null) entry.Note = amend.ProposedNote;
                entry.UpdatedAt = DateTime.UtcNow;
                amend.Status = "Approved";
            }
            else
            {
                amend.Status = "Rejected";
            }
            amend.DecidedByUserId = me.Id;
            amend.DecidedAt = DateTime.UtcNow;
            amend.DecisionNote = noteValue;
            applied++;
        }

        await Db.SaveChangesAsync();

        var verb = approve ? "Approved" : "Rejected";
        var msg = skipped == 0
            ? $"{verb} {applied} schedule request(s)."
            : $"{verb} {applied} schedule request(s); skipped {skipped}.";
        TempData.Flash(msg, applied > 0 ? "success" : "warning");
        return RedirectToAction(nameof(Amendments));
    }

    /// <summary>Requester (or admin) cancels their own pending amendment.</summary>
    [HttpPost("amendments/{id:int}/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelAmendment(int id)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var amend = await Db.ScheduleAmendments.FirstOrDefaultAsync(a => a.Id == id);
        if (amend is null) return NotFound();
        var ownsRequest = amend.UserId == me.Id || amend.RequestedByUserId == me.Id;
        if (!ownsRequest && !IsAdmin) return Forbid();
        if (!string.Equals(amend.Status, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("That request is no longer pending.", "warning");
            return RedirectToAction(nameof(Edit));
        }

        amend.Status = "Cancelled";
        amend.DecidedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        TempData.Flash("Request cancelled.", "info");
        // Admins viewing the queue go back to the queue. Everyone else
        // (the employee themselves) lands on the Leave page \u2014 the
        // unified Requests hub for employees.
        if (IsAdmin && amend.UserId != me.Id)
        {
            return RedirectToAction(nameof(Amendments));
        }
        return RedirectToAction("Index", "Leave", new { tab = "shift" });
    }

    private static string? SanitizePhoto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        const int MaxLength = 4 * 1024 * 1024;
        if (raw.Length > MaxLength) return null;
        if (!raw.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        var commaIdx = raw.IndexOf(',');
        if (commaIdx <= 0) return null;
        return raw;
    }

    /// <summary>
    /// Users that follow 24/7-style schedule timing rules and can carry
    /// cross-midnight shifts (end <= start).
    /// </summary>
    private static bool IsRoundTheClockScheduleUser(User user)
        => user.IsSupport
           || string.Equals(user.Role, Roles.ProgramManager, StringComparison.OrdinalIgnoreCase)
           || string.Equals(user.Role, Roles.Pm, StringComparison.OrdinalIgnoreCase);
}
