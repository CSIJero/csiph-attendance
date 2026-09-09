using System.Collections.Concurrent;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AttendanceMonitoring.Services;

public class OfflineNotifierService : BackgroundService
{
    private enum AlertLevel
    {
        None = 0,
        Warning = 1,
        Deduction = 2,
        ForgotCheckout = 3,
        ShiftReminder = 4,
        RequiredLogout = 5,
        Late = 6,
        MissedCheckIn = 7,
    }

    private sealed class ShiftAlertState
    {
        public DateTime? LastWarningUtc { get; set; }
        public bool DeductionSent { get; set; }
        public bool ForgotCheckoutSent { get; set; }
        public bool LateAlertSent { get; set; }
    }

    private sealed record ManagerRecipient(
        int Id,
        string Role,
        string Email,
        string? BusinessUnits);

    private readonly IServiceProvider _services;
    private readonly ILogger<OfflineNotifierService> _log;
    private readonly IOptionsMonitor<NotifierOptions> _options;
    private readonly IOptionsMonitor<EmailOptions> _emailOptions;

    private readonly ConcurrentDictionary<int, ShiftAlertState> _alertedSessions = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentNotifications = new();

    public OfflineNotifierService(
        IServiceProvider services,
        ILogger<OfflineNotifierService> log,
        IOptionsMonitor<NotifierOptions> options,
        IOptionsMonitor<EmailOptions> emailOptions)
    {
        _services = services;
        _log = log;
        _options = options;
        _emailOptions = emailOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "OfflineNotifierService started (warn={Warn}m, escalate={Esc}m, every {Sec}s).",
            _options.CurrentValue.OfflineThresholdMinutes,
            _options.CurrentValue.EscalationThresholdMinutes,
            _options.CurrentValue.CheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OfflineNotifierService tick failed.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(15, _options.CurrentValue.CheckIntervalSeconds));
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var warnAfter = TimeSpan.FromMinutes(Math.Max(1, opts.OfflineThresholdMinutes));
        var escalateAfter = opts.EscalationThresholdMinutes > 0
            ? TimeSpan.FromMinutes(opts.EscalationThresholdMinutes)
            : TimeSpan.Zero;
        var nowUtc = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var holidayByUserDate = new Dictionary<string, bool>(StringComparer.Ordinal);

        async Task<bool> IsHolidayForLocalDateAsync(User user, DateOnly date)
        {
            var key = $"{user.Id}:{date:yyyyMMdd}";
            if (holidayByUserDate.TryGetValue(key, out var cached)) return cached;
            var isHoliday = await HolidayHelper.IsHolidayAsync(db, user, date);
            holidayByUserDate[key] = isHoliday;
            return isHoliday;
        }

        var managerRows = opts.NotifyAdmins
            ? await db.Users
                .Where(u => (u.Role == Roles.ProgramManager || u.Role == Roles.Pm)
                            && u.Email != null && u.Email != "")
                .Select(u => new ManagerRecipient(u.Id, u.Role, u.Email!, u.BusinessUnits))
                .ToListAsync(ct)
            : new List<ManagerRecipient>();

