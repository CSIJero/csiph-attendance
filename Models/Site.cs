using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// A physical office / work site with a GPS centre + permitted radius.
/// On check-in, an Onsite shift validates the user's reported coords
/// against every site whose <see cref="BusinessUnit"/> matches (or is
/// global, <c>BusinessUnit = null</c>). When at least one site is
/// configured and the user is outside the radius of all of them,
/// the check-in is rejected unless they have an Offsite schedule.
/// </summary>
public class Site
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>WGS-84 latitude in decimal degrees (range -90..+90).</summary>
    public double Latitude { get; set; }

    /// <summary>WGS-84 longitude in decimal degrees (range -180..+180).</summary>
    public double Longitude { get; set; }

    /// <summary>Permitted distance from the centre, in metres. Typical office: 150 m.</summary>
    public int RadiusMeters { get; set; } = 150;

    /// <summary>
    /// Restrict the site to a specific Business Unit (e.g. <c>"BU2 (PH)"</c>).
    /// Null = site applies to every BU. Matches against
    /// <see cref="User.BusinessUnit"/> via canonicalised string compare.
    /// </summary>
    [MaxLength(120)]
    public string? BusinessUnit { get; set; }

    /// <summary>Free-form notes (address, building/floor). Not validated.</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>Soft-disable a site without losing the row. Disabled sites are ignored by check-in validation.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
