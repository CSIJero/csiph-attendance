using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// Audit row for an attempted offline-during-shift alert email so admins
/// can verify deliveries without reading the application console.
/// </summary>
public class NotificationLog
{
    public int Id { get; set; }

    /// <summary>UTC time the send was attempted.</summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Warning" or "Deduction".</summary>
    [Required, MaxLength(20)]
    public string Level { get; set; } = string.Empty;

    /// <summary>"Sent" | "Failed" | "Disabled" | "NoRecipients".</summary>
    [Required, MaxLength(20)]
    public string Status { get; set; } = string.Empty;

    public int? UserId { get; set; }

    [MaxLength(120)]
    public string? UserFullName { get; set; }

    [MaxLength(64)]
    public string? Username { get; set; }

    /// <summary>Comma-separated recipients (truncated if very long).</summary>
    [MaxLength(1024)]
    public string Recipients { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>How long the user had been offline at send time (minutes).</summary>
    public int OfflineMinutes { get; set; }

    /// <summary>Set when <see cref="Status"/> is "Failed".</summary>
    [MaxLength(2048)]
    public string? ErrorMessage { get; set; }

    // ------------------------------------------------------------------
    // Admin enforcement (Step 9). Each notification_log row is one
    // attempted offline-during-shift alert — i.e. one detected violation.
    // The fields below let an admin record what enforcement action (if
    // any) was taken in response, without losing the original alert.
    // ------------------------------------------------------------------

    /// <summary>
    /// Enforcement decision: "Warning", "Deduction", "Overridden", or
    /// null when no action has been taken yet (i.e. still pending review).
    /// </summary>
    [MaxLength(20)]
    public string? ActionTaken { get; set; }

    /// <summary>Admin who recorded the enforcement action.</summary>
    public int? ActionByUserId { get; set; }
    public User? ActionByUser { get; set; }

    /// <summary>UTC time the enforcement action was recorded.</summary>
    public DateTime? ActionAt { get; set; }

    /// <summary>Free-form note from the admin (reason, severity, etc.).</summary>
    [MaxLength(500)]
    public string? ActionNote { get; set; }
}
