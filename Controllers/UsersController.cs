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
/// Admin / PM-only management of user records. Lets either role update any
/// user's profile fields (name, contact, role, business unit, etc.) but
/// never the password — password resets remain a separate, more sensitive
/// flow that this controller intentionally does not expose.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public class UsersController : AppController
{
    public UsersController(AppDbContext db) : base(db) { }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        // Users-management lists every account in scope, admins included,
        // so an admin can still edit / approve other admin records here.
        var visible = await GetVisibleUsersAsync(includeAdmins: true);
        var all = await visible
            .Include(u => u.RoleLabel)
            .OrderBy(u => u.FullName)
            .ToListAsync();
        return View(new UsersListViewModel
        {
            Users   = all.Where(u => u.Approved).ToList(),
            Pending = all.Where(u => !u.Approved).OrderBy(u => u.CreatedAt).ToList(),
            // Two manager picklists for the Approve modal: PM-tier for
            // employees and PgM/Admin-tier for PMs being promoted.
            ProjectManagerChoices = await GetProjectManagerChoicesAsync(),
            ProgramManagerChoices = await GetProgramManagerChoicesAsync(),
            RoleLabelChoices      = await GetRoleLabelChoicesAsync(),
            BusinessUnitChoices   = RuntimeConfig.GetBusinessUnits(),
        });
    }

    /// <summary>
    /// Flips <c>Approved=true</c> on a pending registration so the user can
    /// sign in. Optionally promotes the user to a higher role at the same
    /// time — public registration always creates accounts as <c>employee</c>
    /// so this is where an admin upgrades them to <c>pm</c> or <c>admin</c>.
    /// Admin / PM only (enforced by the controller-level policy).
    /// </summary>
    /// <summary>
    /// Flips <c>Approved=true</c> on a pending registration so the user can
    /// sign in. Optionally promotes the user to a higher role at the same
    /// time — public registration always creates accounts as <c>employee</c>
    /// so this is where an admin upgrades them to <c>pm</c>,
    /// <c>program_manager</c> or <c>admin</c>. Admin / PM only (enforced by
    /// the controller-level policy).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(
        int id,
        string? role = null,
        int? managerId = null,
        string? businessUnits = null,
        string? businessUnit = null,
        int? roleLabelId = null)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        // Resolve the role to assign. The form posts the RoleDefinition
        // Id as `roleLabelId` plus a mirrored canonical role string as
        // `role`. Trust the FK: look up the definition and use its
        // BaseRole regardless of what the hidden Role field said — this
        // means a tampered hidden Role can't elevate a user beyond what
        // the picked role definition actually grants. If the FK isn't
        // supplied (legacy callers / API tests), fall back to the
        // normalised string or the user's current role.
        string assignedRole;
        RoleDefinition? roleDef = null;
        if (roleLabelId is int requestedRlid)
        {
            roleDef = await Db.RoleDefinitions.FirstOrDefaultAsync(r => r.Id == requestedRlid);
            if (roleDef is null)
            {
                TempData.Flash("Pick a valid role from the list.", "danger");
                return RedirectToAction(nameof(Index));
            }
            assignedRole = Roles.Normalise(roleDef.BaseRole);
        }
        else if (!string.IsNullOrWhiteSpace(role))
        {
            assignedRole = Roles.Normalise(role);
        }
        else
        {
            assignedRole = user.Role;
        }

        // Only a pure admin can elevate a registration into the admin,
        // program-manager, or operations tier.
        if (!IsPureAdmin
            && (assignedRole == Roles.Admin
                || assignedRole == Roles.ProgramManager
                || assignedRole == Roles.Operations))
        {
            TempData.Flash(
                "Only an administrator can approve into that role.",
                "danger");
            return RedirectToAction(nameof(Index));
        }

        // Validate the manager assignment per role:
        //   employee → must be a Project Manager (or Program Manager / Admin)
        //   pm       → must be a Program Manager (or Admin)
        //   program_manager / admin → manager is irrelevant; drop the value.
        if (assignedRole == Roles.Employee || assignedRole == Roles.Pm)
        {
            if (managerId is int mid)
            {
                if (mid == id)
                {
                    TempData.Flash("A user can't be assigned as their own manager.", "danger");
                    return RedirectToAction(nameof(Index));
                }
                var managerOk = await Db.Users.AnyAsync(u =>
                    u.Id == mid && u.Approved
                    && (assignedRole == Roles.Employee
                        ? (u.Role == Roles.Pm || u.Role == Roles.ProgramManager || u.Role == Roles.Admin)
                        : (u.Role == Roles.ProgramManager || u.Role == Roles.Admin)));
                if (!managerOk)
                {
                    TempData.Flash("Pick a valid manager from the list.", "danger");
                    return RedirectToAction(nameof(Index));
                }
            }
        }
        else
        {
            managerId = null; // PgM / admin don't carry a direct manager.
        }

        // Multi-BU is only meaningful for Program Manager. Sanitise the
        // semicolon-separated list: trim, drop empty entries, validate each
        // BU against the known list, de-duplicate. Approving a Program
        // Manager requires at least one BU so the visibility scope is
        // meaningful (an empty list would leave the PgM self-only).
        string? buMulti = null;
        if (assignedRole == Roles.ProgramManager)
        {
            if (string.IsNullOrWhiteSpace(businessUnits))
            {
                TempData.Flash("Pick at least one Business Unit for the Program Manager.", "danger");
                return RedirectToAction(nameof(Index));
            }
            var parts = businessUnits
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => RuntimeConfig.NormaliseBusinessUnit(p) ?? p)
                .Where(p => RuntimeConfig.IsKnownBusinessUnit(p))
                .Distinct()
                .ToList();
            if (parts.Count == 0)
            {
                TempData.Flash("Pick at least one valid Business Unit for the Program Manager.", "danger");
                return RedirectToAction(nameof(Index));
            }
            buMulti = string.Join(";", parts);
        }

        // Single-BU is for everyone except Program Manager (who uses the
        // multi-BU list above). Validate when provided.
        string? buSingle = null;
        if (assignedRole != Roles.ProgramManager)
        {
            var bu = RuntimeConfig.NormaliseBusinessUnit(businessUnit) ?? string.Empty;
            if (!string.IsNullOrEmpty(bu) && !RuntimeConfig.IsKnownBusinessUnit(bu))
            {
                TempData.Flash("Pick a valid Business Unit from the list.", "danger");
                return RedirectToAction(nameof(Index));
            }
            // BU is required for Employee/PM. Fall back to the existing
            // value only if the admin didn't supply one AND the user
            // already has one on file; otherwise force the admin to pick.
            buSingle = string.IsNullOrEmpty(bu) ? user.BusinessUnit : bu;
            if (string.IsNullOrEmpty(buSingle))
            {
                TempData.Flash("Business Unit is required for Employee / Project Manager.", "danger");
                return RedirectToAction(nameof(Index));
            }
        }

        if (user.Approved)
        {
            TempData.Flash($"{user.FullName} is already approved.", "info");
            return RedirectToAction(nameof(Index));
        }

        // Role label is optional. Empty / null means "no label". We've
        // already verified the definition exists when resolving the
        // canonical role above, so no extra DB hit here.
        if (roleLabelId is int rlid && roleDef is null)
        {
            var labelOk = await Db.RoleDefinitions.AnyAsync(r => r.Id == rlid);
            if (!labelOk)
            {
                TempData.Flash("Pick a valid role label from the list.", "danger");
                return RedirectToAction(nameof(Index));
            }
        }

        user.Role = assignedRole;
        user.ManagerId = managerId;
        user.RoleLabelId = roleLabelId;
        user.BusinessUnit = assignedRole == Roles.ProgramManager ? null : buSingle;
        user.BusinessUnits = buMulti;
        user.Approved = true;
        await Db.SaveChangesAsync();
        TempData.Flash(
            $"Approved {user.FullName} as {Roles.DisplayName(assignedRole)}. They can now sign in.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Deletes a user record permanently. Used both to reject a pending
    /// registration and to remove an existing account from the Users tab.
    /// Guards: nobody can delete themselves, and the last admin/PM cannot
    /// be removed (lockout protection).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var me = await GetCurrentUserAsync();
        if (me is not null && me.Id == id)
        {
            TempData.Flash("You can't delete your own account.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        // Only a pure admin can delete another admin-tier account.
        if (!IsPureAdmin
            && (Roles.IsAdminRole(user.Role) || user.Role == Roles.Operations))
        {
            TempData.Flash(
                "Only an administrator can delete an elevated account.",
                "danger");
            return RedirectToAction(nameof(Index));
        }

        // Don't let the team delete its way into a lockout — keep at least
        // one approved admin / PM around at all times.
        if (Roles.IsAdminRole(user.Role) && user.Approved)
        {
            var remainingAdmins = await Db.Users.CountAsync(u =>
                u.Id != id && u.Approved
                && (u.Role == Roles.Admin || u.Role == Roles.Pm));
            if (remainingAdmins == 0)
            {
                TempData.Flash(
                    "Can't delete the last admin / PM \u2014 promote another user first.",
                    "danger");
                return RedirectToAction(nameof(Index));
            }
        }

        // Tidy up the user's attendance / schedule rows so the cascade
        // doesn't leave orphans on databases without referential constraints.
        var attendance = Db.Attendances.Where(a => a.UserId == id);
        var schedule = Db.ScheduleEntries.Where(s => s.UserId == id);
        Db.Attendances.RemoveRange(attendance);
        Db.ScheduleEntries.RemoveRange(schedule);
        Db.Users.Remove(user);
        await Db.SaveChangesAsync();

        var label = user.Approved ? "Deleted" : "Rejected";
        TempData.Flash($"{label} {user.FullName}.", "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Deactivates an approved account, marking the user as no longer
    /// employed. Preserves all historical attendance and schedule data.
    /// Deactivated users cannot sign in. Guards: cannot deactivate the
    /// last active admin/PM (lockout protection).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var me = await GetCurrentUserAsync();
        if (me is not null && me.Id == id)
        {
            TempData.Flash("You can't deactivate your own account.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        if (!IsPureAdmin && user.Role == Roles.Operations)
        {
            TempData.Flash("Only an administrator can deactivate an Operations account.", "danger");
            return RedirectToAction(nameof(Index));
        }

        if (!user.IsActive)
        {
            TempData.Flash($"{user.FullName} is already deactivated.", "info");
            return RedirectToAction(nameof(Index));
        }

        // Don't let the team deactivate its way into a lockout — keep at
        // least one approved active admin / PM around at all times.
        if (Roles.IsAdminRole(user.Role) && user.Approved)
        {
            var remainingAdmins = await Db.Users.CountAsync(u =>
                u.Id != id && u.Approved && u.IsActive
                && (u.Role == Roles.Admin || u.Role == Roles.Pm));
            if (remainingAdmins == 0)
            {
                TempData.Flash(
                    "Can't deactivate the last active admin / PM — promote another user first.",
                    "danger");
                return RedirectToAction(nameof(Index));
            }
        }

        user.IsActive = false;
        await Db.SaveChangesAsync();
        TempData.Flash($"Deactivated {user.FullName}. Their account is preserved for historical records.", "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Reactivates a deactivated account, allowing the user to sign in again.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(int id)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        if (user.IsActive)
        {
            TempData.Flash($"{user.FullName} is already active.", "info");
            return RedirectToAction(nameof(Index));
        }

        user.IsActive = true;
        await Db.SaveChangesAsync();
        TempData.Flash($"Reactivated {user.FullName}. They can now sign in again.", "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        return View(new EditUserViewModel
        {
            Id = user.Id,
            FullName = user.FullName,
            Username = user.Username,
            Email = user.Email,
            EmployeeId = user.EmployeeId,
            BusinessUnit = user.BusinessUnit,
            BusinessUnits = user.BusinessUnits,
            Role = user.Role,
            RoleLabelId = user.RoleLabelId,
            ManagerId = user.ManagerId,
            IsSupport = user.IsSupport,
            OptOutReminderEmails = user.OptOutReminderEmails,
            ProjectManagerChoices = await GetProjectManagerChoicesAsync(user.Id),
            ProgramManagerChoices = await GetProgramManagerChoicesAsync(user.Id),
            RoleLabelChoices = await GetRoleLabelChoicesAsync(),
            BusinessUnitChoices = RuntimeConfig.GetBusinessUnits(),
        });
    }

    /// <summary>
    /// Approved managers that can supervise an <b>Employee</b>: Project
    /// Managers, Program Managers and Admins. Excludes <paramref name="excludeId"/>
    /// so a user can't be set as their own manager from the Edit form.
    /// </summary>
    private async Task<List<User>> GetProjectManagerChoicesAsync(int? excludeId = null)
    {
        var q = Db.Users.Where(u => u.Approved
            && (u.Role == Roles.Pm || u.Role == Roles.ProgramManager || u.Role == Roles.Admin));
        if (excludeId is int x) q = q.Where(u => u.Id != x);
        return await q.OrderBy(u => u.FullName).ToListAsync();
    }

    /// <summary>
    /// Approved managers that can supervise a <b>Project Manager</b>:
    /// Program Managers and Admins.
    /// </summary>
    private async Task<List<User>> GetProgramManagerChoicesAsync(int? excludeId = null)
    {
        var q = Db.Users.Where(u => u.Approved
            && (u.Role == Roles.ProgramManager || u.Role == Roles.Admin));
        if (excludeId is int x) q = q.Where(u => u.Id != x);
        return await q.OrderBy(u => u.FullName).ToListAsync();
    }

    /// <summary>
    /// Legacy single-list picklist kept for any caller that hasn't yet
    /// switched to the role-specific helpers. Returns every approved PM,
    /// PgM or Admin (i.e. anyone who could be a manager of any kind).
    /// </summary>
    private async Task<List<User>> GetManagerChoicesAsync(int? excludeId = null)
        => await GetProjectManagerChoicesAsync(excludeId);

    /// <summary>
    /// All admin-managed role labels, sorted alphabetically. Populates the
    /// "Role label" dropdown on the Edit / Create user forms.
    /// </summary>
    private async Task<List<RoleDefinition>> GetRoleLabelChoicesAsync()
        => await Db.RoleDefinitions.OrderBy(r => r.Name).ToListAsync();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, EditUserViewModel vm)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        if (!IsPureAdmin && user.Role == Roles.Operations)
        {
            TempData.Flash("Only an administrator can edit an Operations account.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var fullName = (vm.FullName ?? string.Empty).Trim();
        var username = (vm.Username ?? string.Empty).Trim();
        var email = (vm.Email ?? string.Empty).Trim();
        var employeeId = (vm.EmployeeId ?? string.Empty).Trim();
        var businessUnit = RuntimeConfig.NormaliseBusinessUnit(vm.BusinessUnit) ?? string.Empty;
        // Refresh picklists on validation-failure re-render path.
        vm.ProjectManagerChoices = await GetProjectManagerChoicesAsync(id);
        vm.ProgramManagerChoices = await GetProgramManagerChoicesAsync(id);
        vm.RoleLabelChoices = await GetRoleLabelChoicesAsync();
        vm.BusinessUnitChoices = RuntimeConfig.GetBusinessUnits();

        // The form posts the RoleDefinition Id as `vm.RoleLabelId` plus
        // a hidden canonical role string in `vm.Role`. Trust the FK: look
        // up the definition and override Role with its BaseRole \u2014 see
        // Approve() for the rationale. If no FK was supplied fall back
        // to the canonical string (legacy callers).
        RoleDefinition? roleDef = null;
        string role;
        if (vm.RoleLabelId is int reqRlid)
        {
            roleDef = await Db.RoleDefinitions.FirstOrDefaultAsync(r => r.Id == reqRlid);
            if (roleDef is null)
            {
                TempData.Flash("Pick a valid role from the list.", "danger");
                return View(vm);
            }
            role = Roles.Normalise(roleDef.BaseRole);
            vm.Role = role;
        }
        else
        {
            role = Roles.Normalise(vm.Role);
        }
        var roleLabelId = vm.RoleLabelId;
        var managerId = vm.ManagerId;

        // Privilege-escalation guard: only pure admins can grant the
        // "admin", "program_manager", or "operations" tier.
        var elevatingToAdminTier = (role == Roles.Admin
                                    || role == Roles.ProgramManager
                                    || role == Roles.Operations)
            && role != user.Role;
        if (!IsPureAdmin && elevatingToAdminTier)
        {
            TempData.Flash("Only an administrator can grant that role.", "danger");
            return View(vm);
        }

        if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(username)
            || string.IsNullOrEmpty(email))
        {
            TempData.Flash("Full name, username and email are required.", "danger");
            return View(vm);
        }

        if (!string.IsNullOrEmpty(businessUnit) && !RuntimeConfig.IsKnownBusinessUnit(businessUnit))
        {
            TempData.Flash("Pick a valid Business Unit from the list.", "danger");
            return View(vm);
        }

        // BU is required for everyone except Program Manager (which uses
        // the multi-BU list below). Enforce on the edit path so PM/Employee
        // rows can't be saved without a host BU.
        if (string.IsNullOrEmpty(businessUnit) && role != Roles.ProgramManager)
        {
            TempData.Flash("Business Unit is required.", "danger");
            return View(vm);
        }

        // Role label is admin-managed; if a value is supplied it must
        // resolve to an existing row in role_definitions. Empty / null
        // means "no label" which is always allowed. We've already
        // verified the definition exists when resolving the canonical
        // role above, so no extra DB hit here.
        if (roleLabelId is int rlid && roleDef is null)
        {
            var labelOk = await Db.RoleDefinitions.AnyAsync(r => r.Id == rlid);
            if (!labelOk)
            {
                TempData.Flash("Pick a valid role label from the list.", "danger");
                return View(vm);
            }
        }

        // Manager FK must match the role hierarchy:
        //   employee → PM / PgM / Admin
        //   pm       → PgM / Admin
        //   PgM, Admin → no manager (managerId is silently dropped).
        if (role == Roles.Employee || role == Roles.Pm)
        {
            if (managerId is int mid)
            {
                if (mid == id)
                {
                    TempData.Flash("A user can't be assigned as their own manager.", "danger");
                    return View(vm);
                }
                var managerOk = await Db.Users.AnyAsync(u =>
                    u.Id == mid && u.Approved
                    && (role == Roles.Employee
                        ? (u.Role == Roles.Pm || u.Role == Roles.ProgramManager || u.Role == Roles.Admin)
                        : (u.Role == Roles.ProgramManager || u.Role == Roles.Admin)));
                if (!managerOk)
                {
                    TempData.Flash("Pick a valid manager from the list.", "danger");
                    return View(vm);
                }
            }
        }
        else
        {
            managerId = null;
        }

        // Multi-BU is only valid when the user is a Program Manager.
        string? buMulti = null;
        if (role == Roles.ProgramManager && !string.IsNullOrWhiteSpace(vm.BusinessUnits))
        {
            var parts = vm.BusinessUnits
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => RuntimeConfig.NormaliseBusinessUnit(p) ?? p)
                .Where(p => RuntimeConfig.IsKnownBusinessUnit(p))
                .Distinct()
                .ToList();
            if (parts.Count == 0)
            {
                TempData.Flash("Pick at least one valid Business Unit for the Program Manager.", "danger");
                return View(vm);
            }
            buMulti = string.Join(";", parts);
        }

        // Username and email are unique across the table; allow the user to
        // keep their own value but reject collisions with anybody else.
        if (await Db.Users.AnyAsync(u => u.Id != id && u.Username == username))
        {
            TempData.Flash("That username is already taken.", "danger");
            return View(vm);
        }
        if (await Db.Users.AnyAsync(u => u.Id != id && u.Email == email))
        {
            TempData.Flash("That email is already registered.", "danger");
            return View(vm);
        }

        // Guard against locking the system out by demoting the only remaining
        // admin-tier account. Counting admin + program_manager + pm because
        // any of them can reach this controller via the AdminOnly policy.
        if (Roles.IsAdminRole(user.Role) && !Roles.IsAdminRole(role))
        {
            var remainingAdmins = await Db.Users.CountAsync(u =>
                u.Id != id
                && (u.Role == Roles.Admin
                    || u.Role == Roles.ProgramManager
                    || u.Role == Roles.Pm));
            if (remainingAdmins == 0)
            {
                TempData.Flash(
                    "Can't demote the last admin-tier account — promote another user first.",
                    "danger");
                return View(vm);
            }
        }

        user.FullName = fullName;
        user.Username = username;
        user.Email = email;
        user.EmployeeId = string.IsNullOrEmpty(employeeId) ? null : employeeId;
        // Single-BU is for everyone except Program Manager. PgMs use the
        // multi-BU list and we explicitly null the single column to avoid
        // stale legacy data masquerading as their scope.
        user.BusinessUnit = role == Roles.ProgramManager
            ? null
            : (string.IsNullOrEmpty(businessUnit) ? null : businessUnit);
        user.BusinessUnits = buMulti;
        user.Role = role;
        user.RoleLabelId = roleLabelId;
        user.ManagerId = managerId;
        user.IsSupport = vm.IsSupport;
        user.OptOutReminderEmails = vm.OptOutReminderEmails;
        await Db.SaveChangesAsync();

        TempData.Flash($"Updated {user.FullName}.", "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Admin-only direct user creation. Unlike public registration, accounts
    /// created here are <c>Approved=true</c> immediately and can use any
    /// role the admin picks (PMs still can't mint admins).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Create()
    {
        return View(new NewUserViewModel
        {
            ProjectManagerChoices = await GetProjectManagerChoicesAsync(),
            ProgramManagerChoices = await GetProgramManagerChoicesAsync(),
            RoleLabelChoices = await GetRoleLabelChoicesAsync(),
            BusinessUnitChoices = RuntimeConfig.GetBusinessUnits(),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NewUserViewModel vm)
    {
        var fullName = (vm.FullName ?? string.Empty).Trim();
        var username = (vm.Username ?? string.Empty).Trim();
        var email = (vm.Email ?? string.Empty).Trim();
        var employeeId = (vm.EmployeeId ?? string.Empty).Trim();
        var businessUnit = RuntimeConfig.NormaliseBusinessUnit(vm.BusinessUnit) ?? string.Empty;
        // Same single-source-of-truth pattern as Edit POST / Approve:
        // if a RoleDefinition Id is supplied, look it up and derive the
        // canonical permission tier from its BaseRole. The hidden Role
        // string is ignored when the FK is present.
        RoleDefinition? roleDef = null;
        string role;
        if (vm.RoleLabelId is int reqRlid)
        {
            roleDef = await Db.RoleDefinitions.FirstOrDefaultAsync(r => r.Id == reqRlid);
            if (roleDef is null)
            {
                ViewBag.FieldErrors = new Dictionary<string, string> { ["RoleLabelId"] = "Pick a valid role from the list." };
                vm.ProjectManagerChoices = await GetProjectManagerChoicesAsync();
                vm.ProgramManagerChoices = await GetProgramManagerChoicesAsync();
                vm.RoleLabelChoices = await GetRoleLabelChoicesAsync();
                vm.BusinessUnitChoices = RuntimeConfig.GetBusinessUnits();
                TempData.Flash("Pick a valid role from the list.", "danger");
                return View(vm);
            }
            role = Roles.Normalise(roleDef.BaseRole);
            vm.Role = role;
        }
        else
        {
            role = Roles.Normalise(vm.Role);
        }
        var roleLabelId = vm.RoleLabelId;
        var managerId = vm.ManagerId;
        var password = vm.Password ?? string.Empty;

        // Echo cleaned values back so a failed submit keeps the admin's input.
        vm.FullName = fullName;
        vm.Username = username;
        vm.Email = email;
        vm.EmployeeId = employeeId;
        vm.RoleLabelId = roleLabelId;
        vm.ManagerId = managerId;
        // Refresh picklists for the re-render path.
        vm.ProjectManagerChoices = await GetProjectManagerChoicesAsync();
        vm.ProgramManagerChoices = await GetProgramManagerChoicesAsync();
        vm.RoleLabelChoices = await GetRoleLabelChoicesAsync();
        vm.BusinessUnitChoices = RuntimeConfig.GetBusinessUnits();
        vm.BusinessUnit = businessUnit;
        vm.Role = role;
        vm.Password = string.Empty; // never echo passwords back

        // Same validation collection pattern used by Account/Register so the
        // form can highlight every field at once.
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(fullName))
            errors["FullName"] = "Full name is required.";
        else if (fullName.Length < 2 || fullName.Length > 120)
            errors["FullName"] = "Full name must be 2–120 characters.";

        if (string.IsNullOrEmpty(username))
            errors["Username"] = "Username is required.";
        else if (!System.Text.RegularExpressions.Regex.IsMatch(
                username, @"^[A-Za-z0-9_.\-]{3,64}$"))
        {
            errors["Username"] =
                "Username must be 3–64 characters using letters, digits, dot, underscore or hyphen.";
        }

        if (string.IsNullOrEmpty(email))
            errors["Email"] = "Email is required.";
        else if (!System.Text.RegularExpressions.Regex.IsMatch(
                email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
        {
            errors["Email"] = "Enter a valid email address.";
        }
        else if (email.Length > 200)
        {
            errors["Email"] = "Email is too long (max 200 characters).";
        }

        if (!string.IsNullOrEmpty(employeeId)
            && !System.Text.RegularExpressions.Regex.IsMatch(employeeId, @"^E\d{6,7}$"))
        {
            errors["EmployeeId"] =
                "Employee ID must be E followed by 6–7 digits (e.g. E000123).";
        }

        if (!string.IsNullOrEmpty(businessUnit) && businessUnit.Length > 120)
            errors["BusinessUnit"] = "Business unit name is too long (max 120 characters).";
        else if (!string.IsNullOrEmpty(businessUnit) && !RuntimeConfig.IsKnownBusinessUnit(businessUnit))
            errors["BusinessUnit"] = "Pick a valid Business Unit from the list.";
        else if (string.IsNullOrEmpty(businessUnit) && role != Roles.ProgramManager)
            errors["BusinessUnit"] = "Business Unit is required.";

        if (roleLabelId is int rlid && roleDef is null)
        {
            var labelOk = await Db.RoleDefinitions.AnyAsync(r => r.Id == rlid);
            if (!labelOk)
            {
                errors["RoleLabelId"] = "Pick a valid role label from the list.";
            }
        }

        if (managerId is int mid)
        {
            var managerOk = await Db.Users.AnyAsync(u =>
                u.Id == mid && u.Approved
                && (role == Roles.Employee
                    ? (u.Role == Roles.Pm || u.Role == Roles.ProgramManager || u.Role == Roles.Admin)
                    : role == Roles.Pm
                        ? (u.Role == Roles.ProgramManager || u.Role == Roles.Admin)
                        : false));
            if (!managerOk)
            {
                errors["ManagerId"] = "Pick a valid manager from the list.";
            }
        }

        // Multi-BU is only valid for Program Manager; validate / normalise.
        string? buMulti = null;
        if (role == Roles.ProgramManager && !string.IsNullOrWhiteSpace(vm.BusinessUnits))
        {
            var parts = vm.BusinessUnits
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => RuntimeConfig.NormaliseBusinessUnit(p) ?? p)
                .Where(p => RuntimeConfig.IsKnownBusinessUnit(p))
                .Distinct()
                .ToList();
            if (parts.Count == 0)
            {
                errors["BusinessUnits"] = "Pick at least one valid Business Unit for the Program Manager.";
            }
            else
            {
                buMulti = string.Join(";", parts);
                vm.BusinessUnits = buMulti;
            }
        }
        else if (role == Roles.ProgramManager)
        {
            errors["BusinessUnits"] = "A Program Manager must be assigned at least one Business Unit.";
        }

        if (string.IsNullOrEmpty(password))
            errors["Password"] = "Password is required.";
        else if (password.Length < 6)
            errors["Password"] = "Password must be at least 6 characters.";
        else if (password.Length > 200)
            errors["Password"] = "Password is too long.";
        else if (!System.Text.RegularExpressions.Regex.IsMatch(password, @"[A-Za-z]")
                 || !System.Text.RegularExpressions.Regex.IsMatch(password, @"\d"))
        {
            errors["Password"] = "Password must contain at least one letter and one digit.";
        }

        // Privilege guard: only pure admins can mint an elevated role.
        if (!IsPureAdmin
            && (role == Roles.Admin
                || role == Roles.ProgramManager
                || role == Roles.Operations))
        {
            errors["Role"] = "Only an administrator can create that role.";
        }

        if (!errors.ContainsKey("Username")
            && await Db.Users.AnyAsync(u => u.Username == username))
        {
            errors["Username"] = "That username is already taken.";
        }
        if (!errors.ContainsKey("Email")
            && await Db.Users.AnyAsync(u => u.Email == email))
        {
            errors["Email"] = "That email is already registered.";
        }

        if (errors.Count > 0)
        {
            ViewBag.FieldErrors = errors;
            TempData.Flash("Please fix the highlighted fields and try again.", "danger");
            return View(vm);
        }

        var user = new User
        {
            Username = username,
            Email = email,
            FullName = fullName,
            EmployeeId = string.IsNullOrEmpty(employeeId) ? null : employeeId,
            BusinessUnit = role == Roles.ProgramManager
                ? null
                : (string.IsNullOrEmpty(businessUnit) ? null : businessUnit),
            BusinessUnits = buMulti,
            RoleLabelId = roleLabelId,
            ManagerId = (role == Roles.Employee || role == Roles.Pm) ? managerId : null,
            Role = role,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = DateTime.UtcNow,
            // Admin-created accounts skip the approval queue.
            Approved = true,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Created {user.FullName} as {role}. They can sign in immediately.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Admin-mediated password reset. Generates a short random temporary
    /// password, hashes it into the user's row, flags the account as
    /// <c>MustChangePassword</c>, and surfaces the plaintext temp password
    /// to the admin via a flash message so they can relay it to the user
    /// out-of-band (Slack / Teams / phone). The user is forced to choose a
    /// new password on their next sign-in.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id)
    {
        if (!await CanViewUserAsync(id))
        {
            TempData.Flash("You don't have access to that user.", "danger");
            return RedirectToAction(nameof(Index));
        }

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == id);
        if (user is null)
        {
            TempData.Flash("That user no longer exists.", "danger");
            return RedirectToAction(nameof(Index));
        }

        // PMs can't reset another admin's password — escalation hazard.
        if (!IsPureAdmin
            && (Roles.IsAdminRole(user.Role) || user.Role == Roles.Operations)
            && user.Id != CurrentUserId)
        {
            TempData.Flash(
                "Only an administrator can reset an elevated account's password.",
                "danger");
            return RedirectToAction(nameof(Index));
        }

        var temp = GenerateTempPassword();
        user.PasswordHash = PasswordHasher.Hash(temp);
        user.MustChangePassword = true;
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Temporary password for {user.FullName} ({user.Username}): "
            + $"\u00AB{temp}\u00BB  —  share it with the user securely. "
            + "They'll be required to choose a new password on their next sign-in.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Builds an easy-to-read random temp password: 10 characters drawn
    /// from an unambiguous alphabet (no 0/O/1/l/I) plus one digit, so it
    /// always passes the "at least one letter and one digit" rule.
    /// </summary>
    private static string GenerateTempPassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        Span<byte> buf = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        var chars = new char[10];
        for (var i = 0; i < 9; i++) chars[i] = letters[buf[i] % letters.Length];
        chars[9] = digits[buf[9] % digits.Length];
        return new string(chars);
    }
}
