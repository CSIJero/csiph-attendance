using System.Globalization;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

[Authorize]
[Route("schedule")]
public class ScheduleController : AppController
{
    public ScheduleController(AppDbContext db) : base(db) { }

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

        var visibleIds = await (await GetVisibleUsersAsync()).Select(u => u.Id).ToListAsync();

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
