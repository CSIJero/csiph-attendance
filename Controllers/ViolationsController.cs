using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin enforcement queue \u2014 Step 9 of the system flow. Each row of the
/// <c>notification_log</c> represents one offline-during-shift violation
/// detected by <see cref="OfflineNotifierService"/>. Admins use this page
/// to decide what (if any) enforcement action to apply (warning,
/// salary-deduction flag, or override / dismiss). The decision is stamped
/// onto the same row so the original alert audit is preserved.
/// </summary>
[Authorize]
[Route("violations")]
public class ViolationsController : AppController
{
    private readonly IEmailSender _email;

    public ViolationsController(AppDbContext db, IEmailSender email) : base(db)
    {
        _email = email;
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("")]
    public async Task<IActionResult> Index(string? filter = null)
    {
        // Restrict to users the current viewer can see (admins: everyone;
        // PMs: their team only). Pulled fresh on every load so newly
        // added team members show up automatically.
        var visibleIds = await (await GetVisibleUsersAsync())
            .Select(u => u.Id)
            .ToListAsync();

        // Only actual violation rows \u2014 i.e. attempts that resulted in a
        // Warning or Deduction notice (or would have, if recipients had
        // been configured). Pure failures with no level set are skipped.
        var baseQuery = Db.NotificationLogs
            .Include(n => n.ActionByUser)
            .Where(n => n.OfflineMinutes >= 60
                        && (n.Level == "Warning" || n.Level == "Deduction")
                        && n.UserId != null
                        && visibleIds.Contains(n.UserId!.Value));

        var f = (filter ?? "pending").Trim().ToLowerInvariant();
        IQueryable<NotificationLog> filtered = f switch
        {
            "investigate" => baseQuery.Where(n => n.ActionTaken == "Investigating"),
            "closed" => baseQuery.Where(n => n.ActionTaken == "Closed" || n.ActionTaken == "Warning" || n.ActionTaken == "Deduction" || n.ActionTaken == "Overridden" || n.ActionTaken == "ProgManApprovedOverride" || n.ActionTaken == "ProgManReview" || n.ActionTaken == "ProgManRejected"),
            "all" => baseQuery,
            "actioned" => baseQuery.Where(n => n.ActionTaken == "Closed" || n.ActionTaken == "Warning" || n.ActionTaken == "Deduction" || n.ActionTaken == "Overridden" || n.ActionTaken == "ProgManApprovedOverride" || n.ActionTaken == "ProgManReview" || n.ActionTaken == "ProgManRejected"),
            _ => baseQuery.Where(n => n.ActionTaken == null),
        };

        var rows = await filtered
            .OrderByDescending(n => n.SentAt)
            .Take(500)
            .ToListAsync();

        var pendingCount = await baseQuery.CountAsync(n => n.ActionTaken == null);
        var investigateCount = await baseQuery.CountAsync(n => n.ActionTaken == "Investigating");
        var closedCount = await baseQuery.CountAsync(n => n.ActionTaken == "Closed" || n.ActionTaken == "Warning" || n.ActionTaken == "Deduction" || n.ActionTaken == "Overridden" || n.ActionTaken == "ProgManApprovedOverride" || n.ActionTaken == "ProgManReview" || n.ActionTaken == "ProgManRejected");

        return View("Index", new ViolationsIndexViewModel
        {
            Rows = rows,
            Filter = f,
            PendingCount = pendingCount,
            InvestigateCount = investigateCount,
            NeedActionCount = 0,
            ClosedCount = closedCount,
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("{id:int}/act"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Act(
        int id,
        [FromForm] string? action,
        [FromForm] string? note,
        [FromForm] string? filter)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var row = await Db.NotificationLogs.FirstOrDefaultAsync(n => n.Id == id);
        if (row is null) return NotFound();

        // Stay inside the visibility scope: PMs can only act on rows for
        // users they're allowed to see; pure admins always can.
        if (row.UserId is null || !await CanViewUserAsync(row.UserId.Value))
        {
            return Forbid();
        }

        var rawAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        var normalised = rawAction switch
        {
            "send" => "Investigating",
            "investigate" => "Investigating",
            "sendprogman" or "escalate" => "Closed",
            "approveoverride" => "Closed",
            "disapprove" or "reject" => "Closed",
            "close" or "closed" => "Closed",
            "warning" => "Warning",
            "deduction" => "Deduction",
            "override" or "overridden" or "dismiss" => "Overridden",
            "clear" => "Closed",
            "reset" or "delete" => "__DELETE__",
            "" => null,
            _ => null,
        };

        if (normalised == "__DELETE__")
        {
            Db.NotificationLogs.Remove(row);
            await Db.SaveChangesAsync();
            TempData.Flash("Reminder cleared (false alarm removed).", "success");
            return RedirectToAction(nameof(Index), new { filter = filter ?? "pending" });
        }

        row.ActionTaken = normalised;
        if (normalised is null)
        {
            // Reset \u2014 clear the audit trail back to pending.
            row.ActionByUserId = null;
            row.ActionAt = null;
            row.ActionNote = null;
        }
        else
        {
            row.ActionByUserId = me.Id;
            row.ActionAt = DateTime.UtcNow;
            var trimmed = (note ?? string.Empty).Trim();
            if (trimmed.Length > 500) trimmed = trimmed[..500];
            row.ActionNote = AppendThreadEntry(row.ActionNote, me.FullName, trimmed);
        }

        await Db.SaveChangesAsync();
        if (normalised == "Investigating")
        {
            await TrySendInvestigationRequestEmailAsync(row);
        }
        TempData.Flash(
            normalised is null
                ? "Violation cleared back to pending."
                : $"Recorded \"{normalised}\" for {row.UserFullName ?? row.Username ?? "user"}.",
            "success");
        var targetFilter = normalised switch
        {
            "Investigating" => "investigate",
            null => "pending",
            _ => "closed",
        };
        return RedirectToAction(nameof(Index), new { filter = targetFilter });
    }

    // ------------------------------------------------------------------
    // Bulk-act on multiple reminders in one shot. The Reminders page
    // groups rows by date and lets the admin tick a group-level "select
    // all" checkbox to action every row in that group with one click.
    //
    // Accepts the same vocabulary as <see cref="Act(int, string?, string?)"/>:
    //   "warning"   -> "Warning"
    //   "deduction" -> "Deduction"
    //   "override"  -> "Overridden"
    //   "clear"     -> null (reset to pending)
    // Anything else is treated as "clear" \u2014 the table makes only
    // these four actions reachable so this is just defensive.
    // ------------------------------------------------------------------
    [HttpPost("bulk-act"), ValidateAntiForgeryToken]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> BulkAct(
        [FromForm(Name = "ids")] int[]? ids,
        [FromForm] string? action,
        [FromForm] string? note,
        [FromForm(Name = "filter")] string? filter)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var idList = (ids ?? Array.Empty<int>()).Distinct().ToList();
        if (idList.Count == 0)
        {
            TempData.Flash("Select at least one reminder to act on.", "warning");
            return RedirectToAction(nameof(Index), new { filter = filter ?? "pending" });
        }

        var rawAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        var normalised = rawAction switch
        {
            "send" => "Investigating",
            "investigate" => "Investigating",
            "sendprogman" or "escalate" => "Closed",
            "approveoverride" => "Closed",
            "disapprove" or "reject" => "Closed",
            "close" or "closed" => "Closed",
            "warning" => "Warning",
            "deduction" => "Deduction",
            "override" or "overridden" or "dismiss" => "Overridden",
            "clear" => "Closed",
            "reset" or "delete" => "__DELETE__",
            "" => null,
            _ => null,
        };

        var rows = await Db.NotificationLogs
            .Where(n => idList.Contains(n.Id))
            .ToListAsync();
        if (rows.Count == 0)
        {
            TempData.Flash("None of the selected reminders could be found.", "warning");
            return RedirectToAction(nameof(Index), new { filter = filter ?? "pending" });
        }

        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length > 500) trimmed = trimmed[..500];
        var noteValue = trimmed.Length == 0 ? null : trimmed;

        var applied = 0;
        var deleted = 0;
        var skipped = 0;
        var investigateRows = new List<NotificationLog>();
        foreach (var row in rows)
        {
            // Visibility scope: PMs can only touch reminders for users
            // they're allowed to see. Pure admins always can.
            if (row.UserId is null || !await CanViewUserAsync(row.UserId.Value))
            {
                skipped++;
                continue;
            }

            if (normalised == "__DELETE__")
            {
                Db.NotificationLogs.Remove(row);
                deleted++;
            }
            else
            {
                row.ActionTaken = normalised;
                if (normalised is null)
                {
                    row.ActionByUserId = null;
                    row.ActionAt = null;
                    row.ActionNote = null;
                }
                else
                {
                    row.ActionByUserId = me.Id;
                    row.ActionAt = DateTime.UtcNow;
                    row.ActionNote = AppendThreadEntry(row.ActionNote, me.FullName, noteValue);
                    if (normalised == "Investigating") investigateRows.Add(row);
                }
                applied++;
            }
        }

        await Db.SaveChangesAsync();
        foreach (var inv in investigateRows)
        {
            await TrySendInvestigationRequestEmailAsync(inv);
        }

        var label = normalised switch
        {
            "__DELETE__" => "Cleared",
            null => "Reset",
            _ => normalised,
        };
        var baseMsg = $"Applied \"{label}\" to {applied} reminder(s)";
        if (deleted > 0) baseMsg += $" and deleted {deleted} false-alarm row(s)";
        if (skipped > 0) baseMsg += $"; skipped {skipped} you can't act on";
        var msg = baseMsg + ".";
        TempData.Flash(msg, (applied + deleted) > 0 ? "success" : "warning");
        return RedirectToAction(nameof(Index), new { filter = filter ?? "pending" });
    }

    [HttpGet("inbox")]
    public async Task<IActionResult> Inbox()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var rows = await Db.NotificationLogs
            .Where(n => n.UserId == me.Id
                        && n.OfflineMinutes >= 60
                        && (n.Level == "Warning" || n.Level == "Deduction")
                        && n.ActionTaken == "Investigating")
            .OrderByDescending(n => n.SentAt)
            .Take(200)
            .ToListAsync();

        return View("Inbox", new ViolationsInboxViewModel
        {
            Rows = rows,
        });
    }

