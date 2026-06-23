using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

/// <summary>
/// An employee-submitted request to modify a single calendar-date row of
/// their schedule (e.g. shift 2026-05-18's 09:00–18:00 to 10:00–19:00, or
/// mark that date as a non-working day). A PM or admin approves the row
/// before the change is applied to the live <see cref="ScheduleEntry"/>.
/// Support users edit their own schedule directly and do not need to use
/// this workflow.
/// </summary>
public class ScheduleAmendment
{
    public int Id { get; set; }

    /// <summary>The user whose schedule is being amended.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Who actually submitted the request. Almost always equals
    /// <see cref="UserId"/>, but kept separate so an admin can file a
    /// request on someone's behalf in the future.
    /// </summary>
    public int RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The Philippine-time calendar date this amendment targets.</summary>
    public DateOnly WorkDate { get; set; }

    // ---- Proposed values ------------------------------------------------
    // Stored as-proposed; applied verbatim to the ScheduleEntry on approval.

    public bool ProposedIsWorking { get; set; }

    public TimeOnly? ProposedStartTime { get; set; }

    public TimeOnly? ProposedEndTime { get; set; }

    [MaxLength(200)]
    public string? ProposedNote { get; set; }

    /// <summary>Why the employee wants the change. Required on submit.</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Base-64 data-URL image proof for the shift-change request.</summary>
    public string? ProofPhoto { get; set; }

    // ---- Decision -------------------------------------------------------

    /// <summary>"Pending" | "Approved" | "Rejected" | "Cancelled".</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "Pending";

    public int? DecidedByUserId { get; set; }
    public User? DecidedByUser { get; set; }

    public DateTime? DecidedAt { get; set; }

    [MaxLength(500)]
    public string? DecisionNote { get; set; }

    [NotMapped]
    public int Weekday => ((int)WorkDate.DayOfWeek + 6) % 7;

    [NotMapped]
    public string WeekdayName => Constants.WeekdayNames[Weekday];

    /// <summary>Human-readable label, e.g. "Mon, May 18, 2026".</summary>
    [NotMapped]
    public string WorkDateLabel => WorkDate.ToString("ddd, MMM d, yyyy");

    [NotMapped]
    public double ProposedHours
    {
        get
        {
            if (!ProposedIsWorking || ProposedStartTime is null || ProposedEndTime is null) return 0.0;
            var startMin = ProposedStartTime.Value.Hour * 60 + ProposedStartTime.Value.Minute;
            var endMin = ProposedEndTime.Value.Hour * 60 + ProposedEndTime.Value.Minute;
            var diff = endMin - startMin;
            // Cross-midnight: end <= start wraps to the next day.
            if (diff <= 0) diff += 24 * 60;
            return Math.Round(diff / 60.0, 2);
        }
    }
}
