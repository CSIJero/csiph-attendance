using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// A statutory / company holiday for a given country. Used by
/// <see cref="Services.LateCheck"/>, the daily report, and the
/// schedule editor to suppress late-arrival evaluation and
/// required-render-hour guards on the affected date.
/// </summary>
public class Holiday
{
    public int Id { get; set; }

    /// <summary>Calendar date the holiday falls on (local to <see cref="Country"/>).</summary>
    public DateOnly Date { get; set; }

    /// <summary>
    /// ISO-3166-1 alpha-2 country code: <c>"PH"</c> or <c>"IN"</c>. A
    /// user is considered to be on holiday when their derived country
    /// (from <see cref="User.BusinessUnit"/>) matches this value.
    /// <c>"ALL"</c> means the holiday applies to every country.
    /// </summary>
    [Required, MaxLength(8)]
    public string Country { get; set; } = "PH";

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// "Regular" (paid, full rate) or "Special" (special non-working, premium rate).
    /// Currently informational only — the late / render-hours guards
    /// treat both as non-working days.
    /// </summary>
    [MaxLength(20)]
    public string Kind { get; set; } = "Regular";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
