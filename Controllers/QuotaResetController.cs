using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Employee-submitted requests to reset the daily break / lunch counter
/// to zero so they can take another one. Any admin-tier user
/// (Admin, Program Manager, Project Manager) can approve or reject.
/// Pure admins are not allowed to submit requests for themselves
/// because they don't run a tracked shift.
/// </summary>
[Authorize]
[Route("quota-reset")]
public class QuotaResetController : AppController
{
    public QuotaResetController(AppDbContext db) : base(db) { }

    // ------------------------------------------------------------------
    // List view \u2014 admins see the pending queue + recent decisions;
    // employees viewing this URL get bounced to their dashboard.
    // ------------------------------------------------------------------
    [HttpGet("")]
    [Authorize(Policy = "AdminOrOperations")]
    public async Task<IActionResult> Index([FromQuery] string? bu = null)
    {
        var businessUnitFilter = RuntimeConfig.NormaliseBusinessUnit(bu);
        ViewBag.IsOperationsRequests = IsOperations;
        ViewBag.BusinessUnitFilter = businessUnitFilter;
        ViewBag.BusinessUnits = RuntimeConfig.GetBusinessUnits();

        if (IsOperations)
        {
            var approved = Db.QuotaResetRequests
                .Include(r => r.User)
                .Include(r => r.RequestedByUser)
                .Include(r => r.DecidedByUser)
                .Where(r => r.Status == "Approved"
                            && r.DecidedByUser != null
                            && r.DecidedByUser.Role == Roles.Pm);
            if (!string.IsNullOrWhiteSpace(businessUnitFilter))
            {
                approved = approved.Where(r =>
                    r.User != null && r.User.BusinessUnit == businessUnitFilter);
            }

            return View("Index", new QuotaResetIndexViewModel
            {
                Recent = await approved
                    .OrderByDescending(r => r.DecidedAt)
                    .Take(100)
                    .ToListAsync(),
            });
        }

        var visibleIds = await (await GetVisibleUsersAsync(includeAdmins: IsPureAdmin))
            .Select(u => u.Id)
            .ToListAsync();

        var pending = await Db.QuotaResetRequests
            .Include(r => r.User)
            .Include(r => r.RequestedByUser)
            .Where(r => r.Status == "Pending" && visibleIds.Contains(r.UserId))
            .OrderBy(r => r.RequestedAt)
            .ToListAsync();

        var recent = await Db.QuotaResetRequests
            .Include(r => r.User)
            .Include(r => r.RequestedByUser)
            .Include(r => r.DecidedByUser)
            .Where(r => r.Status != "Pending" && visibleIds.Contains(r.UserId))
            .OrderByDescending(r => r.DecidedAt)
            .Take(25)
            .ToListAsync();

        return View("Index", new QuotaResetIndexViewModel
        {
            Pending = pending,
            Recent = recent,
        });
    }

    // ------------------------------------------------------------------
    // Defensive GET handler. The form posts to /quota-reset/request, but
    // if [Authorize] kicks an unauthenticated POST to /Account/Login, the
    // cookie middleware redirects back to the original URL as GET after
    // sign-in \u2014 which would 405 against the POST-only Submit. Also covers
    // the case where someone refreshes / bookmarks / "back-buttons" into
    // this URL directly. In all cases, send them to the dashboard.
    // ------------------------------------------------------------------
    [HttpGet("request")]
    public IActionResult RequestRedirect()
        => RedirectToAction("Index", "Dashboard");

    // ------------------------------------------------------------------
    // Employee submits a new request. Posted from the dashboard buttons.
    // ------------------------------------------------------------------
    [HttpPost("request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
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
            return RedirectToAction("Index", "Dashboard");
        }

        var normalised = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised != "break" && normalised != "lunch")
        {
            TempData.Flash("Invalid quota kind \u2014 must be \"break\" or \"lunch\".", "danger");
            return RedirectToAction("Index", "Dashboard");
        }

        var today = PhTime.Today;

