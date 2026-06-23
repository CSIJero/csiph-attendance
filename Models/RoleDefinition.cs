using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// Admin-managed role catalogue. Each row is a role that can be assigned
/// to a user from the Users → Create / Edit / Approve screens. The page
/// is served from the Configuration tab.
/// <para>
/// <see cref="Name"/> is the human-readable label shown in the Role
/// dropdown (e.g. "Project Manager", "Senior Engineer"). <see cref="BaseRole"/>
/// is the canonical permission tier that authorization decisions hinge on
/// (one of <see cref="Roles.Admin"/>, <see cref="Roles.ProgramManager"/>,
/// <see cref="Roles.Pm"/> or <see cref="Roles.Employee"/>).
/// </para>
/// <para>
/// Four built-in rows are seeded by <c>DbInitializer</c> and marked
/// <see cref="IsBuiltIn"/> so the admin can't accidentally delete them
/// out from under existing users.
/// </para>
/// </summary>
public class RoleDefinition
{
    public int Id { get; set; }

    /// <summary>Human-readable label shown in the Role dropdown
    /// (e.g. "Project Manager", "Senior Engineer", "QA Lead").</summary>
    [Required, MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Canonical permission tier this role inherits from. Stored as a
    /// lowercase token (admin / program_manager / pm / employee) so it
    /// matches <see cref="User.Role"/> and the
    /// <see cref="Roles"/> constants directly. Defaults to
    /// <see cref="Roles.Employee"/> so custom rows never accidentally
    /// grant elevated access.
    /// </summary>
    [Required, MaxLength(32)]
    public string BaseRole { get; set; } = Roles.Employee;

    /// <summary>
    /// True for the four canonical seed rows (Administrator, Program
    /// Manager, Project Manager, Employee). Built-in rows can't be
    /// deleted from the Configuration page so we never end up with a
    /// permission tier that has no role definition pointing at it.
    /// </summary>
    public bool IsBuiltIn { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

