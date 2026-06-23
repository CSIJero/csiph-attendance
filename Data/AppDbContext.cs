using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AttendanceMonitoring.Data;

public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public DbSet<AttendanceAuditLog> AttendanceAuditLogs => Set<AttendanceAuditLog>();
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<AttendanceEditRequest> AttendanceEditRequests => Set<AttendanceEditRequest>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<ScheduleAmendment> ScheduleAmendments => Set<ScheduleAmendment>();
    public DbSet<QuotaResetRequest> QuotaResetRequests => Set<QuotaResetRequest>();
    public DbSet<RoleDefinition> RoleDefinitions => Set<RoleDefinition>();

    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<RuntimeSetting> RuntimeSettings => Set<RuntimeSetting>();

    // ASP.NET Core data-protection keys. Stored in the same Postgres DB so
    // auth cookies survive Render container redeploys (free tier has no
    // persistent disk). Without this every deploy rotates the key ring and
    // silently logs everyone out.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // SQLite stores DateTime as text and does not preserve DateTimeKind, so
    // values come back as Kind=Unspecified. We always store UTC, so this
    // converter re-tags them as Utc on read. Without this, ToString("o")
    // emits no "Z" suffix and clients render the timestamp 8 hours off PHT.
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AttendanceAuditLog>(e =>
        {
            e.ToTable("attendance_audit_log");
            e.HasIndex(a => a.AttendanceId);
            e.HasIndex(a => a.EditedByUserId);
            e.HasIndex(a => a.EditedAt);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.EmployeeId);
            // Self-referencing FK so an employee can be assigned to a PM
            // (or any other user) without forcing a separate teams table.
            // SetNull on delete keeps direct-reports rows alive when the
            // manager is removed.
            e.HasOne(u => u.Manager)
                .WithMany()
                .HasForeignKey(u => u.ManagerId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(u => u.ManagerId);

            // Admin-managed display label (e.g. "Senior Engineer"). Null
            // is fine — the column is purely cosmetic. SetNull keeps user
            // rows alive when an admin removes a label from the list.
            e.HasOne(u => u.RoleLabel)
                .WithMany()
                .HasForeignKey(u => u.RoleLabelId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(u => u.RoleLabelId);
        });

        b.Entity<RoleDefinition>(e =>
        {
            e.ToTable("role_definitions");
            e.HasIndex(r => r.Name).IsUnique();
        });

        b.Entity<Attendance>(e =>
        {
            e.ToTable("attendance");
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.WorkDate);
            e.HasOne(a => a.User)
                .WithMany(u => u.Attendances)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ScheduleEntry>(e =>
        {
            e.ToTable("schedule_entries");
            e.HasIndex(s => s.UserId);
            e.HasIndex(s => new { s.UserId, s.WorkDate })
                .IsUnique()
                .HasDatabaseName("uq_user_workdate");
            e.HasOne(s => s.User)
                .WithMany(u => u.ScheduleEntries)
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NotificationLog>(e =>
        {
            e.ToTable("notification_log");
            e.HasIndex(n => n.SentAt);
            e.HasIndex(n => n.UserId);
            e.HasIndex(n => n.ActionTaken);
            e.HasOne(n => n.ActionByUser)
                .WithMany()
                .HasForeignKey(n => n.ActionByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AttendanceEditRequest>(e =>
        {
            e.ToTable("attendance_edit_requests");
            e.HasIndex(r => r.AttendanceId);
            e.HasIndex(r => r.RequestedByUserId);
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.Attendance)
                .WithMany()
                .HasForeignKey(r => r.AttendanceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.RequestedByUser)
                .WithMany()
                .HasForeignKey(r => r.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.DecidedByUser)
                .WithMany()
                .HasForeignKey(r => r.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.ToTable("leave_requests");
            e.HasIndex(r => r.UserId);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.StartDate);
            // Map HoursPerDay as numeric(4,2) on Postgres for precision /
            // explicit storage; SQLite ignores precision but the column
            // type still survives as the right CLR conversion.
            e.Property(r => r.HoursPerDay).HasColumnType("numeric(4,2)");
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.RequestedByUser)
                .WithMany()
                .HasForeignKey(r => r.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.DecidedByUser)
                .WithMany()
                .HasForeignKey(r => r.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ScheduleAmendment>(e =>
        {
            e.ToTable("schedule_amendments");
            e.HasIndex(r => r.UserId);
            e.HasIndex(r => r.RequestedByUserId);
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.RequestedByUser)
                .WithMany()
                .HasForeignKey(r => r.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.DecidedByUser)
                .WithMany()
                .HasForeignKey(r => r.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<QuotaResetRequest>(e =>
        {
            e.ToTable("quota_reset_requests");
            e.HasIndex(r => r.UserId);
            e.HasIndex(r => r.RequestedByUserId);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.Kind);
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.RequestedByUser)
                .WithMany()
                .HasForeignKey(r => r.RequestedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.DecidedByUser)
                .WithMany()
                .HasForeignKey(r => r.DecidedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Holiday>(e =>
        {
            e.ToTable("holidays");
            // Same (country, date) tuple should never appear twice.
            e.HasIndex(h => new { h.Country, h.Date })
                .IsUnique()
                .HasDatabaseName("uq_holiday_country_date");
            e.HasIndex(h => h.Date);
        });

        b.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.HasIndex(s => s.BusinessUnit);
            e.HasIndex(s => s.IsActive);
        });

        b.Entity<LeaveBalance>(e =>
        {
            e.ToTable("leave_balances");
            e.HasIndex(r => new { r.UserId, r.LeaveType })
                .IsUnique()
                .HasDatabaseName("uq_leave_balance_user_type");
            e.HasOne(r => r.User)
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            // One row per (user, endpoint) — re-subscribing the same
            // device just refreshes the keys.
            e.HasIndex(p => new { p.UserId, p.Endpoint })
                .IsUnique()
                .HasDatabaseName("uq_push_user_endpoint");
            e.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

            b.Entity<RuntimeSetting>(e =>
            {
                e.ToTable("runtime_settings");
                e.HasIndex(r => r.Key)
                .IsUnique()
                .HasDatabaseName("uq_runtime_settings_key");
            });

        b.Entity<Attendance>(e =>
        {
            e.HasOne<Site>()
                .WithMany()
                .HasForeignKey(a => a.CheckInSiteId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Coalition roster grid — one row per (date, slot) cell. The
        // unique index keeps the grid from accumulating duplicates when
        // the seeder runs against a partially-populated database.
        // (Tables are retained for legacy data preservation; the BU2
        // PH Schedule UI has been retired so EF no longer manages
        // these entities here.)

        // Tag every DateTime / DateTime? column as UTC on read.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            foreach (var prop in entityType.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime))
                {
                    prop.SetValueConverter(UtcConverter);
                }
                else if (prop.ClrType == typeof(DateTime?))
                {
                    prop.SetValueConverter(NullableUtcConverter);
                }
            }
        }
    }
}