        var managerEmailById = managerRows
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First().Email);

        var pgmEmailsByBusinessUnit = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in managerRows)
        {
            if (!string.Equals(m.Role, Roles.ProgramManager, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(m.BusinessUnits)) continue;

            var buList = m.BusinessUnits
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var bu in buList)
            {
                if (!pgmEmailsByBusinessUnit.TryGetValue(bu, out var list))
                {
                    list = new List<string>();
                    pgmEmailsByBusinessUnit[bu] = list;
                }
                list.Add(m.Email);
            }
        }

        List<string> TeamManagerRecipientsFor(User employee)
        {
            if (!opts.NotifyAdmins) return new List<string>();

            var recipients = new List<string>();

            if (employee.ManagerId is int managerId
                && managerEmailById.TryGetValue(managerId, out var managerEmail))
            {
                recipients.Add(managerEmail);
            }

            if (!string.IsNullOrWhiteSpace(employee.BusinessUnit)
                && pgmEmailsByBusinessUnit.TryGetValue(employee.BusinessUnit, out var pgmEmails))
            {
                recipients.AddRange(pgmEmails);
            }

            return recipients
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        await SendShiftRemindersAsync(db, email, opts, nowUtc, ct);
        await SendMissedCheckInRemindersAsync(db, email, opts, nowUtc, ct);

        var openSessions = await db.Attendances
            .Include(a => a.User)
            .Where(a => a.CheckOut == null)
            .ToListAsync(ct);

        if (_alertedSessions.Count > 0)
        {
            var openIds = openSessions.Select(a => a.Id).ToHashSet();
            foreach (var key in _alertedSessions.Keys.ToList())
            {
                if (!openIds.Contains(key))
                {
                    _alertedSessions.TryRemove(key, out _);
                }
            }
        }

        foreach (var att in openSessions)
        {
            var u = att.User;
            if (u is null) continue;
            if (string.Equals(u.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase)) continue;
            var hasTrackedShift = string.Equals(u.Role, Roles.Employee, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Role, Roles.Pm, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Role, Roles.ProgramManager, StringComparison.OrdinalIgnoreCase);

            // Policy: pause ALL notification emails on statutory holidays
            // for the user's local date. Keep auto-close behavior active
            // so open sessions don't leak indefinitely.
            var localToday = UserClock.TodayFor(u);
            var isHolidayToday = await IsHolidayForLocalDateAsync(u, localToday);

            await HandleCutoffAsync(
                db, email, opts, att, u, nowUtc, ct,
                sendNotifications: !isHolidayToday && hasTrackedShift);

            if (isHolidayToday) continue;

            var state = _alertedSessions.GetOrAdd(att.Id, _ => new ShiftAlertState());

            // Late-arrival email — fires once per open session as soon as
            // we detect the check-in is past the shift start + grace.
            // LateCheck handles per-region grace (PH Onsite 15 / PH Offsite 0
            // / India 60). Support / Dayoff / Admin all return NotApplicable.
            if (hasTrackedShift && !state.LateAlertSent)
            {
                var schedForLate = await DbInitializer.GetEffectiveScheduleForDateAsync(
                    db, u, att.WorkDate);
                var isHolidayForCheckInDate = await IsHolidayForLocalDateAsync(
                    u, att.WorkDate);
                var late = LateCheck.Evaluate(
                    u,
                    schedForLate,
                    att.CheckIn,
                    isHoliday: isHolidayForCheckInDate);
                if (late.Status == LateCheck.LateStatus.Late && late.LateMinutes > 0)
                {
                    var lateKey = $"late:{att.Id}";
                    if (ShouldSend(lateKey, TimeSpan.FromHours(24), nowUtc))
                    {
                        var recipientsLate = BuildRecipients(opts, null, u, includeManagers: false);
                        var (lateSubject, lateBody) = BuildLateMessage(u, att, schedForLate, late);
                        await TrySendAndRecordAsync(db, email, AlertLevel.Late, u, recipientsLate, lateSubject, lateBody, late.LateMinutes, ct);
                        state.LateAlertSent = true;
                    }
                }
            }

            if (hasTrackedShift && !state.ForgotCheckoutSent)
            {
                // Primary trigger: 5 minutes before the user's scheduled
                // end-of-shift (anchored to the check-in's local date so an
                // overnight session points at the correct end). Falls back
                // to "rendered >= required hours" when the user has no
                // schedule row for the check-in date (e.g. Support / 24/7
                // rotation), so coverage never drops to zero.
                var offset = UserClock.OffsetFor(u);
                var nowLocal = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToOffset(offset).DateTime;
                var checkInUtc = DateTime.SpecifyKind(att.CheckIn, DateTimeKind.Utc);
                var checkInLocal = new DateTimeOffset(checkInUtc, TimeSpan.Zero).ToOffset(offset).DateTime;
                var schedule = await DbInitializer.GetEffectiveScheduleForDateAsync(
                    db, u, att.WorkDate);
                DateTime? scheduledEndLocal = null;
                if (schedule is { IsWorking: true, StartTime: { } sStart, EndTime: { } sEnd }
                    && !schedule.EffectiveWorkType.Equals("Dayoff", StringComparison.OrdinalIgnoreCase))
                {
                    var endDate = att.WorkDate;
                    // Overnight shift (e.g. 22:00 -> 06:00): bump end date so
                    // the scheduled end lands AFTER check-in, not 16h before.
                    if (sEnd <= sStart) endDate = endDate.AddDays(1);
                    scheduledEndLocal = endDate.ToDateTime(sEnd);
                }

                var elapsedMin = (int)Math.Round((nowUtc - att.CheckIn).TotalMinutes);
                var requiredMin = LateCheck.RequiredRenderHours(u) * 60;

                bool shouldFire;
                if (scheduledEndLocal is { } endLocal)
                {
                    // Fire 5 minutes AFTER scheduled end. The one-shot
                    // `ForgotCheckoutSent` flag below keeps us from spamming
                    // after the first send.
                    var minsUntilEnd = (endLocal - nowLocal).TotalMinutes;
                    shouldFire = minsUntilEnd <= -5;
                }
                else
                {
                    // No schedule available for this date — fall back to the
                    // elapsed-hours rule so support / unscheduled users still
                    // get a reminder.
                    shouldFire = elapsedMin >= requiredMin;
                }

                if (shouldFire)
                {
                    // Re-check right before send in case another request
                    // clocked the user out after we loaded open sessions.
                    var stillOpen = await db.Attendances.AnyAsync(
                        a => a.Id == att.Id && a.CheckOut == null, ct);
                    if (!stillOpen)
                    {
                        state.ForgotCheckoutSent = true;
                        continue;
                    }

                    var key = $"forgot:{att.Id}";
                    if (ShouldSend(key, TimeSpan.FromHours(12), nowUtc))
                    {
                        var recipients = BuildRecipients(opts, null, u, includeManagers: false);
                        var (subject, body) = BuildForgotCheckoutMessage(u, att, elapsedMin, requiredMin, schedule);
                        await TrySendAndRecordAsync(db, email, AlertLevel.ForgotCheckout, u, recipients, subject, body, elapsedMin, ct);
                        state.ForgotCheckoutSent = true;
                    }
                }
            }

            if (string.Equals(u.PresenceState, "lunch", StringComparison.OrdinalIgnoreCase) && u.IsLunchActive()) continue;
            if (string.Equals(u.PresenceState, "break", StringComparison.OrdinalIgnoreCase) && u.IsBreakActive()) continue;

            // Use the same explicit presence state shown on both dashboards.
            // LastSeen is diagnostic data, not proof that an online user went
            // offline; treating it as such creates false recurring alerts.
            if (!string.Equals(u.PresenceState, "offline", StringComparison.OrdinalIgnoreCase)) continue;

            var offlineSince = u.OfflineSince ?? u.LastSeen;
            if (offlineSince is null) continue;

            var offlineSinceUtc = DateTime.SpecifyKind(offlineSince.Value, DateTimeKind.Utc);
            var offlineFor = nowUtc - offlineSinceUtc;
            if (offlineFor < warnAfter) continue;

            var lastUtc = u.LastSeen is { } ls
                ? DateTime.SpecifyKind(ls, DateTimeKind.Utc)
                : offlineSinceUtc;

            AlertLevel toSend;
            if (state.DeductionSent)
            {
                continue;
            }
            else if (escalateAfter > TimeSpan.Zero && offlineFor >= escalateAfter)
            {
                toSend = AlertLevel.Deduction;
            }
            else if (state.LastWarningUtc is { } last && (nowUtc - last) < warnAfter)
            {
                continue;
            }
            else
            {
                toSend = AlertLevel.Warning;
            }

            var minutes = (int)Math.Round(offlineFor.TotalMinutes);
            if (!hasTrackedShift) continue;
            var recipientsOffline = BuildRecipients(
                opts,
                TeamManagerRecipientsFor(u),
                u,
                includeManagers: minutes >= 60);
            var (offlineSubject, offlineBody) = BuildMessage(toSend, u, att, minutes, lastUtc, opts.OfflineThresholdMinutes);

            var offKey = $"offline:{att.Id}:{toSend}";
            if (!ShouldSend(offKey, TimeSpan.FromMinutes(10), nowUtc)) continue;

            await TrySendAndRecordAsync(db, email, toSend, u, recipientsOffline, offlineSubject, offlineBody, minutes, ct);
            if (toSend == AlertLevel.Warning) state.LastWarningUtc = nowUtc;
            if (toSend == AlertLevel.Deduction) state.DeductionSent = true;
        }
    }

    private async Task SendShiftRemindersAsync(
        AppDbContext db,
        IEmailSender email,
        NotifierOptions opts,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var users = await db.Users
            .Where(u => string.Equals(u.Role, Roles.Employee))
            .ToListAsync(ct);

        foreach (var u in users)
        {
            var today = UserClock.TodayFor(u);
            if (await HolidayHelper.IsHolidayAsync(db, u, today)) continue;

            var nowLocal = UserClock.NowFor(u);
            var schedule = await DbInitializer.GetScheduleForDateAsync(db, u, today);
            if (schedule is null || !schedule.IsWorking || schedule.StartTime is null) continue;
            if (schedule.EffectiveWorkType.Equals("Dayoff", StringComparison.OrdinalIgnoreCase)) continue;

            var hasOpen = await db.Attendances.AnyAsync(a => a.UserId == u.Id && a.CheckOut == null, ct);
            if (hasOpen) continue;

            var minsToStart = (int)Math.Round((schedule.StartTime.Value - TimeOnly.FromDateTime(nowLocal.DateTime)).TotalMinutes);
            if (minsToStart is < 0 or > 5) continue;

            var reminderKey = $"shift:{u.Id}:{today:yyyyMMdd}";
            if (!ShouldSend(reminderKey, TimeSpan.FromHours(6), nowUtc)) continue;

            var recipients = BuildRecipients(opts, null, u, includeManagers: false);
            var (subject, body) = BuildShiftReminderMessage(u, schedule);
            await TrySendAndRecordAsync(db, email, AlertLevel.ShiftReminder, u, recipients, subject, body, 0, ct);
        }
    }

    /// <summary>
    /// Fires once per user per day when the local clock is past the
    /// scheduled shift start + grace and there is still no attendance
    /// row for today (i.e. the user never clocked in). Complements
    /// <see cref="SendShiftRemindersAsync"/>, which only nudges users
    /// in the 5 minutes BEFORE start, and the post-check-in Late alert
    /// which can only fire after an Attendance row exists.
    /// </summary>
    private async Task SendMissedCheckInRemindersAsync(
        AppDbContext db,
        IEmailSender email,
        NotifierOptions opts,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var users = await db.Users
            .Where(u => string.Equals(u.Role, Roles.Employee))
            .ToListAsync(ct);

        foreach (var u in users)
        {
            var today = UserClock.TodayFor(u);
            if (await HolidayHelper.IsHolidayAsync(db, u, today)) continue;

            var nowLocal = UserClock.NowFor(u);

            var schedule = await DbInitializer.GetEffectiveScheduleForDateAsync(db, u, today);
            if (schedule is null
                || !schedule.IsWorking
                || schedule.StartTime is null) continue;

            var workType = schedule.EffectiveWorkType;
            if (workType.Equals("Dayoff", StringComparison.OrdinalIgnoreCase)) continue;

            // Skip if the user already has ANY attendance row for today —
            // they may have checked in earlier and even checked out.
            // Open sessions from prior days are evaluated separately
            // (post-check-in Late alert in TickAsync).
            var anyToday = await db.Attendances.AnyAsync(
                a => a.UserId == u.Id && a.WorkDate == today, ct);
            if (anyToday) continue;

            var grace = LateCheck.GraceMinutes(u, workType);
            var nowTime = TimeOnly.FromDateTime(nowLocal.DateTime);
            var start = schedule.StartTime.Value;

            var nowMin = nowTime.Hour * 60 + nowTime.Minute + nowTime.Second / 60.0;
            var startMin = start.Hour * 60 + start.Minute + start.Second / 60.0;
            var minsLate = nowMin - startMin;

            // Only fire after the grace window has closed. Cap how long
            // after the shift start we keep alerting (e.g. don't spam at
            // 23:55 about a 09:00 shift) — 6 hours past start is a
            // reasonable upper bound and the daily dedupe key prevents
            // repeats within that window.
            if (minsLate <= grace) continue;
            if (minsLate > 6 * 60) continue;

            var key = $"missed-checkin:{u.Id}:{today:yyyyMMdd}";
            if (!ShouldSend(key, TimeSpan.FromHours(24), nowUtc)) continue;

            var recipients = BuildRecipients(opts, null, u, includeManagers: false);
            var (subject, body) = BuildMissedCheckInMessage(u, schedule, (int)Math.Round(minsLate), grace, workType);
            await TrySendAndRecordAsync(
                db, email, AlertLevel.MissedCheckIn, u, recipients,
                subject, body, (int)Math.Round(minsLate), ct);
        }
    }

    private async Task HandleCutoffAsync(
        AppDbContext db,
        IEmailSender email,
        NotifierOptions opts,
        Attendance att,
        User u,
        DateTime nowUtc,
        CancellationToken ct,
        bool sendNotifications = true)
    {
        var offset = UserClock.OffsetFor(u);
        var nowLocal = new DateTimeOffset(nowUtc, TimeSpan.Zero).ToOffset(offset).DateTime;

        // Anchor the auto-close to the CHECK-IN's local day, not "today".
        // Otherwise an open row from yesterday would get clamped to today's
        // cutoff, inflating duration by ~24 hours.
        var checkInUtc = DateTime.SpecifyKind(att.CheckIn, DateTimeKind.Utc);
        var checkInLocal = new DateTimeOffset(checkInUtc, TimeSpan.Zero).ToOffset(offset).DateTime;

        // Auto-close policy:
        //   - Support / Copilot rotation users: cap on elapsed time, since
        //     a 24/7 rotation can span any wall-clock window. CheckOut =
        //     CheckIn + 23 hours.
        //   - Authored overnight shifts: close at their next-day scheduled
        //     end instead of truncating them at 23:30.
        //   - Everyone else (normal employees): fixed daily cutoff at
        //     23:30 local time of the check-in date. If the user clocked
        //     in after 23:30 the cutoff rolls to 23:30 the next day so we
        //     do not immediately close a brand-new row.
        DateTime cutoffLocal;
        var isOvernightScheduledShift = false;
        if (u.IsSupport)
        {
            cutoffLocal = checkInLocal.AddHours(23);
        }
        else
        {
            var schedule = await DbInitializer.GetEffectiveScheduleForDateAsync(
                db, u, att.WorkDate);
            if (schedule is { IsWorking: true, StartTime: { } start, EndTime: { } end }
                && end <= start)
            {
                isOvernightScheduledShift = true;
                cutoffLocal = att.WorkDate.AddDays(1).ToDateTime(end);
            }
            else
            {
                cutoffLocal = new DateTime(
                    checkInLocal.Year, checkInLocal.Month, checkInLocal.Day,
                    23, 30, 0);
                if (cutoffLocal <= checkInLocal)
                {
                    cutoffLocal = cutoffLocal.AddDays(1);
                }
            }
        }

        var minsToCutoff = (int)Math.Round((cutoffLocal - nowLocal).TotalMinutes);
        if (minsToCutoff is >= 0 and <= 10)
        {
            var key = $"cutoff-reminder:{att.Id}:{nowLocal:yyyyMMdd}";
            if (sendNotifications && ShouldSend(key, TimeSpan.FromHours(1), nowUtc))
            {
                var recipients = BuildRecipients(opts, null, u, includeManagers: false);
                var subject = "[Attendance] Required logout reminder";
                var tzLabel = UserClock.Label(u);
                string body;
                if (u.IsSupport)
                {
                    body =
                        $"Hi {u.FullName},\r\n\r\n" +
                        $"Your session will be auto-closed at {cutoffLocal:HH:mm} {tzLabel} " +
                        "(23 hours after check-in). Please check out before then to " +
                        "close your attendance cleanly.\r\n\r\n" +
                        "This is an automated reminder.\r\n";
                }
                else
                {
                    body = isOvernightScheduledShift
                        ? $"Hi {u.FullName},\r\n\r\n" +
                          $"Your overnight session will be auto-closed at its scheduled end, " +
                          $"{cutoffLocal:HH:mm} {tzLabel}. Please check out before then to close " +
                          "your attendance cleanly.\r\n\r\n" +
                          "This is an automated reminder.\r\n"
                        : $"Hi {u.FullName},\r\n\r\n" +
                          $"Open sessions are auto-closed daily at 23:30 {tzLabel}. " +
                          $"Please check out by {cutoffLocal:HH:mm} {tzLabel} to close " +
                          "your attendance cleanly.\r\n\r\n" +
                          "This is an automated reminder.\r\n";
                }
                await TrySendAndRecordAsync(db, email, AlertLevel.RequiredLogout, u, recipients, subject, body, 0, ct);
            }
        }

        if (nowLocal >= cutoffLocal && att.CheckOut is null)
        {
            var cutoffUtc = DateTime.SpecifyKind(cutoffLocal - offset, DateTimeKind.Utc);
            att.CheckOut = cutoffUtc;
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Auto checkout at cutoff for {User}", u.Username);
        }
    }

    private static List<string> BuildRecipients(
        NotifierOptions opts,
        IEnumerable<string>? managerEmails,
        User user,
        bool includeManagers)
    {
        var recipients = new List<string>();
        if (opts.NotifyEmployee
            && !user.OptOutReminderEmails
            && !string.IsNullOrWhiteSpace(user.Email))
        {
            recipients.Add(user.Email);
        }
        if (includeManagers && managerEmails is not null)
        {
            recipients.AddRange(managerEmails);
        }
        if (opts.ExtraRecipients is { Count: > 0 }) recipients.AddRange(opts.ExtraRecipients);
        return recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool ShouldSend(string key, TimeSpan minInterval, DateTime nowUtc)
    {
        if (_recentNotifications.TryGetValue(key, out var prev) && nowUtc - prev < minInterval)
        {
            return false;
        }

        _recentNotifications[key] = nowUtc;

        if (_recentNotifications.Count > 5000)
        {
            var cutoff = nowUtc.AddDays(-2);
            foreach (var old in _recentNotifications.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            {
                _recentNotifications.TryRemove(old, out _);
            }
        }

        return true;
    }

    private async Task TrySendAndRecordAsync(
        AppDbContext db,
        IEmailSender email,
        AlertLevel level,
        User u,
        List<string> recipients,
        string subject,
        string body,
        int minutes,
        CancellationToken ct)
    {
        if (recipients.Count == 0)
        {
            await RecordAsync(db, level, "NoRecipients", u, recipients, subject, minutes, null, ct);
            return;
        }

        if (!_emailOptions.CurrentValue.Enabled)
        {
            await RecordAsync(db, level, "Disabled", u, recipients, subject, minutes, null, ct);
            return;
        }

        try
        {
            await email.SendAsync(recipients, subject, body, ct);
            await RecordAsync(db, level, "Sent", u, recipients, subject, minutes, null, ct);
        }
        catch (Exception ex)
        {
            await RecordAsync(db, level, "Failed", u, recipients, subject, minutes, ex.Message, ct);
            _log.LogError(ex, "Failed to send {Level} alert for {User}", level, u.Username);
        }
    }

    private static async Task RecordAsync(
        AppDbContext db,
        AlertLevel level,
        string status,
        User u,
        IEnumerable<string> recipients,
        string subject,
        int offlineMinutes,
        string? error,
        CancellationToken ct)
    {
        var joined = string.Join(", ", recipients);
        if (joined.Length > 1024) joined = joined[..1024];
        var msg = error;
        if (msg is { Length: > 2048 }) msg = msg[..2048];

        db.NotificationLogs.Add(new NotificationLog
        {
            SentAt = DateTime.UtcNow,
            Level = level.ToString(),
            Status = status,
            UserId = u.Id,
            UserFullName = u.FullName,
            Username = u.Username,
            Recipients = joined,
            Subject = subject.Length > 255 ? subject[..255] : subject,
            OfflineMinutes = offlineMinutes,
            ErrorMessage = msg,
        });
        await db.SaveChangesAsync(ct);
    }

    private static (string Subject, string Body) BuildMessage(
        AlertLevel level,
        User u,
        Attendance att,
        int minutes,
        DateTime lastUtc,
        int thresholdMinutes)
    {
        var lastSeenPht = PhTime.Format(lastUtc, "yyyy-MM-dd HH:mm");
        var checkInPht = PhTime.Format(att.CheckIn, "yyyy-MM-dd HH:mm");
        var who = $"{u.FullName} ({u.Username}, {u.EmployeeId ?? "-"})";

        var subject = level == AlertLevel.Deduction
            ? $"[Attendance] {u.EmployeeId} {u.FullName} offline {minutes} min during shift"
            : $"[Attendance] Reminder - {u.FullName} offline {minutes} min";

        var body =
            $"Employee : {who}\r\n" +
            $"Status : Offline for {minutes} minute(s) during an active shift.\r\n" +
            $"Last seen : {lastSeenPht} PHT\r\n" +
            $"Checked in : {checkInPht} PHT\r\n\r\n" +
            $"Gentle reminder that your laptop has been offline for {thresholdMinutes} minutes. Please contact your Project Manager.\r\n\r\n" +
            "This is an automated notification, kindly ignore if it does not apply.\r\n";

        return (subject, body);
    }

    private static (string Subject, string Body) BuildForgotCheckoutMessage(
        User u,
        Attendance att,
        int elapsedMinutes,
        int requiredMinutes,
        ScheduleEntry? schedule)
    {
        var tzLabel = UserClock.Label(u);
        var startLabel = schedule?.StartTime?.ToString("HH:mm") ?? "--:--";
        var endLabel = schedule?.EndTime?.ToString("HH:mm") ?? "--:--";

        var subject = $"[Attendance] Reminder - {u.FullName}, please clock out";
        var body =
            $"Hi {u.FullName},\r\n\r\n" +
            "This is a friendly reminder to clock out so your timesheet is captured correctly.\r\n" +
            $"Your shift is {startLabel} - {endLabel} {tzLabel}.\r\n\r\n" +
            "If you need to keep working, you can ignore this automated notification.\r\n" +
            "Thank you.\r\n";

        return (subject, body);
    }

    private static (string Subject, string Body) BuildShiftReminderMessage(User u, ScheduleEntry sched)
    {
        var start = sched.StartTime?.ToString("HH:mm") ?? "";
        var end = sched.EndTime?.ToString("HH:mm") ?? "";
        var tz = UserClock.Label(u);
        var subject = $"[Attendance] Your shift is about to start ({start} - {end} {tz})";
        var body =
            $"Hi {u.FullName},\r\n\r\n" +
            $"Your shift is about to start ({start} - {end} {tz}).\r\n" +
            "Please remember to check in on time.\r\n\r\n" +
            "This is an automated reminder.\r\n";

        return (subject, body);
    }

    private static (string Subject, string Body) BuildMissedCheckInMessage(
        User u,
        ScheduleEntry sched,
        int minutesLate,
        int graceMinutes,
        string workType)
    {
        var tz = UserClock.Label(u);
        var startLabel = sched.StartTime?.ToString("HH:mm") ?? "--:--";
        var endLabel = sched.EndTime?.ToString("HH:mm") ?? "--:--";
        var h = minutesLate / 60;
        var m = minutesLate % 60;
        var lateLabel = h > 0 ? $"{h:D2}:{m:D2}" : $"{m} min";

        var subject = $"[Attendance] {u.FullName} — missed check-in for {startLabel} {tz} shift";
        var body =
            $"Hi {u.FullName},\r\n\r\n" +
            $"Our records show you have NOT clocked in for today's scheduled shift " +
            $"({startLabel} - {endLabel} {tz}, {workType}). You are currently {lateLabel} " +
            $"past the start of your shift (after the {graceMinutes}-min grace).\r\n\r\n" +
            "Please clock in as soon as possible. If you are unable to work today, " +
            "file a Leave request; if you forgot to clock in, you can file an Add " +
            "Time Entry request from the Attendance page.\r\n\r\n" +
            "This is an automated notification from the Attendance Monitoring System.\r\n";

        return (subject, body);
    }

    private static (string Subject, string Body) BuildLateMessage(
        User u,
        Attendance att,
        ScheduleEntry? sched,
        LateCheck.Result late)
    {
        var tz = UserClock.Label(u);
        var checkInLocal = UserClock.Format(u, att.CheckIn, "yyyy-MM-dd HH:mm");
        var startLabel = sched?.StartTime?.ToString("HH:mm") ?? "--:--";
        var h = late.LateMinutes / 60;
        var m = late.LateMinutes % 60;
        var lateLabel = h > 0 ? $"{h:D2}:{m:D2}" : $"{m} min";

        var subject = $"[Attendance] {u.FullName} — late by {lateLabel} for {startLabel} {tz} shift";
        var body =
            $"Hi {u.FullName},\r\n\r\n" +
            $"Our records show you checked in at {checkInLocal} {tz}, which is {lateLabel} past your scheduled " +
            $"start of {startLabel} {tz} (after the {late.GraceMinutes}-min grace for {late.WorkType}).\r\n\r\n" +
            "If this is unexpected, please reach out to your Project Manager to discuss. " +
            "If you missed your time-in, you can also file an Add Time Entry request from the Attendance page.\r\n\r\n" +
            "This is an automated notification from the Attendance Monitoring System.\r\n";

        return (subject, body);
    }
}
