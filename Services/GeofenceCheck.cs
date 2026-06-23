using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Geofencing helpers. Onsite check-ins should land within a configured
/// site radius — administrators define sites at <c>/sites</c> with a
/// latitude/longitude/radius triple. Offsite shifts (Coalition WFH, IST
/// remote) are not constrained.
/// </summary>
public static class GeofenceCheck
{
    public sealed record Result(bool Ok, Site? Site, double? DistanceMeters, string Status);

    /// <summary>
    /// Validates a check-in coordinate against the active sites for the
    /// user's BU. Returns Ok=true with the matched site if any site's
    /// radius contains the point. Returns Ok=true with Status="NoSites"
    /// when the BU has no configured sites yet (lets early adopters
    /// roll the feature out gradually without breaking existing flows).
    /// </summary>
    public static async Task<Result> EvaluateAsync(
        AppDbContext db, User user, double? lat, double? lng)
    {
        if (lat is null || lng is null)
            return new(false, null, null, "MissingCoords");

        // BU-scoped sites first, fall back to global sites (BusinessUnit = null).
        var sites = await db.Sites
            .Where(s => s.IsActive
                     && (s.BusinessUnit == user.BusinessUnit || s.BusinessUnit == null))
            .ToListAsync();
        if (sites.Count == 0)
            return new(true, null, null, "NoSites");

        Site? best = null;
        double bestDist = double.MaxValue;
        foreach (var s in sites)
        {
            var d = Haversine(lat.Value, lng.Value, s.Latitude, s.Longitude);
            if (d < bestDist) { bestDist = d; best = s; }
        }

        if (best is null) return new(false, null, null, "NoSites");

        var ok = bestDist <= best.RadiusMeters;
        return new(ok, best, bestDist, ok ? "Inside" : "OutsideRadius");
    }

    /// <summary>
    /// Great-circle distance between two WGS-84 coordinates, in metres.
    /// </summary>
    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000.0; // mean Earth radius, metres
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}
