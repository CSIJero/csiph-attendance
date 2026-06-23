using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin-only management of the role catalogue surfaced on the
/// <em>Configuration</em> tab. Each <c>role_definitions</c> row is a real
/// role (e.g. "Project Manager", "Senior Engineer") with a canonical
/// permission tier in <see cref="RoleDefinition.BaseRole"/>. Picking one
/// of these rows from the Users → Create / Edit / Approve screens drives
/// both the display label (<c>RoleLabelId</c>) and the authorization tier
/// (<c>User.Role</c>).
/// </summary>
[Authorize(Roles = Roles.Admin)]
public class RolesController : AppController
{
    public RolesController(AppDbContext db) : base(db) { }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        // Order built-ins first (in canonical seniority), then custom roles
        // alphabetically. Gives the admin a stable mental model of which
        // rows are the "real" foundation vs. their own additions.
        var defs = await Db.RoleDefinitions.ToListAsync();
        var ordered = defs
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => BuiltInOrder(r.BaseRole))
            .ThenBy(r => r.Name)
            .ToList();

        // Per-role usage count so the admin sees the impact of a delete
        // before clicking through (the FK is SetNull so users survive,
        // they just lose the label).
        var counts = await Db.Users
            .Where(u => u.RoleLabelId != null)
            .GroupBy(u => u.RoleLabelId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
        ViewBag.UsageCounts = counts;
        return View(ordered);
    }

    /// <summary>
    /// Stable ordering for the four canonical tiers so the seeded rows
    /// always show in the same Admin → Employee top-to-bottom order.
    /// </summary>
    private static int BuiltInOrder(string baseRole) => baseRole switch
    {
        Models.Roles.Admin          => 0,
        Models.Roles.ProgramManager => 1,
        Models.Roles.Pm             => 2,
        Models.Roles.Employee       => 3,
        _                           => 99,
    };

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? name, string? baseRole)
    {
        var clean = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clean))
        {
            TempData.Flash("Role name is required.", "danger");
            return RedirectToAction(nameof(Index));
        }
        if (clean.Length > 80)
        {
            TempData.Flash("Role name is too long (max 80 characters).", "danger");
            return RedirectToAction(nameof(Index));
        }

        // BaseRole is required so every custom row maps to a known
        // permission tier. Normalise + validate against the canonical
        // four (admin / program_manager / pm / employee).
        var tier = Models.Roles.Normalise(baseRole);
        if (tier != Models.Roles.Admin
            && tier != Models.Roles.ProgramManager
            && tier != Models.Roles.Pm
            && tier != Models.Roles.Employee)
        {
            TempData.Flash("Pick a base permission tier (Administrator, Program Manager, Project Manager or Employee).", "danger");
            return RedirectToAction(nameof(Index));
        }

        // Case-insensitive duplicate guard. The unique index on Name would
        // also catch this, but checking here lets us surface a friendly
        // flash message instead of an EF/DB exception.
        var exists = await Db.RoleDefinitions
            .AnyAsync(r => r.Name.ToLower() == clean.ToLower());
        if (exists)
        {
            TempData.Flash($"\u201C{clean}\u201D already exists.", "warning");
            return RedirectToAction(nameof(Index));
        }

        Db.RoleDefinitions.Add(new RoleDefinition
        {
            Name = clean,
            BaseRole = tier,
            IsBuiltIn = false,
            CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Added role \u201C{clean}\u201D ({Models.Roles.DisplayName(tier)} tier).",
            "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var def = await Db.RoleDefinitions.FirstOrDefaultAsync(r => r.Id == id);
        if (def is null)
        {
            TempData.Flash("That role no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        // Built-in rows back the four canonical tiers — deleting one would
        // leave the Role dropdown without a representative entry for that
        // tier. Hard-block server-side regardless of what the UI sent.
        if (def.IsBuiltIn)
        {
            TempData.Flash(
                $"\u201C{def.Name}\u201D is a built-in role and can't be deleted.",
                "danger");
            return RedirectToAction(nameof(Index));
        }

        // SetNull cascade clears the FK on every user holding this label,
        // so we don't manually loop. Save once and let the DB do its job.
        Db.RoleDefinitions.Remove(def);
        await Db.SaveChangesAsync();

        TempData.Flash($"Deleted role \u201C{def.Name}\u201D.", "success");
        return RedirectToAction(nameof(Index));
    }
}

