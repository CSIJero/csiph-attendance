using System.Globalization;
using System.Security.Claims;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Per-user local-time helpers. Most data in the system is stored in UTC
/// and displayed in PHT (UTC+8), but users based in India work in IST
/// (UTC+5:30) and expect the dashboard / shift-evaluation logic to use
/// their wall clock. Region is inferred from the user's
/// <see cref="User.BusinessUnit"/> column: any value carrying the
/// <c>(IN)</c> ISO-3166 alpha-2 suffix (e.g. <c>"BU2 (IN)"</c>) is
/// treated as India; everything else falls back to PHT.
/// </summary>
public static class UserClock
{
    public static readonly TimeSpan PhOffset = TimeSpan.FromHours(8);
    public static readonly TimeSpan InOffset = new(5, 30, 0);

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

    /// <summary>True when the user is part of an India Business Unit.</summary>
    public static bool IsIndia(User? user)
    {
        var bu = user?.BusinessUnit;
        if (string.IsNullOrWhiteSpace(bu)) return false;
        return bu.IndexOf("(IN)", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// The user's logical local UTC offset — derived strictly from their
    /// <see cref="User.BusinessUnit"/>. Used by server-side logic that
    /// must always evaluate against the employee's actual wall clock
    /// (shift cut-offs, late-arrival checks, the offline notifier),
    /// regardless of who is viewing.
    /// </summary>
    public static TimeSpan OffsetFor(User? user)
        => IsIndia(user) ? InOffset : PhOffset;

    /// <summary>
    /// The offset to render timestamps in. When the viewer's browser
    /// has reported its timezone (cookie present) that wins, so every
    /// viewer sees times in their own local zone (#4). Falls back to
    /// the passed user's <see cref="OffsetFor"/> when no cookie is
    /// available (e.g. the very first request, background services).
    /// </summary>
    public static TimeSpan DisplayOffset(User? user)
    {
        if (TryGetBrowserTz() is { } b) return b.Offset;
        return OffsetFor(user);
    }

    /// <summary>
    /// Short timezone abbreviation for display. Uses the browser cookie
    /// when available; falls back to the user's BU-derived label.
    /// </summary>
    public static string Label(User? user)
    {
        if (TryGetBrowserTz() is { } b)
        {
            return BrowserLabelFor(b.Offset, b.Iana);
        }
        return IsIndia(user) ? "IST" : "PHT";
    }

    /// <summary>
    /// Friendly short label for a browser-reported timezone. Prefers a
    /// well-known abbreviation when the IANA name matches a common
    /// region; otherwise falls back to <c>"GMT±HH:MM"</c>.
    /// </summary>
    internal static string BrowserLabelFor(TimeSpan offset, string iana)
    {
        if (!string.IsNullOrEmpty(iana))
        {
            if (KnownAbbreviations.TryGetValue(iana, out var abbr)) return abbr;
        }
        // GMT±HH:MM fallback (whole-minute precision).
        var sign = offset.Ticks >= 0 ? "+" : "-";
        var abs = offset.Duration();
        return $"GMT{sign}{abs.Hours:D2}:{abs.Minutes:D2}";
    }

    private static readonly Dictionary<string, string> KnownAbbreviations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Asia/Manila"]    = "PHT",
            ["Asia/Kolkata"]   = "IST",
            ["Asia/Calcutta"]  = "IST",
            ["Asia/Singapore"] = "SGT",
            ["Asia/Hong_Kong"] = "HKT",
            ["Asia/Tokyo"]     = "JST",
            ["Asia/Seoul"]     = "KST",
            ["Asia/Dubai"]     = "GST",
            ["Australia/Sydney"] = "AEDT",
            ["Europe/London"]  = "GMT",
            ["Europe/Paris"]   = "CET",
            ["Europe/Berlin"]  = "CET",
            ["America/New_York"] = "EST",
            ["America/Chicago"]  = "CST",
            ["America/Denver"]   = "MST",
            ["America/Los_Angeles"] = "PST",
            ["UTC"]            = "UTC",
        };

    /// <summary>Current wall-clock <see cref="DateTimeOffset"/> for the user.</summary>
    public static DateTimeOffset NowFor(User? user)
        => DateTimeOffset.UtcNow.ToOffset(OffsetFor(user));

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
        return new DateTimeOffset(v)
            .ToOffset(DisplayOffset(user))
            .ToString(fmt, CultureInfo.InvariantCulture);
    }

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
