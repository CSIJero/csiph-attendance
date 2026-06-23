using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Background service that tops up <see cref="LeaveBalance"/> rows on
/// the 1st of every month. Each employee accrues hours per leave type
/// according to a built-in policy table. The accrual is idempotent —
/// the row stores <see cref="LeaveBalance.LastAccrualMonth"/> so a
/// month is only credited once even when the service restarts.
///
/// Policy (per month):
///   • Vacation : 6.67 h   (10 days / yr at 8 h/day)
///   • Sick     : 5.33 h   (8 days  / yr at 8 h/day)
///   • Personal : 2.67 h   (4 days  / yr at 8 h/day)
///
/// Admins do not accrue. New hires (CreatedAt later than the current
/// month start) are skipped until their first full calendar month.
/// </summary>
public class LeaveAccrualService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LeaveAccrualService> _log;

    public LeaveAccrualService(
        IServiceProvider services,
        ILogger<LeaveAccrualService> log)
    {
        _services = services;
        _log = log;
    }

    // Hours credited per calendar month, per leave type. Keep this in
    // one place so HR can review without grepping for magic numbers.
    public static readonly Dictionary<string, decimal> MonthlyAccrual = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["Vacation"] = 6.67m,
        ["Sick"]     = 5.33m,
        ["Personal"] = 2.67m,
    };

    // Annual cap — balance never exceeds this regardless of accrual.
    public static readonly Dictionary<string, decimal> AnnualCap = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["Vacation"] = 80m,   // 10 days
        ["Sick"]     = 64m,   // 8 days
        ["Personal"] = 32m,   // 4 days
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("LeaveAccrualService started.");

        // Run once at startup (catches restarts that happen after the
        // 1st of the month) then once every 6 hours. The per-row
        // LastAccrualMonth guard ensures idempotency.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AccrueAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "LeaveAccrualService tick failed.");
            }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task AccrueAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // We accrue at the START of the current calendar month. The
        // LastAccrualMonth check below blocks repeat credits within the
        // same month.
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(todayUtc.Year, todayUtc.Month, 1);

        var employees = await db.Users
            .Where(u => u.Approved
                     && u.Role != Roles.Admin)
            .ToListAsync(ct);

        int credited = 0;
        foreach (var user in employees)
        {
            // Skip employees whose record was created after the 1st of
            // this month — they get their first accrual next cycle.
            var hireMonth = new DateOnly(user.CreatedAt.Year, user.CreatedAt.Month, 1);
            if (hireMonth > monthStart) continue;

            foreach (var (leaveType, hours) in MonthlyAccrual)
            {
                var row = await db.LeaveBalances
                    .FirstOrDefaultAsync(b => b.UserId == user.Id && b.LeaveType == leaveType, ct);

                if (row is null)
                {
                    row = new LeaveBalance
                    {
                        UserId = user.Id,
                        LeaveType = leaveType,
                        HoursRemaining = 0,
                        HoursAccruedYtd = 0,
                        HoursUsedYtd = 0,
                        LastAccrualMonth = null,
                    };
                    db.LeaveBalances.Add(row);
                }

                if (row.LastAccrualMonth == monthStart) continue;

                // Reset YTD totals on January.
                if (monthStart.Month == 1
                    && (row.LastAccrualMonth is null
                        || row.LastAccrualMonth.Value.Year < monthStart.Year))
                {
                    row.HoursAccruedYtd = 0;
                    row.HoursUsedYtd = 0;
                }

                var cap = AnnualCap.GetValueOrDefault(leaveType, decimal.MaxValue);
                var headroom = cap - row.HoursRemaining;
                if (headroom <= 0)
                {
                    // Still mark the month so we don't keep retrying.
                    row.LastAccrualMonth = monthStart;
                    row.UpdatedAt = DateTime.UtcNow;
                    continue;
                }

                var credit = Math.Min(hours, headroom);
                row.HoursRemaining += credit;
                row.HoursAccruedYtd += credit;
                row.LastAccrualMonth = monthStart;
                row.UpdatedAt = DateTime.UtcNow;
                credited++;
            }
        }

        if (credited > 0)
        {
            await db.SaveChangesAsync(ct);
            _log.LogInformation(
                "LeaveAccrualService credited {Count} balance rows for {Month:yyyy-MM}.",
                credited, monthStart);
        }
    }
}