        // Don't accept a duplicate while one is still pending for the same
        // kind on the same PHT day.
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
            return RedirectToAction("Index", "Dashboard");
        }

        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length > 500) trimmedReason = trimmedReason[..500];

        var row = new QuotaResetRequest
        {
            UserId = me.Id,
            RequestedByUserId = me.Id,
            RequestedAt = DateTime.UtcNow,
            Kind = normalised,
            TargetDate = today,
            Reason = trimmedReason,
            Status = "Pending",
        };
        Db.QuotaResetRequests.Add(row);
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Your {normalised} reset request was submitted \u2014 awaiting approval.",
            "success");
        return RedirectToAction("Index", "Dashboard");
    }

    // ------------------------------------------------------------------
    // Approve \u2014 zero out the target counter and roll its date forward.
    // ------------------------------------------------------------------
    [HttpPost("{id:int}/approve"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Approve(int id, [FromForm] string? note)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var req = await Db.QuotaResetRequests
            .Include(r => r.User)
            .Include(r => r.RequestedByUser)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (req is null) return NotFound();

        if (req.Status != "Pending")
        {
            TempData.Flash("This request was already decided.", "warning");
            return RedirectToAction(nameof(Index));
        }
        if (!await CanViewUserAsync(req.UserId)) return Forbid();

        var target = req.User ?? await Db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId);
        if (target is null) return NotFound();

        var today = PhTime.Today;
        if (string.Equals(req.Kind, "break", StringComparison.OrdinalIgnoreCase))
        {
            target.BreaksUsedToday = 0;
            target.BreaksUsedDate = today;
        }
        else if (string.Equals(req.Kind, "lunch", StringComparison.OrdinalIgnoreCase))
        {
            target.LunchesUsedToday = 0;
            target.LunchesUsedDate = today;
        }

        req.Status = "Approved";
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedByUserId = me.Id;
        req.DecisionNote = string.IsNullOrWhiteSpace(note)
            ? null
            : (note.Trim().Length > 500 ? note.Trim()[..500] : note.Trim());

        await Db.SaveChangesAsync();
        TempData.Flash(
            $"Approved \u2014 {req.User?.FullName ?? "user"}'s {req.Kind} counter has been reset.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Reject \u2014 close the request without applying.
    // ------------------------------------------------------------------
    [HttpPost("{id:int}/reject"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> Reject(int id, [FromForm] string? note)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var req = await Db.QuotaResetRequests
            .FirstOrDefaultAsync(r => r.Id == id);
        if (req is null) return NotFound();

        if (req.Status != "Pending")
        {
            TempData.Flash("This request was already decided.", "warning");
            return RedirectToAction(nameof(Index));
        }
        if (!await CanViewUserAsync(req.UserId)) return Forbid();

        req.Status = "Rejected";
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedByUserId = me.Id;
        req.DecisionNote = string.IsNullOrWhiteSpace(note)
            ? null
            : (note.Trim().Length > 500 ? note.Trim()[..500] : note.Trim());

        await Db.SaveChangesAsync();
        TempData.Flash("Request rejected.", "info");
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    // Bulk approve / reject for the pending quota-reset queue. Skipped
    // rows (already decided, outside visibility) are tallied.
    // ------------------------------------------------------------------
    [HttpPost("bulk-approve"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public Task<IActionResult> BulkApprove(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideAsync(ids, note, approve: true);

    [HttpPost("bulk-reject"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public Task<IActionResult> BulkReject(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? note)
        => BulkDecideAsync(ids, note, approve: false);

    private async Task<IActionResult> BulkDecideAsync(int[]? ids, string? note, bool approve)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var idList = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (idList.Count == 0)
        {
            TempData.Flash("Select at least one request to act on.", "warning");
            return RedirectToAction(nameof(Index));
        }

        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length > 500) trimmed = trimmed[..500];
        var noteValue = trimmed.Length == 0 ? null : trimmed;

        var rows = await Db.QuotaResetRequests
            .Include(r => r.User)
            .Where(r => idList.Contains(r.Id))
            .ToListAsync();

        var today = PhTime.Today;
        var applied = 0;
        var skipped = 0;
        foreach (var req in rows)
        {
            if (req.Status != "Pending") { skipped++; continue; }
            if (!await CanViewUserAsync(req.UserId)) { skipped++; continue; }

            if (approve)
            {
                var target = req.User ?? await Db.Users.FirstOrDefaultAsync(u => u.Id == req.UserId);
                if (target is null) { skipped++; continue; }
                if (string.Equals(req.Kind, "break", StringComparison.OrdinalIgnoreCase))
                {
                    target.BreaksUsedToday = 0;
                    target.BreaksUsedDate = today;
                }
                else if (string.Equals(req.Kind, "lunch", StringComparison.OrdinalIgnoreCase))
                {
                    target.LunchesUsedToday = 0;
                    target.LunchesUsedDate = today;
                }
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
            ? $"{verb} {applied} reset request(s)."
            : $"{verb} {applied} reset request(s); skipped {skipped}.";
        TempData.Flash(msg, applied > 0 ? "success" : "warning");
        return RedirectToAction(nameof(Index));
    }
}
