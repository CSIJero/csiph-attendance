using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Thin convenience layer over <see cref="DbInitializer.GetHolidayForAsync"/>.
/// Wrapping the DB lookup here means controllers do not have to take
/// an explicit dependency on <see cref="DbInitializer"/> for what is
/// otherwise a one-line check.
/// </summary>
public static class HolidayHelper
{
    /// <summary>
    /// Returns the matching <see cref="Holiday"/> row for the given user
    /// and date, or null if it's a regular working day.
    /// </summary>
    public static Task<Holiday?> GetAsync(AppDbContext db, User user, DateOnly date)
        => DbInitializer.GetHolidayForAsync(db, user, date);

    /// <summary>Returns true when <paramref name="date"/> is a regular
    /// or special non-working day in the user's country.</summary>
    public static async Task<bool> IsHolidayAsync(AppDbContext db, User user, DateOnly date)
        => (await GetAsync(db, user, date)) is not null;

    /// <summary>Resolves the user's country code from their BU
    /// designation. See <see cref="DbInitializer.CountryFor"/>.</summary>
    public static string CountryFor(User user) => DbInitializer.CountryFor(user);

    /// <summary>
    /// Loads holidays for a date range (used by Reports / dashboards
    /// to flag the holiday rows differently from regular days).
    /// </summary>
    public static async Task<Dictionary<DateOnly, Holiday>> RangeForAsync(
        AppDbContext db, User user, DateOnly fromInclusive, DateOnly toInclusive)
    {
        var country = CountryFor(user);
        var rows = await db.Holidays
            .Where(h => h.Date >= fromInclusive
                     && h.Date <= toInclusive
                     && (h.Country == country || h.Country == "ALL"))
            .ToListAsync();
        // Country-specific overrides any "ALL" entry on the same date.
        return rows
            .GroupBy(h => h.Date)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(h => h.Country == "ALL" ? 1 : 0).First());
    }
}
