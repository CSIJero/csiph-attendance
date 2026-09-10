using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

public static class PresenceTracker
{
    private static readonly HashSet<string> OfflineReasons =
    [
        "logout",
        "locked",
        "page_hidden",
        "browser_closed",
        "heartbeat_timeout",
        "unknown",
    ];

    public static async Task MarkOfflineAsync(
        AppDbContext db,
        User user,
        string? reason,
        DateTime nowUtc,
        bool preserveExistingReason = false,
        CancellationToken cancellationToken = default)
    {
        nowUtc = AsUtc(nowUtc);
        var normalizedReason = NormalizeOfflineReason(reason);
        if (normalizedReason == "page_hidden"
            && user.PresenceState == "offline"
            && user.PresenceReason is "logout" or "locked" or "browser_closed")
        {
            normalizedReason = user.PresenceReason;
        }
        var intervalReason = preserveExistingReason
            ? NormalizeOfflineReason(user.PresenceReason)
            : normalizedReason;

        user.PresenceState = "offline";
        user.OfflineSince ??= nowUtc;
        if (!preserveExistingReason)
        {
            user.PresenceReason = normalizedReason;
        }
        if (normalizedReason == "logout")
        {
            user.LogoutAt = nowUtc;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO presence_intervals ("UserId", "StartedAt", "Reason")
            VALUES ({user.Id}, {nowUtc}, {intervalReason})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);

        if (!preserveExistingReason)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE presence_intervals
                SET "Reason" = {normalizedReason}
                WHERE "UserId" = {user.Id} AND "EndedAt" IS NULL
                """,
                cancellationToken);
        }
    }

    public static async Task MarkPresentAsync(
        AppDbContext db,
        User user,
        string state,
        string reason,
        DateTime nowUtc,
        int offlineThresholdSeconds = Constants.DefaultOnlineThresholdSeconds,
        CancellationToken cancellationToken = default)
    {
        nowUtc = AsUtc(nowUtc);
        var openIntervals = await db.PresenceIntervals
            .Where(interval => interval.UserId == user.Id
                && interval.EndedAt == null)
            .ToListAsync(cancellationToken);

        // If all client signals disappeared, the notifier may not have run
        // before this recovery request. Persist the elapsed outage now so it
        // is still represented in dashboard and historical report totals.
        if (openIntervals.Count == 0
            && user.EffectiveOfflineSince(nowUtc, offlineThresholdSeconds) is { } inferredStart
            && inferredStart < nowUtc)
        {
            var interval = new PresenceInterval
            {
                UserId = user.Id,
                StartedAt = inferredStart,
                EndedAt = nowUtc,
                Reason = string.Equals(
                    user.PresenceState,
                    "offline",
                    StringComparison.OrdinalIgnoreCase)
                        ? NormalizeOfflineReason(user.PresenceReason)
                        : "heartbeat_timeout",
            };
            db.PresenceIntervals.Add(interval);
        }

        foreach (var interval in openIntervals)
        {
            interval.EndedAt = nowUtc < interval.StartedAt
                ? interval.StartedAt
                : nowUtc;
        }

        user.PresenceState = state;
        user.OfflineSince = null;
        user.PresenceReason = reason;
    }

    public static int OfflineSecondsForDate(
        User user,
        DateOnly date,
        IEnumerable<PresenceInterval> intervals,
        DateTime nowUtc,
        IEnumerable<Attendance>? attendances = null,
        int offlineThresholdSeconds = Constants.DefaultOnlineThresholdSeconds)
    {
        var offset = UserClock.OffsetFor(user);
        var localStart = date.ToDateTime(TimeOnly.MinValue);
        var startUtc = DateTime.SpecifyKind(localStart - offset, DateTimeKind.Utc);
        var endUtc = startUtc.AddDays(1);
        nowUtc = AsUtc(nowUtc);
        var attendanceWindows = attendances?
            .Where(attendance => attendance.UserId == user.Id)
            .Select(attendance => (
                Start: AsUtc(attendance.CheckIn),
                End: attendance.CheckOut is { } checkOut
                    ? AsUtc(checkOut)
                    : nowUtc))
            .Where(window => window.End > startUtc && window.Start < endUtc)
            .OrderBy(window => window.Start)
            .ToArray();
        if (attendanceWindows is { Length: > 1 })
        {
            attendanceWindows = MergeWindows(attendanceWindows);
        }

        var userIntervals = intervals
            .Where(interval => interval.UserId == user.Id)
            .ToList();
        var offlineWindows = userIntervals
            .Select(interval =>
            {
                var intervalStart = AsUtc(interval.StartedAt);
                var intervalEnd = interval.EndedAt is { } ended
                    ? AsUtc(ended)
                    : nowUtc;
                return (
                    Start: intervalStart > startUtc ? intervalStart : startUtc,
                    End: intervalEnd < endUtc ? intervalEnd : endUtc);
            })
            .Where(window => window.End > window.Start)
            .OrderBy(window => window.Start)
            .ToList();

        if (!userIntervals.Any(interval => interval.EndedAt == null)
            && user.EffectiveOfflineSince(nowUtc, offlineThresholdSeconds) is { } inferredStart)
        {
            var clippedStart = inferredStart > startUtc ? inferredStart : startUtc;
            var clippedEnd = nowUtc < endUtc ? nowUtc : endUtc;
            if (clippedEnd > clippedStart)
            {
                offlineWindows.Add((clippedStart, clippedEnd));
                offlineWindows.Sort((left, right) => left.Start.CompareTo(right.Start));
            }
        }

        var mergedOfflineWindows = offlineWindows.Count > 1
            ? MergeWindows(offlineWindows)
            : offlineWindows.ToArray();

        double totalSeconds = 0;
        foreach (var offlineWindow in mergedOfflineWindows)
        {
            if (attendanceWindows is null)
            {
                totalSeconds +=
                    (offlineWindow.End - offlineWindow.Start).TotalSeconds;
                continue;
            }
            foreach (var window in attendanceWindows)
            {
                var overlapStart = offlineWindow.Start > window.Start
                    ? offlineWindow.Start
                    : window.Start;
                var overlapEnd = offlineWindow.End < window.End
                    ? offlineWindow.End
                    : window.End;
                if (overlapEnd > overlapStart)
                {
                    totalSeconds += (overlapEnd - overlapStart).TotalSeconds;
                }
            }
        }

        return (int)Math.Floor(Math.Max(0, totalSeconds));
    }

    public static string NormalizeOfflineReason(string? reason)
    {
        var normalized = (reason ?? string.Empty).Trim().ToLowerInvariant();
        return OfflineReasons.Contains(normalized) ? normalized : "unknown";
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static (DateTime Start, DateTime End)[] MergeWindows(
        IReadOnlyList<(DateTime Start, DateTime End)> windows)
    {
        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var window in windows)
        {
            if (merged.Count == 0 || window.Start > merged[^1].End)
            {
                merged.Add(window);
                continue;
            }

            if (window.End > merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, window.End);
            }
        }
        return [.. merged];
    }
}
