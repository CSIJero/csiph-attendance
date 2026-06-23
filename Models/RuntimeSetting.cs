using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// Simple key/value runtime configuration persisted in the app database.
/// Used for admin-tunable settings that should take effect immediately
/// without editing deployment files or restarting the app.
/// </summary>
public class RuntimeSetting
{
    public int Id { get; set; }

    [Required, MaxLength(80)]
    public string Key { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
