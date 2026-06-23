namespace AttendanceMonitoring.Models;

public static class Constants
{
    /// <summary>Mon-first weekday names matching Python's <c>date.weekday()</c>.</summary>
    public static readonly string[] WeekdayNames =
    {
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday",
    };

    /// <summary>How many seconds since last heartbeat counts as "online".</summary>
    public const int DefaultOnlineThresholdSeconds = 60;

    /// <summary>Lunch breaks auto-expire after this many minutes.</summary>
    public const int LunchBreakMinutes = 60;

    /// <summary>Short breaks (morning / afternoon) auto-expire after this many minutes.</summary>
    public const int ShortBreakMinutes = 15;

    /// <summary>How many short breaks an employee may take per work-day, in addition to lunch.</summary>
    public const int MaxShortBreaksPerDay = 2;

    /// <summary>How many lunches an employee may take per work-day. Currently a single 1-hour lunch.</summary>
    public const int MaxLunchesPerDay = 1;

    /// <summary>
    /// Maximum Hamming distance (0..64) between an enrolled face hash
    /// and a check-in selfie's hash before the check-in is flagged as
    /// a possible spoof. Lower = stricter. 14 is empirically a good
    /// fit for an 8x8 average-hash on indoor lighting variation.
    /// </summary>
    public const int FaceMatchMaxDistance = 14;

    /// <summary>
    /// Picklist of allowed activity tags shown on the check-in form.
    /// Drives the <see cref="Attendance.ActivityTag"/> dropdown.
    /// Free-text is also allowed by the controller so on-call tasks
    /// outside this list can still be captured.
    /// </summary>
    public static readonly string[] ActivityTags =
    {
        "Development",
        "Meetings",
        "Client Support",
        "Training",
        "Operations",
        "Administrative",
        "Other",
    };

    /// <summary>
    /// Business Unit picklist shown on the Register / Users Create / Users
    /// Edit forms. BU2/BU3/BU4 are split by country (PH vs IN) so PM
    /// scoping (see <c>AppController.GetVisibleUsersAsync</c>) keeps the two
    /// regions separate. BU1 is currently a single global unit.
    ///
    /// Country codes follow ISO-3166-1 alpha-2:
    ///   Philippines = PH
    ///   India       = IN
    /// </summary>
    public static readonly string[] BusinessUnits =
    {
        "BU1",
        "BU2 (PH)",
        "BU2 (IN)",
        "BU3 (PH)",
        "BU3 (IN)",
        "BU4 (PH)",
        "BU4 (IN)",
    };

    /// <summary>
    /// Map a legacy / loosely-formed BU string onto the canonical picklist
    /// value. Currently rewrites the historical "India" suffix to the
    /// ISO "IN" code so old DB rows display the new label without breaking
    /// PM-scoping lookups.
    /// </summary>
    public static string? NormaliseBusinessUnit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        // "BU2 (India)" -> "BU2 (IN)" etc.
        v = System.Text.RegularExpressions.Regex.Replace(
            v, @"\(India\)", "(IN)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return v;
    }

    /// <summary>True when the given string matches one of <see cref="BusinessUnits"/>.</summary>
    public static bool IsKnownBusinessUnit(string? value)
    {
        var v = NormaliseBusinessUnit(value);
        if (string.IsNullOrWhiteSpace(v)) return false;
        foreach (var b in BusinessUnits)
        {
            if (string.Equals(b, v, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
