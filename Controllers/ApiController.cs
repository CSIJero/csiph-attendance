using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// JSON endpoints used by the live dashboard JS. Antiforgery is intentionally
/// disabled for these endpoints because <see cref="Navigator"/> sendBeacon
/// cannot attach the token; authentication is still required.
/// </summary>
[Authorize]
[Route("api")]
[ApiController]
[IgnoreAntiforgeryToken]
public class ApiController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly int _onlineThreshold;

    public ApiController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _onlineThreshold = config.GetValue(
            "AttendanceMonitoring:OnlineThresholdSeconds",
            Models.Constants.DefaultOnlineThresholdSeconds);
    }

    public class HeartbeatRequest
    {
        // Client-reported presence: "online" | "away" | "offline".
        // "away" is accepted for legacy clients but collapses to "online".
        public string? State { get; set; }
    }

    [HttpPost("heartbeat")]
    [EnableRateLimiting("Heartbeat")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Heartbeat([FromBody] HeartbeatRequest? body = null)
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        if (!TryNormalizeHeartbeatState(body?.State, out var clientState))
            return BadRequest(new { error = "State must be online, away, or offline." });

        u.LastSeen = DateTime.UtcNow;

        // Lunch wins over any client-reported state until it expires. The
        // client-side toggle is one-way (start/end) so the heartbeat label
        // shouldn't accidentally flip the state mid-break.
        if (u.PresenceState == "lunch" && u.IsLunchActive())
        {
            // Stay on lunch — ignore the client's "online/away" ticks.
        }
        else if (u.PresenceState == "lunch")
        {
            // Lunch expired — fall through to whatever the client reports.
            u.LunchStartedAt = null;
            u.PresenceState = clientState;
        }
        else if (u.PresenceState == "break" && u.IsBreakActive())
        {
            // Stay on break — same idea as lunch above.
        }
        else if (u.PresenceState == "break")
        {
            // Short break expired — clear it and follow the client state.
            u.BreakStartedAt = null;
            u.PresenceState = clientState;
        }
        else
        {
            u.PresenceState = clientState;
        }

        // Maintain OfflineSince: stamp when we first see "offline",
        // clear when transitioning back to any other state. The offline
        // notifier uses this column directly (instead of LastSeen
        // staleness) so alert cadence reflects PC-lock duration, not
        // heartbeat health.
        if (u.PresenceState == "offline")
        {
            u.OfflineSince ??= DateTime.UtcNow;
        }
        else
        {
            u.OfflineSince = null;
        }

        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            state = u.PresenceState,
            last_seen = u.LastSeen!.Value.ToString("o"),
            lunch_ends_at = LunchEndsAt(u)?.ToString("o"),
            break_ends_at = BreakEndsAt(u)?.ToString("o"),
            breaks_used = BreaksUsedTodayPh(u),
            breaks_max = Models.Constants.MaxShortBreaksPerDay,
            lunches_used = LunchesUsedTodayPh(u),
            lunches_max = Models.Constants.MaxLunchesPerDay,
        });
    }

    private static DateTime? LunchEndsAt(Models.User u)
    {
        if (u.LunchStartedAt is null) return null;
        var start = DateTime.SpecifyKind(u.LunchStartedAt.Value, DateTimeKind.Utc);
        return start.AddMinutes(Models.Constants.LunchBreakMinutes);
    }

    private static DateTime? BreakEndsAt(Models.User u)
    {
        if (u.BreakStartedAt is null) return null;
        var start = DateTime.SpecifyKind(u.BreakStartedAt.Value, DateTimeKind.Utc);
        return start.AddMinutes(Models.Constants.ShortBreakMinutes);
    }

    /// <summary>
    /// Returns the user's break counter for the current PHT calendar day.
    /// Returns 0 (without mutating) when the stored date is older than today.
    /// </summary>
    private static int BreaksUsedTodayPh(Models.User u)
    {
        var today = PhTime.Today;
        return (u.BreaksUsedDate == today) ? u.BreaksUsedToday : 0;
    }

    /// <summary>
    /// Returns the user's lunch counter for the current PHT calendar day.
    /// Returns 0 (without mutating) when the stored date is older than today.
    /// </summary>
    private static int LunchesUsedTodayPh(Models.User u)
    {
        var today = PhTime.Today;
        return (u.LunchesUsedDate == today) ? u.LunchesUsedToday : 0;
    }

    private static string NormalizeState(string? raw, string fallback)
    {
        return (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "online"  => "online",
            // Policy: no Away tier — collapse legacy clients to Online.
            "away"    => "online",
            "lunch"   => "lunch",
            "break"   => "break",
            "offline" => "offline",
            _         => fallback,
        };
    }

    private static bool TryNormalizeHeartbeatState(string? raw, out string state)
    {
        state = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (state)
        {
            case "online":
            case "offline":
                return true;
            case "away":
                state = "online";
                return true;
            default:
                state = string.Empty;
                return false;
        }
    }

    [HttpPost("lunch/start")]
    public async Task<IActionResult> StartLunch()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        // Reset the daily counter when the PHT date rolls over so the user
        // can take their one lunch again tomorrow.
        var today = PhTime.Today;
        if (u.LunchesUsedDate != today)
        {
            u.LunchesUsedToday = 0;
            u.LunchesUsedDate = today;
        }

        // One-lunch-per-day cap. The client also hides the button after
        // the quota is consumed but defend in depth.
        if (u.LunchesUsedToday >= Models.Constants.MaxLunchesPerDay)
        {
            return BadRequest(new
            {
                ok = false,
                error = "limit_reached",
                lunches_used = u.LunchesUsedToday,
                lunches_max = Models.Constants.MaxLunchesPerDay,
            });
        }

        u.LunchesUsedToday += 1;
        u.PresenceState = "lunch";
        u.LunchStartedAt = DateTime.UtcNow;
        u.LastSeen = DateTime.UtcNow;
        u.OfflineSince = null;
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            state = u.PresenceState,
            lunch_ends_at = LunchEndsAt(u)!.Value.ToString("o"),
            lunches_used = u.LunchesUsedToday,
            lunches_max = Models.Constants.MaxLunchesPerDay,
        });
    }

    [HttpPost("lunch/end")]
    public async Task<IActionResult> EndLunch()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        u.LunchStartedAt = null;
        u.PresenceState = "online";
        u.LastSeen = DateTime.UtcNow;
        u.OfflineSince = null;
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            state = u.PresenceState,
            lunches_used = LunchesUsedTodayPh(u),
            lunches_max = Models.Constants.MaxLunchesPerDay,
        });
    }

    // ------------------------------------------------------------------
    // Short break (15 min) — up to MaxShortBreaksPerDay per PHT day,
    // in addition to the one lunch break. Same semantics as lunch:
    // suppresses offline alerts and auto-expires after the window.
    // ------------------------------------------------------------------

    [HttpPost("break/start")]
    public async Task<IActionResult> StartBreak()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        // Reset the daily counter when the PHT date rolls over.
        var today = PhTime.Today;
        if (u.BreaksUsedDate != today)
        {
            u.BreaksUsedToday = 0;
            u.BreaksUsedDate = today;
        }

        // Block when the quota is exhausted (independent of any active lunch
        // since the front-end button is hidden / disabled in that case).
        if (u.BreaksUsedToday >= Models.Constants.MaxShortBreaksPerDay)
        {
            return BadRequest(new
            {
                ok = false,
                error = "limit_reached",
                breaks_used = u.BreaksUsedToday,
                breaks_max = Models.Constants.MaxShortBreaksPerDay,
            });
        }

        u.BreaksUsedToday += 1;
        u.PresenceState = "break";
        u.BreakStartedAt = DateTime.UtcNow;
        u.LastSeen = DateTime.UtcNow;
        u.OfflineSince = null;
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            state = u.PresenceState,
            break_ends_at = BreakEndsAt(u)!.Value.ToString("o"),
            breaks_used = u.BreaksUsedToday,
            breaks_max = Models.Constants.MaxShortBreaksPerDay,
        });
    }

    [HttpPost("break/end")]
    public async Task<IActionResult> EndBreak()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        u.BreakStartedAt = null;
        u.PresenceState = "online";
        u.LastSeen = DateTime.UtcNow;
        u.OfflineSince = null;
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            state = u.PresenceState,
            breaks_used = BreaksUsedTodayPh(u),
            breaks_max = Models.Constants.MaxShortBreaksPerDay,
        });
    }

    [HttpPost("offline")]
    public async Task<IActionResult> GoOffline()
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        u.LastSeen = null;
        u.PresenceState = "offline";
        u.OfflineSince ??= DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }

    public class LocationSetRequest
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Accuracy { get; set; } // metres; informational
    }

    /// <summary>
    /// Stores the browser-reported GPS coordinates for the authenticated
    /// user and reverse-geocodes them to a "City, Country" label. The
    /// caller is the dashboard JS, which calls this once per session
    /// after <c>navigator.geolocation</c> resolves. Overwrites the
    /// IP-based fallback that was captured at login. Coordinates with an
    /// accuracy worse than 50 km are rejected so a misconfigured device
    /// can't replace the IP-based guess with something even less useful.
    /// </summary>
    [HttpPost("location/set")]
    public async Task<IActionResult> SetLocation([FromBody] LocationSetRequest body)
    {
        var raw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(raw, out var id)) return Unauthorized();

        var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return Unauthorized();

        var lat = body?.Latitude ?? double.NaN;
        var lon = body?.Longitude ?? double.NaN;
        var acc = body?.Accuracy ?? double.NaN;

        if (double.IsNaN(lat) || double.IsNaN(lon)
            || lat is < -90 or > 90
            || lon is < -180 or > 180)
        {
            return BadRequest(new { ok = false, error = "invalid_coordinates" });
        }

        // Reject grossly inaccurate readings (e.g. IP-derived "geolocation"
        // surfaced through the browser API). Anything coarser than 50 km
        // is no better than the IP fallback we already have.
        if (!double.IsNaN(acc) && acc > 50_000)
        {
            return BadRequest(new { ok = false, error = "accuracy_too_low" });
        }

        var label = await ClientInfo.ReverseGeocodeAsync(lat, lon);

        u.LastLoginLatitude = lat;
        u.LastLoginLongitude = lon;
        u.LastLoginLocationSource = "gps";
        if (!string.IsNullOrEmpty(label))
        {
            u.LastLoginLocation = label;
        }
        await _db.SaveChangesAsync();

        return new JsonResult(new
        {
            ok = true,
            location = u.LastLoginLocation,
            source = u.LastLoginLocationSource,
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var users = await VisibleUsersAsync();
        var today = PhTime.Today;
        var yesterday = today.AddDays(-1);
        var userIds = users.Select(u => u.Id).ToList();
        var attendanceRows = await _db.Attendances
            .Where(a => userIds.Contains(a.UserId)
                        && (a.WorkDate == today
                            || (a.WorkDate == yesterday && a.CheckOut == null)))
            .OrderByDescending(a => a.CheckIn)
            .ToListAsync();
        var todays = attendanceRows
            .GroupBy(a => a.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.IsOpen)
                      .ThenByDescending(a => a.CheckIn)
                      .First());
        var todaysSchedule = await _db.ScheduleEntries
            .Where(s => s.WorkDate == today && userIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId);
        var todayHolidayRows = await _db.Holidays
            .Where(h => h.Date == today)
            .ToListAsync();

        var payload = new List<object>();
        var online = 0;
        var lunch = 0;
        var onbreak = 0;
        var present = 0;

        foreach (var u in users)
        {
            todays.TryGetValue(u.Id, out var att);
            todaysSchedule.TryGetValue(u.Id, out var sched);
            var state = u.EffectiveDashboardState(att, _onlineThreshold);
            var isOnline = state != "offline";
            var isHoliday = todayHolidayRows.Any(h => h.Country == HolidayHelper.CountryFor(u) || h.Country == "ALL");
            var todayWorkType = isHoliday ? "Holiday" : sched?.EffectiveWorkType;
            if (isOnline) online++;
            if (state == "lunch") lunch++;
            if (state == "break") onbreak++;
            if (att is not null) present++;

            payload.Add(new
            {
                id = u.Id,
                username = u.Username,
                full_name = u.FullName,
                role = u.Role,
                business_unit = u.BusinessUnit,
                online = isOnline,
                state,
                last_seen = u.LastSeen?.ToString("o"),
                lunch_ends_at = state == "lunch" ? LunchEndsAt(u)?.ToString("o") : null,
                break_ends_at = state == "break" ? BreakEndsAt(u)?.ToString("o") : null,
                checked_in = att is not null,
                today_work_type = todayWorkType,
                check_in = att?.CheckIn.ToString("o"),
                check_out = att?.CheckOut?.ToString("o"),
                duration_minutes = att?.DurationMinutes ?? 0,
                last_login_ip = u.LastLoginIp,
                last_login_host = u.LastLoginHost,
                last_login_location = u.LastLoginLocation,
                last_login_at = u.LastLoginAt?.ToString("o"),
            });
        }

        return new JsonResult(new
        {
            generated_at = DateTime.UtcNow.ToString("o"),
            totals = new
            {
                online,
                away = 0, // legacy field; the Away tier was removed (policy: Online unless locked)
                lunch,
                @break = onbreak,
                offline = users.Count - online,
                present_today = present,
                total_users = users.Count,
            },
            users = payload,
        });
    }

    /// <summary>
    /// Attendance history for a specific user. Admins can query anyone;
    /// non-admins can only query themselves.
    /// </summary>
    [HttpGet("attendance/{userId:int}")]
    public async Task<IActionResult> UserAttendance(
        int userId,
        [FromQuery(Name = "start")] string? startRaw,
        [FromQuery(Name = "end")] string? endRaw)
    {
        var meRaw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(meRaw, out var meId)) return Unauthorized();

        if (!await CanViewAsync(meId, userId)) return Forbid();

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (target is null) return NotFound();

        var q = _db.Attendances.AsQueryable().Where(a => a.UserId == userId);
        if (TryParseDate(startRaw, out var start)) q = q.Where(a => a.WorkDate >= start);
        if (TryParseDate(endRaw, out var end)) q = q.Where(a => a.WorkDate <= end);

        var records = await q
            .OrderByDescending(a => a.WorkDate)
            .ThenByDescending(a => a.CheckIn)
            .Take(500)
            .ToListAsync();

        return new JsonResult(new
        {
            user = new
            {
                id = target.Id,
                username = target.Username,
                full_name = target.FullName,
                email = target.Email,
                employee_id = target.EmployeeId,
                role = target.Role,
            },
            records = records.Select(r => new
            {
                id = r.Id,
                work_date = r.WorkDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                check_in = r.CheckIn.ToString("o"),
                check_out = r.CheckOut?.ToString("o"),
                duration_minutes = r.DurationMinutes,
                status = r.CheckOut is null ? "Open" : "Closed",
                check_in_photo = r.CheckInPhoto,
                check_out_photo = r.CheckOutPhoto,
            }),
        });
    }

    private static bool TryParseDate(string? raw, out DateOnly d)
    {
        d = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    // ------------------------------------------------------------------
    // Offsite-days editor (admin quick action on the dashboard modal)
    //
    // Under the per-date schedule model these endpoints operate on the
    // current PHT week (Mon..Sun containing today): GET reports which
    // weekdays of this week the user currently has a working entry for.
    // POST applies the selected weekday pattern to a rolling horizon
    // (current week + next N weeks) so admins don't need to repeat the
    // same offsite mask every single week.
    // ------------------------------------------------------------------
    public class OffsiteDaysRequest
    {
        // Working weekdays (0=Mon … 6=Sun) of the current PHT week.
        public List<int>? Days { get; set; }
    }

    private static (DateOnly weekStart, DateOnly weekEnd) CurrentPhWeekRange()
    {
        var today = AttendanceMonitoring.Services.PhTime.Today;
        var todayIdx = ((int)today.DayOfWeek + 6) % 7; // Mon = 0
        var start = today.AddDays(-todayIdx);
        return (start, start.AddDays(6));
    }

    private const int OffsiteHorizonWeeks = 12;

    /// <summary>
    /// Returns which weekdays (0=Mon … 6=Sun) of the current PHT week
    /// the user has a working schedule row for. Admins can query anyone;
    /// non-admins can only query themselves.
    /// </summary>
    [HttpGet("offsite/{userId:int}")]
    public async Task<IActionResult> GetOffsiteDays(int userId)
    {
        var meRaw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(meRaw, out var meId)) return Unauthorized();

        if (!await CanViewAsync(meId, userId)) return Forbid();

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (target is null) return NotFound();

        var (weekStart, weekEnd) = CurrentPhWeekRange();
        var entries = await _db.ScheduleEntries
            .Where(s => s.UserId == target.Id && s.WorkDate >= weekStart && s.WorkDate <= weekEnd)
            .OrderBy(s => s.WorkDate)
            .ToListAsync();

        return new JsonResult(new
        {
            user = new
            {
                id = target.Id,
                username = target.Username,
                full_name = target.FullName,
                employee_id = target.EmployeeId,
            },
            week_start = weekStart.ToString("yyyy-MM-dd"),
            week_end = weekEnd.ToString("yyyy-MM-dd"),
            days = entries.Select(e => new
            {
                work_date = e.WorkDate.ToString("yyyy-MM-dd"),
                weekday = e.Weekday,
                name = Constants.WeekdayNames[e.Weekday],
                working = e.IsWorking,
                // Whether the row is currently flagged Offsite — this is
                // what the chip-panel checkboxes track. Days that are
                // working-Onsite stay unchecked so the admin can safely
                // toggle Offsite without nuking an existing Onsite shift.
                is_offsite = e.EffectiveWorkType.Equals("Offsite", StringComparison.OrdinalIgnoreCase),
                work_type = e.EffectiveWorkType,
                start = e.StartTime?.ToString("HH:mm"),
                end = e.EndTime?.ToString("HH:mm"),
                note = e.Note ?? string.Empty,
            }),
        });
    }

    /// <summary>
    /// Sets the user's Offsite-day mask as a recurring weekday pattern
    /// over a rolling horizon (current week + next <see cref="OffsiteHorizonWeeks"/>-1 weeks).
    /// The body's <c>days</c> array lists the weekday indices (0=Mon … 6=Sun)
    /// that should be flagged as Offsite working days.
    /// <para>
    /// Semantics (chip = ticked iff Offsite):
    ///   • Ticked, no entry yet         → create as Offsite (09:00–18:00).
    ///   • Ticked, entry is Onsite      → flip WorkType to Offsite, keep times/note.
    ///   • Ticked, entry already Offsite → no-op.
    ///   • Ticked, entry is Dayoff      → promote to Offsite (default 09:00–18:00).
    ///   • Unticked, entry is Offsite   → revert to Dayoff (clear times, clear "Offsite" note).
    ///   • Unticked, entry is Onsite    → LEAVE ALONE. The chip panel only
    ///                                    manages Offsite designation —
    ///                                    it must never silently nuke an
    ///                                    Onsite shift authored elsewhere.
    ///   • Unticked, entry is Dayoff / missing → no-op.
    /// </para>
    /// Admin only.
    /// </summary>
    [HttpPost("offsite/{userId:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SetOffsiteDays(int userId, [FromBody] OffsiteDaysRequest body)
    {
        var meRaw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(meRaw, out var meId)) return Unauthorized();

        if (!await CanViewAsync(meId, userId)) return Forbid();

        var target = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (target is null) return NotFound();

        var rawDays = body?.Days ?? new List<int>();
        var offsiteSet = rawDays.Where(d => d is >= 0 and <= 6).ToHashSet();

        var (weekStart, _) = CurrentPhWeekRange();
        var defaultStart = new TimeOnly(9, 0);
        var defaultEnd = new TimeOnly(18, 0);
        var horizonDates = Enumerable
            .Range(0, OffsiteHorizonWeeks * 7)
            .Select(i => weekStart.AddDays(i))
            .ToList();
        var weekDates = horizonDates.Take(7).ToList();

        var existing = await _db.ScheduleEntries
            .Where(s => s.UserId == target.Id && horizonDates.Contains(s.WorkDate))
            .ToDictionaryAsync(s => s.WorkDate);

        foreach (var d in horizonDates)
        {
            var wd = ((int)d.DayOfWeek + 6) % 7;
            var wantOffsite = offsiteSet.Contains(wd);

            if (!existing.TryGetValue(d, out var entry))
            {
                if (!wantOffsite) continue; // chip unticked + no row = nothing to do
                _db.ScheduleEntries.Add(new ScheduleEntry
                {
                    UserId = target.Id,
                    WorkDate = d,
                    IsWorking = true,
                    StartTime = defaultStart,
                    EndTime = defaultEnd,
                    Note = "Offsite",
                    WorkType = "Offsite",
                    UpdatedAt = DateTime.UtcNow,
                });
                continue;
            }

            var currentlyOffsite = entry.EffectiveWorkType.Equals("Offsite", StringComparison.OrdinalIgnoreCase);
            var currentlyOnsite  = entry.EffectiveWorkType.Equals("Onsite",  StringComparison.OrdinalIgnoreCase);

            if (wantOffsite)
            {
                if (currentlyOffsite) continue; // already correct
                // Flip to Offsite — this is the previously-broken path.
                // Whether the row was Onsite or Dayoff, we always end up
                // with a working Offsite shift after this.
                entry.IsWorking = true;
                entry.StartTime ??= defaultStart;
                entry.EndTime ??= defaultEnd;
                entry.WorkType = "Offsite";
                if (string.IsNullOrWhiteSpace(entry.Note)
                    || entry.Note!.Equals("Onsite", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Note = "Offsite";
                }
                entry.UpdatedAt = DateTime.UtcNow;
                continue;
            }

            // Chip unticked.
            if (currentlyOnsite) continue; // never destroy an Onsite shift here
            if (!currentlyOffsite) continue; // already Dayoff / unknown — nothing to undo

            // Was Offsite, admin unticked → revert to Dayoff.
            entry.IsWorking = false;
            entry.StartTime = null;
            entry.EndTime = null;
            entry.WorkType = "Dayoff";
            if (string.Equals(entry.Note, "Offsite", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(entry.Note))
            {
                entry.Note = string.Empty;
            }
            entry.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        // Re-read just the current week to report the refreshed chip state.
        var savedOffsite = await _db.ScheduleEntries
            .Where(s => s.UserId == target.Id && weekDates.Contains(s.WorkDate) && s.IsWorking)
            .ToListAsync();

        return new JsonResult(new
        {
            ok = true,
            user_id = target.Id,
            horizon_weeks = OffsiteHorizonWeeks,
            week_start = weekStart.ToString("yyyy-MM-dd"),
            days = savedOffsite
                .Where(s => s.EffectiveWorkType.Equals("Offsite", StringComparison.OrdinalIgnoreCase))
                .Select(s => ((int)s.WorkDate.DayOfWeek + 6) % 7)
                .ToArray(),
        });
    }

    // ------------------------------------------------------------------
    // Business-Unit visibility helpers (mirror AppController so the API
    // controller — which inherits ControllerBase — applies the same rules).
    // Admin accounts are filtered out of every result so they don't
    // appear in team-status / dashboard tiles.
    // ------------------------------------------------------------------
    private async Task<List<User>> VisibleUsersAsync()
    {
        // Pure admin: see every non-admin user.
        if (User.IsInRole(Models.Roles.Admin)
            || User.IsInRole(Models.Roles.Operations))
        {
            return await _db.Users
                .Where(u => u.Role != Models.Roles.Admin)
                .OrderBy(u => u.FullName)
                .ToListAsync();
        }

        var meRaw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(meRaw, out var meId)) return new List<User>();

        var me = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
        if (me is null) return new List<User>();

        // Program Manager: self + users whose Business Unit is in the
        // PgM's multi-BU list. Mirrors AppController.GetVisibleUsersAsync
        // so the dashboard team list and the /api endpoints stay in sync.
        if (User.IsInRole(Models.Roles.ProgramManager))
        {
            var buList = me.BusinessUnitList;
            if (buList.Count == 0)
            {
                return new List<User> { me };
            }
            return await _db.Users
                .Where(u =>
                    (u.Id == me.Id
                        || (u.BusinessUnit != null && buList.Contains(u.BusinessUnit)))
                    && u.Role != Models.Roles.Admin)
                .OrderBy(u => u.FullName)
                .ToListAsync();
        }

        // Project Manager: self + direct reports (ManagerId points at me).
        if (User.IsInRole(Models.Roles.Pm))
        {
            return await _db.Users
                .Where(u => (u.Id == me.Id || u.ManagerId == me.Id)
                            && u.Role != Models.Roles.Admin)
                .OrderBy(u => u.FullName)
                .ToListAsync();
        }

        // Plain employee: only self.
        return new List<User> { me };
    }

    private async Task<bool> CanViewAsync(int meId, int targetId)
    {
        if (meId == targetId) return true;
        if (User.IsInRole(Models.Roles.Admin)
            || User.IsInRole(Models.Roles.Operations)) return true;

        // Program Manager: target must live in one of the PgM's BUs.
        if (User.IsInRole(Models.Roles.ProgramManager))
        {
            var me = await _db.Users
                .Where(u => u.Id == meId)
                .Select(u => new { u.BusinessUnits })
                .FirstOrDefaultAsync();
            if (me is null) return false;
            var buList = string.IsNullOrWhiteSpace(me.BusinessUnits)
                ? new List<string>()
                : me.BusinessUnits
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            if (buList.Count == 0) return false;
            var target = await _db.Users
                .Where(u => u.Id == targetId)
                .Select(u => new { u.BusinessUnit })
                .FirstOrDefaultAsync();
            return target is not null
                && target.BusinessUnit != null
                && buList.Contains(target.BusinessUnit);
        }

        // Project Manager: target must be a direct report.
        if (User.IsInRole(Models.Roles.Pm))
        {
            return await _db.Users.AnyAsync(u =>
                u.Id == targetId && u.ManagerId == meId);
        }

        return false;
    }
}
