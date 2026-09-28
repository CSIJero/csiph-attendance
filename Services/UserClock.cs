using System.Globalization;
using System.Security.Claims;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Per-user local-time helpers. Timestamps are stored in UTC while attendance
/// dates, schedules, and display values use the IANA timezone captured from
/// the browser at login. Legacy users without a captured timezone retain the
/// previous India/PHT business-unit fallback until their next login.
/// </summary>
public static class UserClock
{
    public static readonly TimeSpan PhOffset = TimeSpan.FromHours(8);
    public static readonly TimeSpan InOffset = new(5, 30, 0);
    private static readonly TimeZoneInfo PhZone = ResolveZoneOrFallback(
        "Asia/Manila",
        TimeZoneInfo.CreateCustomTimeZone("PHT", PhOffset, "PHT", "PHT"));
    private static readonly TimeZoneInfo InZone = ResolveZoneOrFallback(
        "Asia/Kolkata",
        TimeZoneInfo.CreateCustomTimeZone("IST", InOffset, "IST", "IST"));

    /// <summary>
    /// Wired from <c>Program.cs</c>. Lets the static helpers reach the
    /// current request's cookies so the formatter can honour the
    /// browser's local timezone (set by a small client-side bootstrap)
    /// without every call site having to pass an HttpContext.
    /// </summary>
    public static IHttpContextAccessor? Accessor { get; set; }

    /// <summary>Name of the cookie the bootstrap JS writes.</summary>
    public const string TimezoneCookieName = "tz";

    /// <summary>
    /// Try to read the viewer's browser timezone from the cookie. The
    /// cookie value is formatted as <c>"&lt;offsetMinutes&gt;|&lt;ianaName&gt;"</c>
    /// (e.g. <c>"480|Asia/Manila"</c>). Returns <c>null</c> when the
    /// cookie is absent or malformed so callers can fall back.
    /// </summary>
    public static (TimeSpan Offset, string Iana)? TryGetBrowserTz()
    {
        var raw = Accessor?.HttpContext?.Request?.Cookies?[TimezoneCookieName];
        return ParseBrowserTzCookie(raw);
    }

