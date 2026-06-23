namespace AttendanceMonitoring.Services;

/// <summary>
/// Philippine Standard Time helpers (fixed UTC+8, no DST). All timestamps are
/// stored in UTC; this class is the single source of truth for converting them
/// to PHT for display.
/// </summary>
public static class PhTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        id: "PHT",
        baseUtcOffset: Offset,
        displayName: "(UTC+08:00) Philippine Standard Time",
        standardDisplayName: "PHT");

    public static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Offset);

    public static DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public static DateTimeOffset? ToPh(DateTime? utc)
    {
        if (utc is null) return null;
        var v = utc.Value;
        if (v.Kind == DateTimeKind.Unspecified)
            v = DateTime.SpecifyKind(v, DateTimeKind.Utc);
        return new DateTimeOffset(v).ToOffset(Offset);
    }

    public static string Format(DateTime? utc, string fmt = "yyyy-MM-dd HH:mm")
    {
        var ph = ToPh(utc);
        return ph?.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Render an attendance duration (in minutes) as <c>HH:MM</c> for human
    /// display. The raw minute count is hard to read once it crosses 60
    /// — e.g. <c>510</c> minutes is much clearer as <c>"08:30"</c>.
    /// Returns "00:00" for null / zero / negative inputs.
    /// </summary>
    public static string FormatDurationMinutes(int minutes)
    {
        if (minutes <= 0) return "00:00";
        var h = minutes / 60;
        var m = minutes % 60;
        return $"{h:D2}:{m:D2}";
    }

    public static string FormatDurationMinutes(int? minutes)
        => minutes is null ? string.Empty : FormatDurationMinutes(minutes.Value);

    /// <summary>
    /// Render a duration as "X hour(s) and Y min" for summary labels.
    /// Returns "0 hour and 0 min" for null/zero/negative values.
    /// </summary>
    public static string FormatDurationLong(int minutes)
    {
        if (minutes <= 0) return "0 hour and 0 min";
        var h = minutes / 60;
        var m = minutes % 60;
        var hourLabel = h == 1 ? "hour" : "hours";
        return $"{h} {hourLabel} and {m} min";
    }

    /// <summary>
    /// Render the over/undertime variance for one attendance row, given the
    /// actual minutes worked and the user's scheduled minutes for that day.
    /// Returns "" if the row is still open (no checkout) or there's no
    /// schedule for the weekday. Positive variance &gt;= 1 min ⇒
    /// "+HH:MM OT", negative ⇒ "-HH:MM UT", zero ⇒ "on time".
    /// </summary>
    public static string FormatVariance(int actualMinutes, int scheduledMinutes, bool isOpen)
    {
        if (isOpen) return string.Empty;
        if (scheduledMinutes <= 0) return string.Empty;

        var diff = actualMinutes - scheduledMinutes;
        // Treat ±1 minute as "on time" — clock drift / rounding shouldn't
        // surface as a 1-minute overtime / undertime flag.
        if (Math.Abs(diff) <= 1) return "on time";

        var abs = Math.Abs(diff);
        var h = abs / 60;
        var m = abs % 60;
        var sign = diff > 0 ? "+" : "-";
        var tag = diff > 0 ? "OT" : "UT";
        return $"{sign}{h:D2}:{m:D2} {tag}";
    }

    /// <summary>
    /// Map a <see cref="DateOnly"/> to the 0..6 weekday index used by
    /// <c>ScheduleEntry.Weekday</c> (0 = Monday, 6 = Sunday).
    /// </summary>
    public static int MondayFirstWeekday(DateOnly date)
        => ((int)date.DayOfWeek + 6) % 7;
}
