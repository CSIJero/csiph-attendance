using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

/// <summary>
/// An employee-submitted request to override a single attendance row's
/// CheckIn / CheckOut / WorkDate. Requests need PM or admin approval
/// before they're applied to the live <see cref="Attendance"/> row.
/// PMs and admins who edit the row directly via the existing edit form
/// bypass this workflow entirely.
/// </summary>
public class AttendanceEditRequest
{
    public int Id { get; set; }

    /// <summary>The attendance row the employee wants to change.</summary>
    public int AttendanceId { get; set; }
    public Attendance? Attendance { get; set; }

    /// <summary>The employee who filed the request (usually = Attendance.UserId).</summary>
    public int RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    // ---- The change being proposed -------------------------------------
    // All three are stored "as proposed". Apply on approval copies them
    // verbatim onto the Attendance row.

    public DateOnly RequestedWorkDate { get; set; }

    public DateTime RequestedCheckIn { get; set; }

    /// <summary>Optional — empty leaves the row open after approval.</summary>
    public DateTime? RequestedCheckOut { get; set; }

    /// <summary>Free-text justification. Required when submitted by employees.</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Evidence image (data URL) attached by the requester as proof for
    /// time adjustment requests.
    /// </summary>
    public string? ProofPhoto { get; set; }

    // ---- Decision ------------------------------------------------------

    /// <summary>"Pending" | "Approved" | "Rejected".</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "Pending";

    /// <summary>Admin / PM who decided (or the requester themselves if a PM).</summary>
    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    public DateTime? DecidedAt { get; set; }

    /// <summary>Optional reviewer note shown to the requester.</summary>
    [MaxLength(500)]
    public string? DecisionNote { get; set; }
}
