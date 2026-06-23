using AttendanceMonitoring.Models;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Centralised late-arrival policy. Combines the user's region (PH vs.
/// India, derived from <see cref="UserClock.IsIndia"/>) and the
/// schedule row's working type (Onsite / Offsite / Dayoff) to decide
/// whether a clock-in is on time, late, or not applicable.
/// <para>
/// Grace rules (admin-configurable from Settings):
///   PH Onsite  : defaults to 30 minutes
///   PH Offsite : defaults to 0 minutes
///   India *    : defaults to 60 minutes
///   Dayoff     : not applicable (no clock-in expected)
///   admin role : not applicable (admins don't render a shift)
/// </para>
/// <para>
/// The rule applies to every other role &#8212; Employee, Project Manager,
/// Program Manager, and Support users included. Support previously had
/// a blanket exemption but is now evaluated against whatever schedule
/// row is on file (rows without a StartTime still fall through to
/// <see cref="NotApplicable"/>).
/// </para>
/// </summary>
public static class LateCheck
{
    public enum LateStatus
    {
        /// <summary>Not on a working day / Support user / unknown — don't surface a badge.</summary>
        NotApplicable = 0,
        OnTime = 1,
        Late = 2,
    }

    public sealed record Result(
        LateStatus Status,
        int LateMinutes,
        int GraceMinutes,
        string WorkType);

    public static readonly Result NotApplicable =
        new(LateStatus.NotApplicable, 0, 0, "Unknown");

    /// <summary>
    /// Evaluate a single attendance row against its scheduled shift.
    /// Returns <see cref="NotApplicable"/> for support users, day-off
    /// schedules, or missing data so callers can suppress the badge.
    /// </summary>
    /// <param name="isHoliday">
    /// When true, the row falls on a statutory holiday for the user's
    /// country. The late-arrival rule is suppressed (counts as
    /// <see cref="NotApplicable"/>) because a holiday clock-in is
    /// either voluntary or on-call coverage, not a missed shift start.
    /// Callers without holiday context may safely pass false.
    /// </param>
    public static Result Evaluate(
        User user,
        ScheduleEntry? schedule,
        DateTime checkInUtc,
        bool isHoliday = false)
    {
        // Policy: late-arrival rules apply to every role *except* admin.
        // Pure admins don't clock attendance through this app, so any row
        // that does exist on their account shouldn't surface a Late badge.
        // Project Managers, Program Managers, Employees and Support users
        // are all evaluated against their scheduled shift.
        if (Roles.Admin.Equals(user.Role, StringComparison.OrdinalIgnoreCase))
        {
            return NotApplicable;
        }

        if (isHoliday)
        {
            return NotApplicable;
        }

        if (schedule is null
            || !schedule.IsWorking
            || schedule.StartTime is null)
        {
            return NotApplicable;
        }

        var workType = schedule.EffectiveWorkType;
        if (ScheduleEntry.IsNonWorkingType(workType))
        {
            return NotApplicable;
        }

        var grace = GraceMinutes(user, workType);

        // Compare on the user's wall clock so India users get evaluated
        // against IST, not PHT.
        var ci = checkInUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(checkInUtc, DateTimeKind.Utc)
            : checkInUtc.ToUniversalTime();
        var local = new DateTimeOffset(ci).ToOffset(UserClock.OffsetFor(user));
        var start = schedule.StartTime.Value;

        // Anchor lateness to the schedule's calendar date in the employee's
        // local timezone. This avoids false 23h+ "late" results when a row
        // is posted near midnight for the upcoming date (e.g. 23:47 for a
        // 00:00 shift on the next work date).
        var scheduledStartLocal = schedule.WorkDate.ToDateTime(start);
        var diffMinutes = (local.DateTime - scheduledStartLocal).TotalMinutes;

        // Midnight-start shifts can be checked in from the previous night's
        // final minutes. If the row ended up attached to the same calendar
        // date, the raw diff appears as ~23h late (e.g. 23:47 vs 00:00).
        // Normalize that shape into the intended pre-shift window.
        var arrivalMinutes = local.DateTime.Hour * 60 + local.DateTime.Minute;
        var startMinutes = start.Hour * 60 + start.Minute;
        if (startMinutes <= 3 * 60
            && arrivalMinutes >= 18 * 60
            && diffMinutes > 12 * 60)
        {
            diffMinutes -= 24 * 60;
        }

        if (diffMinutes <= grace)
        {
            return new Result(LateStatus.OnTime, 0, grace, workType);
        }

        var lateBy = (int)Math.Ceiling(diffMinutes - grace);
        return new Result(LateStatus.Late, lateBy, grace, workType);
    }

    /// <summary>How many minutes of grace apply for this user / work type.</summary>
    public static int GraceMinutes(User user, string workType)
    {
        if (UserClock.IsIndia(user)) return RuntimeConfig.GraceIndia;
        // PH Offsite: no grace period, must be on time
        if (workType.Equals("Offsite", StringComparison.OrdinalIgnoreCase)) return RuntimeConfig.GraceOffsitePh;
        // PH Onsite: grace period is runtime-configurable
        return workType.Equals("Onsite", StringComparison.OrdinalIgnoreCase) ? RuntimeConfig.GraceOnsitePh : 0;
    }

    /// <summary>
    /// Required render hours before a user is allowed to check out. PMs
    /// and Employees are expected to render 9 hours; Support users —
    /// who do not have a fixed daily shift length — are pegged at 8.
    /// </summary>
    public static int RequiredRenderHours(User user)
        => user.IsSupport ? 8 : 9;
}
