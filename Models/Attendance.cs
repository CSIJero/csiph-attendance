using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AttendanceMonitoring.Models;

public class Attendance
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public DateOnly WorkDate { get; set; }

    public DateTime CheckIn { get; set; } = DateTime.UtcNow;

    public DateTime? CheckOut { get; set; }

    /// <summary>
    /// JPEG selfie captured at check-in, stored as a data URL
    /// ("data:image/jpeg;base64,...") so it round-trips into &lt;img src&gt;
    /// untouched. Nullable so legacy rows (and check-ins where the camera
    /// wasn't allowed) keep working.
    /// </summary>
    public string? CheckInPhoto { get; set; }

    /// <summary>JPEG selfie captured at check-out, same shape as <see cref="CheckInPhoto"/>.</summary>
    public string? CheckOutPhoto { get; set; }

    /// <summary>
    /// Free-form activity tag selected at check-in (e.g. "Development",
    /// "Meetings", "Client Support", "Training"). Drives report-level
    /// drill-downs and lets the company bill / cost-centre time without
    /// a separate timesheet system. Null = legacy row.
    /// </summary>
    [MaxLength(64)]
    public string? ActivityTag { get; set; }

    /// <summary>WGS-84 latitude reported by the browser at check-in. Null when GPS was unavailable.</summary>
    public double? CheckInLatitude { get; set; }

    /// <summary>WGS-84 longitude reported by the browser at check-in.</summary>
    public double? CheckInLongitude { get; set; }

    /// <summary>Reported GPS accuracy in metres (browser-side estimate).</summary>
    public double? CheckInAccuracy { get; set; }

    /// <summary>
    /// FK into <see cref="Site"/> when the check-in was validated against
    /// a configured geofence. Null when no sites are configured, the
    /// check-in was Offsite, or the GPS was outside every site (and an
    /// admin allowed the override).
    /// </summary>
    public int? CheckInSiteId { get; set; }

    /// <summary>
    /// Result of the face-match comparison against
    /// <see cref="User.FaceHash"/>: <c>"Match"</c>, <c>"Mismatch"</c>,
    /// or <c>"NotEnrolled"</c>. Null = legacy row.
    /// </summary>
    [MaxLength(16)]
    public string? FaceMatchStatus { get; set; }

    /// <summary>Hamming distance between enrolled and check-in face hashes (0..64).</summary>
    public int? FaceMatchDistance { get; set; }

    [NotMapped]
    public int DurationMinutes
    {
        get
        {
            var ci = DateTime.SpecifyKind(CheckIn, DateTimeKind.Utc);
            DateTime co;
            if (CheckOut is null)
            {
                // Preserve legitimate overnight duration while retaining a
                // defensive ceiling until the notifier auto-closes the row.
                var now = DateTime.UtcNow;
                var cutoff = ci.AddHours(24);
                co = now < cutoff ? now : cutoff;
            }
            else
            {
                // User-recorded check-out is authoritative. Do NOT trim it
                // here — retroactively clamping a legitimate check-out
                // (e.g. anything after 23:30 PHT) would silently shave
                // worked minutes off the displayed total.
                co = DateTime.SpecifyKind(CheckOut.Value, DateTimeKind.Utc);
            }
            if (co <= ci) return 0;
            return (int)Math.Round(
                (co - ci).TotalMinutes,
                MidpointRounding.AwayFromZero);
        }
    }

    [NotMapped]
    public bool IsOpen => CheckOut is null;
}
