using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

/// <summary>
/// An employee-submitted leave-of-absence request covering one or more
/// work days. PM or admin approval is required before the leave is
/// considered accepted. PMs / admins can also file leave on behalf of a
/// teammate using the same form.
/// </summary>
public class LeaveRequest
{
    public int Id { get; set; }

    /// <summary>The employee the leave applies to.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Who actually submitted the row (= UserId unless a PM filed it).</summary>
    public int RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    // ---- The leave window ---------------------------------------------
    // Full-day requests use StartDate..EndDate inclusive (and conventionally
    // run from start-of-day to end-of-day). Half-day and Undertime requests
    // must fall on a single calendar day -- the time component pinpoints
    // when on that day the worker will be out.

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    /// <summary>"Full" (8 h/day), "HalfDay" (4 h), or "Undertime" (custom).</summary>
    [Required, MaxLength(16)]
    public string LeaveType { get; set; } = "Full";

    /// <summary>
    /// Hours of leave **per day** in the range. Full ⇒ 8, HalfDay ⇒ 4,
    /// Undertime ⇒ the value the employee typed (0.5 .. 8).
    /// </summary>
    public decimal HoursPerDay { get; set; } = 8m;

    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Base-64 data-URL image proof (e.g. medical certificate).</summary>
    public string? ProofPhoto { get; set; }

    // ---- Decision ------------------------------------------------------

    /// <summary>"Pending" | "Approved" | "Rejected" | "Cancelled".</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "Pending";

    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    public DateTime? DecidedAt { get; set; }

    [MaxLength(500)]
    public string? DecisionNote { get; set; }

    // ---- Convenience accessors ----------------------------------------

    [NotMapped]
    public int DayCount =>
        EndDate.Date >= StartDate.Date
            ? (EndDate.Date - StartDate.Date).Days + 1
            : 0;

    [NotMapped]
    public decimal TotalHours => DayCount * HoursPerDay;
}