    [HttpPost("{id:int}/explain")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Explain(int id, [FromForm] string? explanation)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var row = await Db.NotificationLogs
            .FirstOrDefaultAsync(n => n.Id == id);
        if (row is null) return NotFound();
        if (row.UserId != me.Id) return Forbid();
        if (!string.Equals(row.ActionTaken, "Investigating", StringComparison.OrdinalIgnoreCase))
        {
            TempData.Flash("This reminder is not in Investigate state.", "warning");
            return RedirectToAction(nameof(Inbox));
        }

        var text = (explanation ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            TempData.Flash("Please enter your explanation before sending.", "warning");
            return RedirectToAction(nameof(Inbox));
        }
        if (text.Length > 500) text = text[..500];

        row.ActionNote = AppendThreadEntry(row.ActionNote, me.FullName, text, isEmployee: true);
        row.ActionAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();

        await TrySendExplanationThreadEmailAsync(row, me);
        TempData.Flash("Explanation sent. Your Project Manager received the thread by email.", "success");
        return RedirectToAction(nameof(Inbox));
    }

    private static string? AppendThreadEntry(string? existing, string? author, string? note, bool isEmployee = false)
    {
        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length == 0) return existing;
        var who = string.IsNullOrWhiteSpace(author)
            ? (isEmployee ? "Employee" : "Manager")
            : author.Trim();
        var stamp = PhTime.Now.ToString("yyyy-MM-dd HH:mm");
        var entry = $"[{stamp}] {who}: {trimmed}";
        if (string.IsNullOrWhiteSpace(existing)) return entry;
        return existing + "\n" + entry;
    }

    private async Task TrySendInvestigationRequestEmailAsync(NotificationLog row)
    {
        try
        {
            if (row.UserId is not int uid) return;
            var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == uid);
            var to = user?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(to)) return;

            var subject = "[Attendance] Reminder: explanation requested";
            var body =
                "Your manager started an investigation for an offline reminder.\n\n"
                + $"Detected: {PhTime.Format(row.SentAt, "yyyy-MM-dd HH:mm")} PHT\n"
                + $"Offline duration: {row.OfflineMinutes} minute(s)\n"
                + "\nPlease open the Inbox in the system and submit your explanation.";
            await _email.SendAsync(new[] { to }, subject, body);
        }
        catch
        {
            // Don't block investigation workflow if email is unavailable.
        }
    }

    private async Task TrySendExplanationThreadEmailAsync(NotificationLog row, User employee)
    {
        try
        {
            var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (employee.ManagerId is int mid)
            {
                var mgr = await Db.Users.FirstOrDefaultAsync(u => u.Id == mid && u.Approved);
                if (!string.IsNullOrWhiteSpace(mgr?.Email)) recipients.Add(mgr.Email.Trim());
            }

            if (recipients.Count == 0)
            {
                var sameBuLeads = await Db.Users
                    .Where(u => u.Approved
                                && (u.Role == Roles.Pm || u.Role == Roles.ProgramManager)
                                && u.BusinessUnit == employee.BusinessUnit
                                && u.Email != null
                                && u.Email != "")
                    .Select(u => u.Email)
                    .ToListAsync();
                foreach (var mail in sameBuLeads) recipients.Add(mail!.Trim());
            }

            if (recipients.Count == 0) return;

            var subject = "[Attendance] Reminder: employee explanation thread";
            var body =
                $"Employee: {employee.FullName} ({employee.Username})\n"
                + $"Detected: {PhTime.Format(row.SentAt, "yyyy-MM-dd HH:mm")} PHT\n"
                + $"Offline duration: {row.OfflineMinutes} minute(s)\n\n"
                + "Conversation thread:\n"
                + (row.ActionNote ?? "(no thread notes)");
            await _email.SendAsync(recipients, subject, body);
        }
        catch
        {
            // Keep the in-app explanation saved even if email fails.
        }
    }

}
