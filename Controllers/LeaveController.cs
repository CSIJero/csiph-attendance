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
[Route("leave")]
public class LeaveController : AppController
{
    public LeaveController(AppDbContext db) : base(db) { }

    // ------------------------------------------------------------------
    // List view — employees see their own; admins/PMs see pending queue
    // plus recent decisions, scoped to their visible Business Unit.
    // ------------------------------------------------------------------
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();

        var mine = await Db.LeaveRequests
            .Where(r => r.UserId == meId)
            .Include(r => r.DecidedByUser)
            .OrderByDescending(r => r.RequestedAt)
            .Take(50)
            .ToListAsync();

        // Pull the current user once so the embedded "Request a shift
        // change" form (folded in from the Schedule page) knows whether
        // to render and which weekdays are already pending.
        var me = await Db.Users.FirstOrDefaultAsync(u => u.Id == meId);

        var vm = new LeaveIndexViewModel
        {
            Mine = mine,
            ViewerIsAdmin = IsAdmin,
            Me = me,
        };

        if (me is not null)
        {
            var pendingAmends = await Db.ScheduleAmendments
                .Where(a => a.UserId == me.Id && a.Status == "Pending")
                .ToListAsync();
            vm.MyPendingAmendmentsByDate = pendingAmends
                .GroupBy(a => a.WorkDate)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.RequestedAt).First());

            // History of the worker's own decided / cancelled shift-change
            // requests so they can see what was approved or rejected
            // without pinging the admin.
            vm.MyRecentAmendments = await Db.ScheduleAmendments
                .Where(a => a.UserId == me.Id && a.Status != "Pending")
                .Include(a => a.DecidedByUser)
                .OrderByDescending(a => a.DecidedAt ?? a.RequestedAt)
                .Take(25)
                .ToListAsync();

            // Admins don't carry schedules; Support users edit directly.
            // Everyone else (employee or PM) goes through the request flow.
            var isAdminRole = string.Equals(me.Role, Models.Roles.Admin,
                StringComparison.OrdinalIgnoreCase);
            vm.CanRequestShiftChange = !isAdminRole && !me.IsSupport;
        }

        if (IsAdmin)
        {
            // Restrict the admin / PM queue to users they're allowed to see
            // (admins: everyone; PMs: their own Business Unit).
            var visibleIds = await (await GetVisibleUsersAsync(includeAdmins: IsPureAdmin))
                .Select(u => u.Id)
                .ToListAsync();

            vm.Pending = await Db.LeaveRequests
                .Where(r => r.Status == "Pending" && visibleIds.Contains(r.UserId))
                .Include(r => r.User)
                .Include(r => r.RequestedByUser)
                .OrderBy(r => r.StartDate)
                .ToListAsync();

            vm.Recent = await Db.LeaveRequests
                .Where(r => r.Status != "Pending" && visibleIds.Contains(r.UserId))
                .Include(r => r.User)
                .Include(r => r.DecidedByUser)
                .OrderByDescending(r => r.DecidedAt)
                .Take(25)
                .ToListAsync();
        }

        return View(vm);
    }

    // ------------------------------------------------------------------
    // Submit form (GET)
    // ------------------------------------------------------------------
    [HttpGet("request")]
    public new async Task<IActionResult> Request()
    {
        var vm = await BuildFormVmAsync();
        return View("Request", vm);
    }

    [HttpPost("request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestPost(
        [FromForm(Name = "target_user_id")] int targetUserId,
        [FromForm(Name = "start_date")] string? startRaw,
        [FromForm(Name = "end_date")] string? endRaw,
        [FromForm(Name = "leave_type")] string? leaveType,
        [FromForm(Name = "hours_per_day")] string? hoursRaw,
        [FromForm(Name = "reason")] string? reasonRaw,
        [FromForm(Name = "proof_photo")] string? proofPhoto)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();

        // Employees can only file for themselves. PMs / admins can file for
        // anyone they can see.
        var resolvedTarget = IsAdmin ? targetUserId : meId.Value;
        if (!await CanViewUserAsync(resolvedTarget))
        {
            TempData.Flash("You cannot file leave for that user.", "danger");
            return RedirectToAction(nameof(Request));
        }

        // Accept both the new HTML5 datetime-local format (yyyy-MM-ddTHH:mm)
        // and the legacy plain-date format for backwards compatibility with
        // any old links or API callers.
        var dateFormats = new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(startRaw, dateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !DateTime.TryParseExact(endRaw, dateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
        {
            TempData.Flash("Please provide valid start / end date and time.", "danger");
            return RedirectToAction(nameof(Request));
        }

        if (end < start)
        {
            TempData.Flash("End date cannot be earlier than start date.", "danger");
            return RedirectToAction(nameof(Request));
        }

        var type = (leaveType ?? "Full").Trim();
        decimal hoursPerDay = type switch
        {
            "Full" => 8m,
            "HalfDay" => 4m,
            "Undertime" =>
                decimal.TryParse(hoursRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var h)
                    ? Math.Round(h, 2)
                    : 0m,
            // Early checkout: optional hours field captures how many hours
            // short the user expects to be. When omitted we default to 1h
            // so we still record a meaningful entitlement.
            "EarlyCheckout" =>
                decimal.TryParse(hoursRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var eh)
                    ? Math.Round(eh, 2)
                    : 1m,
            _ => 0m,
        };

        if (type is not ("Full" or "HalfDay" or "Undertime" or "EarlyCheckout"))
        {
            TempData.Flash("Unknown leave type.", "danger");
            return RedirectToAction(nameof(Request));
        }

        if (hoursPerDay <= 0 || hoursPerDay > 8)
        {
            TempData.Flash("Hours per day must be between 0.5 and 8.", "danger");
            return RedirectToAction(nameof(Request));
        }

        // Half-day / Undertime / Early checkout are single-day affairs by
        // convention. Snap the end date back to the start date but preserve
        // the user's chosen end-of-leave time so the row records the slice
        // of the day the worker will be out.
        if (type is "HalfDay" or "Undertime" or "EarlyCheckout")
        {
            end = start.Date + end.TimeOfDay;
            if (end < start) end = start;
        }

        var reason = (reasonRaw ?? string.Empty).Trim();
        if (reason.Length < 5)
        {
            TempData.Flash("Please provide a reason (5+ characters).", "danger");
            return RedirectToAction(nameof(Request));
        }
        if (reason.Length > 500) reason = reason[..500];

        var safeProof = SanitizePhoto(proofPhoto);
        if (safeProof is null)
        {
            TempData.Flash("Image proof is required for leave requests.", "danger");
            return RedirectToAction(nameof(Request));
        }

        var row = new LeaveRequest
        {
            UserId = resolvedTarget,
            RequestedByUserId = meId.Value,
            RequestedAt = DateTime.UtcNow,
            StartDate = start,
            EndDate = end,
            LeaveType = type,
            HoursPerDay = hoursPerDay,
            Reason = reason,
            ProofPhoto = safeProof,
            Status = "Pending",
        };
        Db.LeaveRequests.Add(row);
        await Db.SaveChangesAsync();

        TempData.Flash("Leave request submitted. Your PM / admin will review it.", "success");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Approve / Reject (admin + PM)
    // ------------------------------------------------------------------
    [HttpPost("{id:int}/approve"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Approve(int id, [FromForm] string? note)
        => await DecideAsync(id, "Approved", note);

    [HttpPost("{id:int}/reject"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Reject(int id, [FromForm] string? note)
        => await DecideAsync(id, "Rejected", note);

    // ------------------------------------------------------------------
    // Bulk approve / reject — applied to every pending request whose id
    // arrives in the POST body. Rows the reviewer can't act on (already
    // decided, outside their visibility) are silently skipped.
    // ------------------------------------------------------------------
    [HttpPost("bulk-approve"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public Task<IActionResult> BulkApprove(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideAsync(ids, "Approved", note);

    [HttpPost("bulk-reject"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public Task<IActionResult> BulkReject(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideAsync(ids, "Rejected", note);

    private async Task<IActionResult> BulkDecideAsync(int[]? ids, string status, string? note)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();

        var idList = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (idList.Count == 0)
        {
            TempData.Flash("Select at least one request to act on.", "warning");
            return RedirectToAction(nameof(Index));
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var rows = await Db.LeaveRequests
            .Where(r => idList.Contains(r.Id))
            .ToListAsync();

        var applied = 0;
        var skipped = 0;
        foreach (var row in rows)
        {
            if (row.Status != "Pending") { skipped++; continue; }
            if (!await CanViewUserAsync(row.UserId)) { skipped++; continue; }
            row.Status = status;
            row.DecidedByUserId = meId;
            row.DecidedAt = DateTime.UtcNow;
            row.DecisionNote = trimmed;
            if (status == "Approved")
            {
                await ApplyApprovedLeaveScheduleAsync(row);
            }
            applied++;
        }

        await Db.SaveChangesAsync();
        var verb = status == "Approved" ? "approved" : "rejected";
        var msg = skipped == 0
            ? $"{applied} leave request(s) {verb}."
            : $"{applied} leave request(s) {verb}; skipped {skipped}.";
        TempData.Flash(msg, applied > 0 ? "success" : "warning");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> DecideAsync(int id, string status, string? note)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();

        var row = await Db.LeaveRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null)
        {
            TempData.Flash("Leave request not found.", "danger");
            return RedirectToAction(nameof(Index));
        }
        if (!await CanViewUserAsync(row.UserId))
        {
            TempData.Flash("You cannot decide on that request.", "danger");
            return RedirectToAction(nameof(Index));
        }
        if (row.Status != "Pending")
        {
            TempData.Flash($"Request is already {row.Status}.", "info");
            return RedirectToAction(nameof(Index));
        }

        row.Status = status;
        row.DecidedByUserId = meId;
        row.DecidedAt = DateTime.UtcNow;
        row.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (status == "Approved")
        {
            await ApplyApprovedLeaveScheduleAsync(row);
        }
        await Db.SaveChangesAsync();

        TempData.Flash($"Leave {status.ToLowerInvariant()}.", "success");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Cancel (employees can withdraw their own pending requests)
    // ------------------------------------------------------------------
    [HttpPost("{id:int}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();

        var row = await Db.LeaveRequests.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return RedirectToAction(nameof(Index));
        if (row.UserId != meId && !IsAdmin) return Forbid();
        if (row.Status != "Pending")
        {
            TempData.Flash("Only pending requests can be cancelled.", "info");
            return RedirectToAction(nameof(Index));
        }

        row.Status = "Cancelled";
        row.DecidedByUserId = meId;
        row.DecidedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        TempData.Flash("Leave request cancelled.", "success");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
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

    private async Task<LeaveRequestFormViewModel> BuildFormVmAsync()
    {
        var meId = CurrentUserId ?? 0;
        var candidates = IsAdmin
            ? await (await GetVisibleUsersAsync()).OrderBy(u => u.FullName).ToListAsync()
            : new List<User>();

        return new LeaveRequestFormViewModel
        {
            CandidateUsers = candidates,
            TargetUserId = meId,
            StartDate = PhTime.Today.ToDateTime(new TimeOnly(9, 0)),
            EndDate = PhTime.Today.ToDateTime(new TimeOnly(18, 0)),
            LeaveType = "Full",
            HoursPerDay = 8m,
            ViewerIsAdmin = IsAdmin,
        };
    }

    /// <summary>
    /// Reflect an approved leave request onto the per-date schedule by
    /// marking covered dates as Onleave (non-working).
    /// </summary>
    private async Task ApplyApprovedLeaveScheduleAsync(LeaveRequest row)
    {
        var start = DateOnly.FromDateTime(row.StartDate);
        var end = DateOnly.FromDateTime(row.EndDate);
        if (end < start) (start, end) = (end, start);

        var existing = await Db.ScheduleEntries
            .Where(s => s.UserId == row.UserId
                        && s.WorkDate >= start
                        && s.WorkDate <= end)
            .ToDictionaryAsync(s => s.WorkDate);

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (!existing.TryGetValue(d, out var entry))
            {
                entry = new ScheduleEntry
                {
                    UserId = row.UserId,
                    WorkDate = d,
                };
                Db.ScheduleEntries.Add(entry);
                existing[d] = entry;
            }

            entry.WorkType = "Onleave";
            entry.IsWorking = false;
            entry.StartTime = null;
            entry.EndTime = null;
            entry.UpdatedAt = DateTime.UtcNow;
        }
    }
}
