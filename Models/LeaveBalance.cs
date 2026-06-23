using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

/// <summary>
/// Running balance of accruable leave per user and leave type. The
/// nightly <c>LeaveAccrualService</c> tops these up; approving a
/// <see cref="LeaveRequest"/> decrements them. Negative balances are
/// allowed (some companies let employees borrow against future accrual)
/// — the UI surfaces them with a warning badge.
/// </summary>
public class LeaveBalance
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>
    /// Leave classification: <c>"Vacation"</c>, <c>"Sick"</c>, etc.
    /// Free-form string keyed against the company's leave-policy
    /// configuration. Stored as a short uppercase tag so the UI can
    /// switch on it without normalising every read.
    /// </summary>
    [Required, MaxLength(32)]
    public string LeaveType { get; set; } = "Vacation";

    /// <summary>Hours currently available to spend on this leave type.</summary>
    [Column(TypeName = "numeric(8,2)")]
    public decimal HoursRemaining { get; set; }

    /// <summary>Total hours accrued YTD (for reporting / annual reset).</summary>
    [Column(TypeName = "numeric(8,2)")]
    public decimal HoursAccruedYtd { get; set; }

    /// <summary>Total hours used YTD (sum of approved LeaveRequest hours that consumed this balance).</summary>
    [Column(TypeName = "numeric(8,2)")]
    public decimal HoursUsedYtd { get; set; }

    /// <summary>Last calendar month an accrual posted (yyyy-MM-01). Drives idempotency for the nightly job.</summary>
    public DateOnly? LastAccrualMonth { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
