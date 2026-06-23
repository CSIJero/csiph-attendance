using System.Security.Claims;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Base controller with a couple of shared conveniences (current-user lookup).
/// </summary>
public abstract class AppController : Controller
{
    protected readonly AppDbContext Db;

    protected AppController(AppDbContext db) => Db = db;

    protected int? CurrentUserId
    {
        get
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(raw, out var id) ? id : null;
        }
    }

    protected bool IsAdmin =>
        User.IsInRole(Models.Roles.Admin)
        || User.IsInRole(Models.Roles.ProgramManager)
        || User.IsInRole(Models.Roles.Pm);

    /// <summary>
    /// True only for the literal "admin" role. Pure admins have unrestricted
    /// visibility across every Business Unit; PMs do not.
    /// </summary>
    protected bool IsPureAdmin => User.IsInRole(Models.Roles.Admin);

    /// <summary>True only for the "program_manager" role.</summary>
    protected bool IsProgramManager => User.IsInRole(Models.Roles.ProgramManager);

    /// <summary>True only for the "pm" role.</summary>
    protected bool IsPm => User.IsInRole(Models.Roles.Pm);

    protected async Task<User?> GetCurrentUserAsync()
    {
        var id = CurrentUserId;
        if (id is null) return null;
        return await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }

    /// <summary>
    /// Users the current viewer is allowed to see:
    /// <list type="bullet">
    ///   <item>Pure admins see everyone.</item>
    ///   <item>PMs see members of their own Business Unit (themselves
    ///         included). PMs without a Business Unit see only themselves.</item>
    ///   <item>Employees see only themselves.</item>
    /// </list>
    /// <para>
    /// By default, accounts with the <c>admin</c> role are filtered out of
    /// the result so admin records don't show up in dashboards, team
    /// status, schedule pickers, attendance histories, reports, or leave
    /// queues. The Users-management page passes
    /// <paramref name="includeAdmins"/> = <c>true</c> so admins can still
    /// be edited / approved from that screen.
    /// </para>
    /// Returns an <see cref="IQueryable{User}"/> so callers can compose
    /// further filters / projections without re-materialising.
    /// </summary>
    protected async Task<IQueryable<User>> GetVisibleUsersAsync(bool includeAdmins = false)
    {
        IQueryable<User> q;
        if (IsPureAdmin)
        {
            q = Db.Users.AsQueryable();
        }
        else
        {
            var me = await GetCurrentUserAsync();
            if (me is null) return Db.Users.Where(u => false);

            if (IsProgramManager)
            {
                // Program Manager: scope = self + every user whose BU is
                // in the PgM's multi-BU list. The list lives on the user
                // row as a semicolon-separated string; materialise it once
                // here so LINQ-to-Entities `Contains` translates to a SQL
                // IN clause on the BU column.
                var buList = me.BusinessUnitList;
                if (buList.Count == 0)
                {
                    // Unassigned PgM: lock to self until an admin assigns
                    // at least one Business Unit, otherwise they'd accidentally
                    // see every BU-less user (the legacy "bu IS NULL" case).
                    return Db.Users.Where(u => u.Id == me.Id);
                }
                q = Db.Users.Where(u =>
                    u.Id == me.Id
                    || (u.BusinessUnit != null && buList.Contains(u.BusinessUnit)));
            }
            else if (IsPm)
            {
                // Project Manager: scope = self + only their direct reports
                // (users whose ManagerId points at them). This is tighter
                // than the previous "whole BU" policy.
                q = Db.Users.Where(u => u.Id == me.Id || u.ManagerId == me.Id);
            }
            else
            {
                return Db.Users.Where(u => u.Id == me.Id);
            }
        }

        if (!includeAdmins)
        {
            // Hide admin records from dashboards, reports, pickers, etc.
            // Admins themselves are intentionally excluded too \u2014 they don't
            // clock attendance through this app, so showing them in
            // team-status / reports just clutters the view.
            q = q.Where(u => u.Role != Roles.Admin);
        }
        return q;
    }

    /// <summary>
    /// Checks whether the current viewer is allowed to see the given user.
    /// Mirrors <see cref="GetVisibleUsersAsync"/>'s rules but answers a
    /// single-row question without an extra DB round-trip when possible.
    /// </summary>
    protected async Task<bool> CanViewUserAsync(int targetId)
    {
        if (CurrentUserId == targetId) return true;
        if (IsPureAdmin) return true;

        var me = await GetCurrentUserAsync();
        if (me is null) return false;

        if (IsProgramManager)
        {
            // Program Manager: target must live in one of the PgM's BUs.
            var buList = me.BusinessUnitList;
            if (buList.Count == 0) return false;
            var target = await Db.Users
                .Where(u => u.Id == targetId)
                .Select(u => new { u.BusinessUnit })
                .FirstOrDefaultAsync();
            return target is not null
                && target.BusinessUnit != null
                && buList.Contains(target.BusinessUnit);
        }

        if (IsPm)
        {
            // Project Manager: target must be a direct report.
            return await Db.Users.AnyAsync(u =>
                u.Id == targetId && u.ManagerId == me.Id);
        }

        return false;
    }
}
