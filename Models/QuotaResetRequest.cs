using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// An employee-submitted request to reset their daily break / lunch
/// counter to zero so they can take another one. Requires approval from
/// any admin-tier user (Admin, Program Manager, or Project Manager).
/// One pending request per (user, kind) is enforced in the controller —
/// duplicates are short-circuited with a flash message.
/// </summary>
public class QuotaResetRequest
{
    public int Id { get; set; }

    /// <summary>The employee whose counter is being reset.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Who submitted the row (= UserId unless an admin filed it).</summary>
    public int RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"break" or "lunch" — which daily counter to reset.</summary>
    [Required, MaxLength(8)]
    public string Kind { get; set; } = "break";

    /// <summary>
    /// PHT calendar date the reset is targeted at. Approval is a no-op
    /// (still marks the request decided) if the user's
    /// <c>BreaksUsedDate</c> / <c>LunchesUsedDate</c> has already rolled
    /// past this date, since the counter has auto-reset itself.
    /// </summary>
    public DateOnly TargetDate { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    // ---- Decision ------------------------------------------------------

    /// <summary>"Pending" | "Approved" | "Rejected".</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "Pending";

    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    public DateTime? DecidedAt { get; set; }

    [MaxLength(500)]
    public string? DecisionNote { get; set; }
}
