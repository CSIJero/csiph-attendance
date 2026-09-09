using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// One continuous period during which the employee was explicitly offline.
/// Open intervals have no <see cref="EndedAt"/> and are closed by the next
/// successful online heartbeat.
/// </summary>
public class PresenceInterval
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    [Required, MaxLength(32)]
    public string Reason { get; set; } = "unknown";
}
