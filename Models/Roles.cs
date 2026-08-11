namespace AttendanceMonitoring.Models;

/// <summary>
/// Canonical role names used for authorization and role checks.
/// <c>admin</c>, <c>program_manager</c> and <c>pm</c> (Project Manager)
/// grant administrative access; <c>operations</c> has a separate read-heavy
/// permission set for dashboards, reports, requests, reminders, and holidays.
/// </summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string ProgramManager = "program_manager";
    public const string Pm = "pm";
    public const string Operations = "operations";
    public const string Employee = "employee";

    /// <summary>Roles that have administrative access (any tier).</summary>
    public static readonly string[] AdminRoles = { Admin, ProgramManager, Pm };

    public static bool IsAdminRole(string? role) =>
        string.Equals(role, Admin, System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, ProgramManager, System.StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, Pm, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalises any incoming role string to one of the five canonical values.</summary>
    public static string Normalise(string? role)
    {
        var lower = (role ?? string.Empty).Trim().ToLowerInvariant();
        return lower switch
        {
            Admin => Admin,
            ProgramManager => ProgramManager,
            "programmanager" => ProgramManager, // tolerate no-underscore spelling
            "program manager" => ProgramManager, // tolerate spaced spelling
            Pm => Pm,
            Operations => Operations,
            "operation" => Operations,
            _ => Employee,
        };
    }

    public static string DisplayName(string? role) => (role ?? string.Empty).ToLowerInvariant() switch
    {
        Admin => "Administrator",
        ProgramManager => "Program Manager",
        Pm => "Project Manager",
        Operations => "Operations",
        _ => "Employee",
    };
}