    internal static (TimeSpan Offset, string Iana)? ParseBrowserTzCookie(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var pipe = raw.IndexOf('|');
        var offsetPart = pipe < 0 ? raw : raw.Substring(0, pipe);
        var ianaPart = pipe < 0 ? string.Empty : raw.Substring(pipe + 1);
        if (!int.TryParse(offsetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
            return null;
        // Sanity-check: real-world offsets are roughly -12h .. +14h.
        if (minutes < -14 * 60 || minutes > 14 * 60) return null;
        return (TimeSpan.FromMinutes(minutes), ianaPart);
    }

    public static bool IsValidTimeZoneId(string? timeZoneId) =>
        TryFindZone(timeZoneId, out _);

    /// <summary>True when the user is part of an India Business Unit.</summary>
    public static bool IsIndia(User? user)
    {
        var bu = user?.BusinessUnit;
        if (string.IsNullOrWhiteSpace(bu)) return false;
        return bu.IndexOf("(IN)", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The employee's timezone. A validated browser-reported IANA timezone
    /// wins; legacy accounts fall back to their business-unit timezone.
    /// </summary>
    public static TimeZoneInfo ZoneFor(User? user)
    {
        if (TryFindZone(user?.TimeZoneId, out var zone)) return zone;
        return IsIndia(user) ? InZone : PhZone;
    }

    public static TimeSpan OffsetFor(User? user) =>
        ZoneFor(user).GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>
    /// The offset to render timestamps in. When the viewer's browser
    /// has reported its timezone (cookie present) that wins, so every
    /// viewer sees times in their own local zone (#4). Falls back to
    /// the passed user's <see cref="OffsetFor"/> when no cookie is
    /// available (e.g. the very first request, background services).
    /// </summary>
    public static TimeZoneInfo DisplayZone(User? user)
    {
        if (TryFindZone(user?.TimeZoneId, out var userZone)) return userZone;
        if (TryGetBrowserTz() is { Iana.Length: > 0 } browser
            && TryFindZone(browser.Iana, out var browserZone))
        {
            return browserZone;
        }
        return ZoneFor(user);
    }

    public static TimeSpan DisplayOffset(User? user) =>
        DisplayZone(user).GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>
    /// Short timezone abbreviation for display. Uses the browser cookie
    /// when available; falls back to the user's BU-derived label.
    /// </summary>
    public static string Label(User? user)
    {
        var zone = DisplayZone(user);
        var nowUtc = DateTime.UtcNow;
        var offset = zone.GetUtcOffset(nowUtc);
        return BrowserLabelFor(offset, zone.Id, zone.IsDaylightSavingTime(
            TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone)));
    }

    /// <summary>
    /// Friendly short label for a browser-reported timezone. Prefers a
    /// well-known abbreviation when the IANA name matches a common
    /// region; otherwise falls back to <c>"GMT±HH:MM"</c>.
    /// </summary>
    internal static string BrowserLabelFor(
        TimeSpan offset,
        string iana,
        bool isDaylightSaving = false)
    {
        if (!string.IsNullOrEmpty(iana))
        {
            if (KnownAbbreviations.TryGetValue(iana, out var abbreviations))
                return isDaylightSaving
                    ? abbreviations.Daylight
                    : abbreviations.Standard;
        }
        // GMT±HH:MM fallback (whole-minute precision).
        var sign = offset.Ticks >= 0 ? "+" : "-";
        var abs = offset.Duration();
        return $"GMT{sign}{abs.Hours:D2}:{abs.Minutes:D2}";
    }

    private static readonly Dictionary<string, (string Standard, string Daylight)> KnownAbbreviations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Asia/Manila"] = ("PHT", "PHT"),
            ["Asia/Kolkata"] = ("IST", "IST"),
            ["Asia/Calcutta"] = ("IST", "IST"),
            ["Asia/Singapore"] = ("SGT", "SGT"),
            ["Asia/Hong_Kong"] = ("HKT", "HKT"),
            ["Asia/Tokyo"] = ("JST", "JST"),
            ["Asia/Seoul"] = ("KST", "KST"),
            ["Asia/Dubai"] = ("GST", "GST"),
            ["Australia/Sydney"] = ("AEST", "AEDT"),
            ["Europe/London"] = ("GMT", "BST"),
            ["Europe/Paris"] = ("CET", "CEST"),
            ["Europe/Berlin"] = ("CET", "CEST"),
            ["America/New_York"] = ("EST", "EDT"),
            ["America/Chicago"] = ("CST", "CDT"),
            ["America/Denver"] = ("MST", "MDT"),
            ["America/Los_Angeles"] = ("PST", "PDT"),
            ["UTC"] = ("UTC", "UTC"),
            ["Etc/UTC"] = ("UTC", "UTC"),
        };

    /// <summary>Current wall-clock <see cref="DateTimeOffset"/> for the user.</summary>
    public static DateTimeOffset NowFor(User? user)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ZoneFor(user));

    public static DateTimeOffset ToLocal(User? user, DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Unspecified)
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        else if (utc.Kind == DateTimeKind.Local)
            utc = utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), ZoneFor(user));
    }

    /// <summary>Today's calendar date in the user's local zone.</summary>
    public static DateOnly TodayFor(User? user)
        => DateOnly.FromDateTime(NowFor(user).DateTime);

    /// <summary>
    /// Format a UTC timestamp in the user's local time. Empty string when
    /// <paramref name="utc"/> is <c>null</c>.
    /// </summary>
    public static string Format(User? user, DateTime? utc, string fmt = "yyyy-MM-dd HH:mm")
    {
        if (utc is null) return string.Empty;
        var v = utc.Value;
        if (v.Kind == DateTimeKind.Unspecified)
            v = DateTime.SpecifyKind(v, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(v), DisplayZone(user))
            .ToString(fmt, CultureInfo.InvariantCulture);
    }

    public static bool TryConvertLocalToUtc(
        User? user,
        DateTime local,
        out DateTime utc,
        bool useDisplayZone = false)
    {
        var zone = useDisplayZone ? DisplayZone(user) : ZoneFor(user);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }

        utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }

    public static DateTime StartOfDateUtc(User? user, DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            ZoneFor(user));
    }

    private static bool TryFindZone(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = null!;
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out var found))
            return false;
        zone = found;
        return true;
    }

    private static TimeZoneInfo ResolveZoneOrFallback(
        string timeZoneId,
        TimeZoneInfo fallback) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone)
            ? zone
            : fallback;

    /// <summary>
    /// Razor convenience: look up the signed-in user from the request
    /// once and cache the result in <see cref="HttpContext.Items"/> so
    /// repeated calls inside the same view don't hit the database.
    /// Returns <c>null</c> when the request is unauthenticated.
    /// </summary>
    private const string ItemsKey = "__UserClock.ViewerUser";

    public static User? Resolve(HttpContext? ctx, AppDbContext db)
    {
        if (ctx is null) return null;
        if (ctx.Items.TryGetValue(ItemsKey, out var cached))
            return cached as User;

        User? user = null;
        var idStr = ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(idStr, out var id))
        {
            user = db.Users.AsNoTracking().FirstOrDefault(u => u.Id == id);
        }
        ctx.Items[ItemsKey] = user;
        return user;
    }
}
