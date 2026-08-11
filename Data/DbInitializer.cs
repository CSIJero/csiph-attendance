using System.Globalization;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext db)
    {
        // Create the schema if it doesn't exist (mirrors Flask's db.create_all()).
        await db.Database.EnsureCreatedAsync();

        // ASP.NET data-protection key table. Needed on both providers so the
        // auth cookie key ring survives container restarts. EnsureCreated
        // is a no-op on already-provisioned production DBs, so add the
        // table ourselves when missing.
        await EnsureDataProtectionKeysTableAsync(db);

        // The Ensure*Column migrations below are SQLite-only and exist to
        // patch the long-lived local attendance.db. On a fresh Postgres
        // database (Render's free tier), EnsureCreated already produced all
        // columns from the current model, so the migrations are no-ops and
        // their PRAGMA / sqlite_master syntax would just throw.
        if (db.Database.IsSqlite())
        {
            await EnsureLoginColumnsAsync(db);
            await EnsureBusinessUnitColumnAsync(db);
            await EnsurePresenceStateColumnAsync(db);
            await EnsureLunchStartedAtColumnAsync(db);
            await EnsureAttendancePhotoColumnsAsync(db);
            await EnsureApprovedColumnAsync(db);
            await EnsureNotificationLogTableAsync(db);
        }

        // Password-reset columns are needed on both SQLite (local) and
        // Postgres (Render). Postgres natively supports IF NOT EXISTS so
        // it's safe to run unconditionally on every startup.
        await EnsurePasswordResetColumnsAsync(db);
        await EnsureReminderOptOutColumnAsync(db);
        await EnsureOfflineSinceColumnAsync(db);
        await EnsureShortBreakColumnsAsync(db);
        await EnsureLunchQuotaColumnsAsync(db);
        await EnsureManagerColumnAsync(db);
        await EnsureRoleLabelMigrationAsync(db);
        await EnsureRoleDefinitionTierColumnsAsync(db);
        await SeedBuiltInRoleDefinitionsAsync(db);
        await EnsureBusinessUnitsColumnAsync(db);
        await EnsureLastLoginLocationColumnAsync(db);
        await EnsureLastLoginAgentColumnAsync(db);
        await EnsureLastLoginGpsColumnsAsync(db);
        await NormaliseLegacyCountryCodeAsync(db);
        await EnsureAttendanceEditRequestsTableAsync(db);
        await EnsureAttendanceEditRequestProofPhotoColumnAsync(db);
        await EnsureLeaveRequestsTableAsync(db);

        // Switch the schedule_* tables from the old weekly template
        // (Weekday INTEGER 0..6) to the new per-date model (WorkDate TEXT
        // ISO yyyy-MM-dd). Runs BEFORE EnsureScheduleAmendmentsTableAsync
        // so the legacy schedule_amendments table is dropped first and
        // then recreated with the new schema.
        await EnsurePerDateScheduleSchemaAsync(db);

        await EnsureScheduleAmendmentsTableAsync(db);
        await EnsureQuotaResetRequestsTableAsync(db);
        await EnsureNotificationLogActionColumnsAsync(db);

        // Convert any legacy DateOnly columns that earlier deploys created
        // as TEXT on Postgres over to native DATE. EF Core 8 maps DateOnly
        // to date, so a TEXT column makes every range query throw
        // 'operator does not exist: text >= date'. SQLite keeps TEXT (its
        // DateOnly storage is always text).
        await EnsureDateColumnTypesAsync(db);

        // Promote leave_requests.StartDate / EndDate from DATE to TIMESTAMP
        // so the form can capture the time-of-day the worker will be out.
        // Older deploys stored these as DATE (or TEXT before that); this
        // helper widens them in place. SQLite stores both DateOnly and
        // DateTime as ISO text, so no schema change is needed there.
        await EnsureLeaveRequestDateTimeColumnsAsync(db);

        // FaceHash / FaceEnrolledAt must exist before any user query below
        // (for example RemoveDemoAccountAsync) because EF selects mapped
        // columns even when the calling code does not read them directly.
        // Run this early so legacy Postgres schemas are patched first.
        await EnsureUserFaceColumnsAsync(db);

        // Deactivation support: add IsActive column to allow marking users
        // as inactive without deleting their historical data.
        await EnsureIsActiveColumnAsync(db);

        // One-time cleanup: drop the bundled demo (jdoe) account if it's
        // still hanging around from a pre-launch seed. Idempotent — no-op
        // once the row is gone.
        await RemoveDemoAccountAsync(db);

        // Coalition roster grid (BU2 PH Schedule) has been retired.
        // We keep the Source column on schedule_entries for legacy
        // rows that may have been authored by the old roster save
        // handler. The shift_assignments / shift_day_notes tables are
        // left in place on existing databases (no DROP) but are no
        // longer created on fresh installs.
        await EnsureScheduleEntrySourceColumnAsync(db);

        // Schedule-entry WorkType column for the Onsite / Offsite /
        // Dayoff classification that drives the late-arrival grace
        // rules. Idempotent — adds the column once on legacy databases.
        await EnsureScheduleEntryWorkTypeColumnAsync(db);

        // ProofPhoto columns on leave_requests and schedule_amendments so
        // all request types require image proof.
        await EnsureLeaveRequestProofPhotoColumnAsync(db);
        await EnsureScheduleAmendmentProofPhotoColumnAsync(db);

        // New columns and tables for v2026 feature drop (geofencing,
        // holidays, activity tag, leave accrual, face match, API keys,
        // push subs). Each helper is idempotent on both providers.
        await EnsureAttendanceActivityAndGpsColumnsAsync(db);
        await EnsureHolidaysTableAsync(db);
        await EnsureSitesTableAsync(db);
        await EnsureLeaveBalancesTableAsync(db);
        await EnsurePushSubscriptionsTableAsync(db);
        await EnsureRuntimeSettingsTableAsync(db);
        await SeedDefaultHolidaysAsync(db);

        await SeedAdminAsync(db);
    }

    /// <summary>
    /// Creates the <c>DataProtectionKeys</c> table that the ASP.NET Core
    /// data-protection EF provider expects. Without a persistent key ring
    /// every container restart (Render free-tier deploys do this on every
    /// push) rotates the key material and silently invalidates every
    /// auth cookie — users get bounced back to /Account/Login mid-session.
    /// Idempotent on both SQLite and Postgres.
    /// </summary>
    private static async Task EnsureDataProtectionKeysTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            // Quoted identifiers match the EF default (PascalCase) so the
            // PersistKeysToDbContext provider can read/write through the
            // generated SQL without us overriding the table name.
            const string pgSql = @"
                CREATE TABLE IF NOT EXISTS ""DataProtectionKeys"" (
                    ""Id""           SERIAL PRIMARY KEY,
                    ""FriendlyName"" TEXT NULL,
                    ""Xml""          TEXT NULL
                );";
            await db.Database.ExecuteSqlRawAsync(pgSql);
            return;
        }

        if (!db.Database.IsSqlite()) return;

        if (await TableExistsAsync(db, "DataProtectionKeys")) return;

        const string sqliteSql = @"
            CREATE TABLE ""DataProtectionKeys"" (
                ""Id""           INTEGER NOT NULL CONSTRAINT ""PK_DataProtectionKeys"" PRIMARY KEY AUTOINCREMENT,
                ""FriendlyName"" TEXT NULL,
                ""Xml""          TEXT NULL
            );";
        await db.Database.ExecuteSqlRawAsync(sqliteSql);
    }

    /// <summary>
    /// Creates the <c>notification_log</c> table on databases that were
    /// provisioned before the offline-notifier audit log shipped. EF's
    /// <see cref="DatabaseFacade.EnsureCreatedAsync"/> only seeds the schema
    /// for empty databases, so any pre-existing DB needs this top-up.
    /// </summary>
    private static async Task EnsureNotificationLogTableAsync(AppDbContext db)
    {
        if (await TableExistsAsync(db, "notification_log")) return;

        const string sql = @"
            CREATE TABLE notification_log (
                Id            INTEGER NOT NULL CONSTRAINT PK_notification_log PRIMARY KEY AUTOINCREMENT,
                SentAt        TEXT    NOT NULL,
                Level         TEXT    NOT NULL,
                Status        TEXT    NOT NULL,
                UserId        INTEGER NULL,
                UserFullName  TEXT    NULL,
                Username      TEXT    NULL,
                Recipients    TEXT    NOT NULL,
                Subject       TEXT    NOT NULL,
                OfflineMinutes INTEGER NOT NULL,
                ErrorMessage  TEXT    NULL
            );
            CREATE INDEX IX_notification_log_SentAt ON notification_log (SentAt);
            CREATE INDEX IX_notification_log_UserId ON notification_log (UserId);
        ";
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>
    /// Adds the admin-enforcement columns (ActionTaken, ActionByUserId,
    /// ActionAt, ActionNote) to the existing <c>notification_log</c> table.
    /// Each row is one detected offline-during-shift violation; these
    /// columns let an admin record what enforcement action (if any) was
    /// taken in response. Safe to call on every startup.
    /// </summary>
    private static async Task EnsureNotificationLogActionColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN IF NOT EXISTS \"ActionTaken\" VARCHAR(20) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN IF NOT EXISTS \"ActionByUserId\" INTEGER NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN IF NOT EXISTS \"ActionAt\" TIMESTAMP NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN IF NOT EXISTS \"ActionNote\" VARCHAR(500) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_notification_log_ActionTaken\" ON notification_log (\"ActionTaken\");");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "notification_log");
        if (!existing.Contains("ActionTaken"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN ActionTaken TEXT NULL;");
        }
        if (!existing.Contains("ActionByUserId"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN ActionByUserId INTEGER NULL;");
        }
        if (!existing.Contains("ActionAt"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN ActionAt TEXT NULL;");
        }
        if (!existing.Contains("ActionNote"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE notification_log ADD COLUMN ActionNote TEXT NULL;");
        }
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_notification_log_ActionTaken ON notification_log (ActionTaken);");
        }
        catch { /* old SQLite without IF NOT EXISTS \u2014 ignore */ }
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = table;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is string;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Adds <c>BusinessUnit</c> to existing <c>users</c> tables that were
    /// created before the field was introduced. Safe to call repeatedly.
    /// </summary>
    private static async Task EnsureBusinessUnitColumnAsync(AppDbContext db)
    {
        var existing = await GetColumnsAsync(db, "users");
        if (existing.Contains("BusinessUnit")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN BusinessUnit TEXT NULL;");
    }

    /// <summary>
    /// Adds <c>PresenceState</c> to existing <c>users</c> tables. Defaults to
    /// "offline" so users have to heartbeat in before they're marked online.
    /// Safe to call repeatedly.
    /// </summary>
    private static async Task EnsurePresenceStateColumnAsync(AppDbContext db)
    {
        var existing = await GetColumnsAsync(db, "users");
        if (existing.Contains("PresenceState")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN PresenceState TEXT NOT NULL DEFAULT 'offline';");
    }

    /// <summary>
    /// Adds <c>LunchStartedAt</c> to existing <c>users</c> tables. Records the
    /// timestamp the current lunch break began so the notifier can suppress
    /// offline alerts for the duration. Safe to call repeatedly.
    /// </summary>
    private static async Task EnsureLunchStartedAtColumnAsync(AppDbContext db)
    {
        var existing = await GetColumnsAsync(db, "users");
        if (existing.Contains("LunchStartedAt")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN LunchStartedAt TEXT NULL;");
    }

    /// <summary>
    /// Adds <c>Approved</c> to existing <c>users</c> tables. Pre-existing
    /// accounts are auto-approved so the upgrade doesn't lock anyone out;
    /// only freshly registered accounts (created after this column shipped)
    /// will land with <c>Approved=0</c> and need admin sign-off.
    /// </summary>
    private static async Task EnsureApprovedColumnAsync(AppDbContext db)
    {
        var existing = await GetColumnsAsync(db, "users");
        if (existing.Contains("Approved")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN Approved INTEGER NOT NULL DEFAULT 0;");
        // Grandfather everyone who already existed pre-upgrade.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE users SET Approved = 1;");
    }

    /// <summary>
    /// Adds the <c>CheckInPhoto</c> / <c>CheckOutPhoto</c> columns to existing
    /// <c>attendance</c> tables that were created before the selfie feature
    /// shipped. Safe to call repeatedly.
    /// </summary>
    private static async Task EnsureAttendancePhotoColumnsAsync(AppDbContext db)
    {
        var existing = await GetColumnsAsync(db, "attendance");

        var statements = new List<string>();
        if (!existing.Contains("CheckInPhoto"))
            statements.Add("ALTER TABLE attendance ADD COLUMN CheckInPhoto TEXT NULL;");
        if (!existing.Contains("CheckOutPhoto"))
            statements.Add("ALTER TABLE attendance ADD COLUMN CheckOutPhoto TEXT NULL;");

        foreach (var sql in statements)
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(AppDbContext db, string table)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing.Add(reader.GetString(1));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
        return existing;
    }

    /// <summary>
    /// EF's <c>EnsureCreatedAsync</c> only creates missing tables, never adds
    /// columns to existing ones. For databases provisioned before the login
    /// telemetry feature shipped, add the new <c>users</c> columns by hand so
    /// we don't lose existing data.
    /// </summary>
    private static async Task EnsureLoginColumnsAsync(AppDbContext db)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(users);";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Column 1 ("name") is the column name in PRAGMA table_info.
                existing.Add(reader.GetString(1));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }

        var statements = new List<string>();
        if (!existing.Contains("LastLoginIp"))
            statements.Add("ALTER TABLE users ADD COLUMN LastLoginIp TEXT NULL;");
        if (!existing.Contains("LastLoginHost"))
            statements.Add("ALTER TABLE users ADD COLUMN LastLoginHost TEXT NULL;");
        if (!existing.Contains("LastLoginAt"))
            statements.Add("ALTER TABLE users ADD COLUMN LastLoginAt TEXT NULL;");

        foreach (var sql in statements)
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    /// <summary>
    /// Adds runtime_settings table for admin-tunable key/value options.
    /// </summary>
    private static async Task EnsureRuntimeSettingsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            const string pgSql = @"
                CREATE TABLE IF NOT EXISTS runtime_settings (
                    ""Id""        SERIAL PRIMARY KEY,
                    ""Key""       VARCHAR(80) NOT NULL,
                    ""Value""     VARCHAR(2000) NOT NULL,
                    ""UpdatedAt"" TIMESTAMP NOT NULL DEFAULT NOW()
                );
                CREATE UNIQUE INDEX IF NOT EXISTS uq_runtime_settings_key
                    ON runtime_settings (""Key"");";
            await db.Database.ExecuteSqlRawAsync(pgSql);
            return;
        }

        if (!db.Database.IsSqlite()) return;

        if (!await TableExistsAsync(db, "runtime_settings"))
        {
            const string sqliteSql = @"
                CREATE TABLE runtime_settings (
                    Id INTEGER NOT NULL CONSTRAINT PK_runtime_settings PRIMARY KEY AUTOINCREMENT,
                    Key TEXT NOT NULL,
                    Value TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX uq_runtime_settings_key ON runtime_settings (Key);";
            await db.Database.ExecuteSqlRawAsync(sqliteSql);
            return;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_runtime_settings_key ON runtime_settings (Key);");
        }
        catch
        {
            // best-effort for older SQLite variants
        }
    }

    private static async Task SeedAdminAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync(u => u.Username == "admin")) return;

        var admin = new User
        {
            Username = "admin",
            Email = "admin@example.com",
            FullName = "System Administrator",
            Role = "admin",
            PasswordHash = PasswordHasher.Hash("Admin123!"),
            CreatedAt = DateTime.UtcNow,
            Approved = true,
        };

        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds the password-reset columns to <c>users</c> on whichever provider
    /// is in use. Postgres has native <c>IF NOT EXISTS</c> support so it's
    /// safe to call on every boot; on SQLite we sniff PRAGMA first because
    /// the dialect doesn't support that clause for ADD COLUMN.
    /// </summary>
    private static async Task EnsurePasswordResetColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"PasswordResetTokenHash\" VARCHAR(128) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"PasswordResetExpiresAt\" TIMESTAMP NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"MustChangePassword\" BOOLEAN NOT NULL DEFAULT FALSE;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"IsSupport\" BOOLEAN NOT NULL DEFAULT FALSE;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("PasswordResetTokenHash"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN PasswordResetTokenHash TEXT NULL;");
        }
        if (!existing.Contains("PasswordResetExpiresAt"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN PasswordResetExpiresAt TEXT NULL;");
        }
        if (!existing.Contains("MustChangePassword"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0;");
        }
        if (!existing.Contains("IsSupport"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IsSupport INTEGER NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>
    /// Adds <c>OfflineSince</c> to <c>users</c> on both Postgres and SQLite.
    /// Stamps the UTC moment a user's <c>PresenceState</c> transitioned to
    /// <c>"offline"</c>; the offline-notifier uses this column directly
    /// (instead of <c>LastSeen</c> staleness) so the alert cadence reflects
    /// PC-lock duration, not heartbeat health.
    /// </summary>
    private static async Task EnsureOfflineSinceColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"OfflineSince\" TIMESTAMP NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("OfflineSince"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN OfflineSince TEXT NULL;");
        }
    }

    /// <summary>
    /// Adds <c>OptOutReminderEmails</c> to <c>users</c> on both providers.
    /// When true, reminder emails addressed to the employee are skipped,
    /// while manager/admin recipients continue receiving alerts.
    /// </summary>
    private static async Task EnsureReminderOptOutColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"OptOutReminderEmails\" BOOLEAN NOT NULL DEFAULT FALSE;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("OptOutReminderEmails"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN OptOutReminderEmails INTEGER NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>
    /// Adds <c>LastLoginLocation</c> to <c>users</c> on both Postgres and
    /// SQLite. Populated at sign-in from a free IP-geolocation service so
    /// the Team status table can show "City, Country" next to the IP
    /// instead of the raw last-octet hostname. Idempotent.
    /// </summary>
    private static async Task EnsureLastLoginLocationColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LastLoginLocation\" VARCHAR(128) NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("LastLoginLocation"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LastLoginLocation TEXT NULL;");
        }
    }

    /// <summary>
    /// Adds <c>LastLoginAgent</c> to <c>users</c> on both Postgres and
    /// SQLite. Captures a compact browser/OS label parsed from the
    /// User-Agent header at sign-in so admins can distinguish users who
    /// share a public IP (same office wifi). Idempotent.
    /// </summary>
    private static async Task EnsureLastLoginAgentColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LastLoginAgent\" VARCHAR(256) NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("LastLoginAgent"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LastLoginAgent TEXT NULL;");
        }
    }

    /// <summary>
    /// Adds the three GPS-related columns (<c>LastLoginLocationSource</c>,
    /// <c>LastLoginLatitude</c>, <c>LastLoginLongitude</c>) to
    /// <c>users</c> on both Postgres and SQLite. Populated by the
    /// hybrid geolocation flow: the browser asks for
    /// <c>navigator.geolocation</c> on first page load after sign-in;
    /// when permission is granted the coordinates are POSTed to
    /// <c>/api/location/set</c> and the server reverse-geocodes them to
    /// overwrite the (less accurate) IP-based location. Idempotent.
    /// </summary>
    private static async Task EnsureLastLoginGpsColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LastLoginLocationSource\" VARCHAR(8) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LastLoginLatitude\" DOUBLE PRECISION NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LastLoginLongitude\" DOUBLE PRECISION NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("LastLoginLocationSource"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LastLoginLocationSource TEXT NULL;");
        }
        if (!existing.Contains("LastLoginLatitude"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LastLoginLatitude REAL NULL;");
        }
        if (!existing.Contains("LastLoginLongitude"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LastLoginLongitude REAL NULL;");
        }
    }

    /// <summary>
    /// Adds the three short-break columns (<c>BreakStartedAt</c>,
    /// <c>BreaksUsedToday</c>, <c>BreaksUsedDate</c>) to <c>users</c> on
    /// both Postgres and SQLite. Used by the topbar Break button so an
    /// employee can take up to <see cref="Constants.MaxShortBreaksPerDay"/>
    /// short breaks per work day in addition to the 1-hour lunch.
    /// Idempotent; safe to call on every startup.
    /// </summary>
    private static async Task EnsureShortBreakColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"BreakStartedAt\" TIMESTAMP NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"BreaksUsedToday\" INTEGER NOT NULL DEFAULT 0;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"BreaksUsedDate\" DATE NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("BreakStartedAt"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN BreakStartedAt TEXT NULL;");
        }
        if (!existing.Contains("BreaksUsedToday"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN BreaksUsedToday INTEGER NOT NULL DEFAULT 0;");
        }
        if (!existing.Contains("BreaksUsedDate"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN BreaksUsedDate TEXT NULL;");
        }
    }

    /// <summary>
    /// Adds the two lunch-quota columns (<c>LunchesUsedToday</c>,
    /// <c>LunchesUsedDate</c>) to <c>users</c>. Enforces the
    /// <see cref="Constants.MaxLunchesPerDay"/> cap so each employee can
    /// only start lunch once per PHT calendar day. Idempotent.
    /// </summary>
    private static async Task EnsureLunchQuotaColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LunchesUsedToday\" INTEGER NOT NULL DEFAULT 0;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"LunchesUsedDate\" DATE NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("LunchesUsedToday"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LunchesUsedToday INTEGER NOT NULL DEFAULT 0;");
        }
        if (!existing.Contains("LunchesUsedDate"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN LunchesUsedDate TEXT NULL;");
        }
    }

    /// <summary>
    /// Adds <c>ManagerId</c> self-referencing FK column to <c>users</c> on
    /// both Postgres and SQLite. SetNull on delete keeps direct-reports
    /// rows alive (the FK constraint is added only on Postgres; SQLite
    /// ALTER TABLE can't add constraints retroactively but EF still
    /// enforces SetNull through the model on save).
    /// </summary>
    private static async Task EnsureManagerColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"ManagerId\" INTEGER NULL REFERENCES users(\"Id\") ON DELETE SET NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS ix_users_managerid ON users(\"ManagerId\");");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("ManagerId"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN ManagerId INTEGER NULL REFERENCES users(Id) ON DELETE SET NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS ix_users_managerid ON users(ManagerId);");
        }
    }

    /// <summary>
    /// One-shot migration that retires the free-text <c>JobTitle</c>
    /// column in favour of an admin-managed <c>role_definitions</c>
    /// picklist referenced via a new <c>RoleLabelId</c> FK on <c>users</c>.
    /// Creates the lookup table on both providers, drops the legacy column
    /// when present, and adds the new FK column. Idempotent on every boot.
    /// </summary>
    private static async Task EnsureRoleLabelMigrationAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS role_definitions (
                    ""Id""        SERIAL PRIMARY KEY,
                    ""Name""      VARCHAR(80) NOT NULL,
                    ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT NOW()
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_role_definitions_name ON role_definitions(\"Name\");");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"RoleLabelId\" INTEGER NULL REFERENCES role_definitions(\"Id\") ON DELETE SET NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS ix_users_rolelabelid ON users(\"RoleLabelId\");");
            // Retire the legacy free-text job title column.
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users DROP COLUMN IF EXISTS \"JobTitle\";");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        if (!await TableExistsAsync(db, "role_definitions"))
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE role_definitions (
                    Id        INTEGER NOT NULL CONSTRAINT PK_role_definitions PRIMARY KEY AUTOINCREMENT,
                    Name      TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX ix_role_definitions_name ON role_definitions(Name);");
        }

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("RoleLabelId"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN RoleLabelId INTEGER NULL REFERENCES role_definitions(Id) ON DELETE SET NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS ix_users_rolelabelid ON users(RoleLabelId);");
        }

        if (existing.Contains("JobTitle"))
        {
            // SQLite 3.35+ supports DROP COLUMN. The bundled provider in
            // EF Core 8 is recent enough; if a very old SQLite is in play
            // the catch below leaves the orphan column behind harmlessly.
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE users DROP COLUMN JobTitle;");
            }
            catch
            {
                // Older SQLite — column stays but is unmapped by EF, no harm.
            }
        }
    }

    /// <summary>
    /// Adds the <c>BaseRole</c> + <c>IsBuiltIn</c> columns to
    /// <c>role_definitions</c> so each row can represent a real role with
    /// a canonical permission tier (employee / pm / program_manager /
    /// admin) instead of being a display-only label. Existing rows default
    /// to <c>employee</c> which is the safest tier — admins can promote
    /// them from the Configuration page afterwards.
    /// </summary>
    private static async Task EnsureRoleDefinitionTierColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE role_definitions ADD COLUMN IF NOT EXISTS \"BaseRole\" VARCHAR(32) NOT NULL DEFAULT 'employee';");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE role_definitions ADD COLUMN IF NOT EXISTS \"IsBuiltIn\" BOOLEAN NOT NULL DEFAULT FALSE;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "role_definitions");
        if (!existing.Contains("BaseRole"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE role_definitions ADD COLUMN BaseRole TEXT NOT NULL DEFAULT 'employee';");
        }
        if (!existing.Contains("IsBuiltIn"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE role_definitions ADD COLUMN IsBuiltIn INTEGER NOT NULL DEFAULT 0;");
        }
    }

    /// <summary>
    /// Seeds the five canonical roles into <c>role_definitions</c> on the
    /// very first boot after this feature ships, marking them
    /// <c>IsBuiltIn=true</c> so the admin can't delete them from the
    /// Configuration page. Idempotent: skips any row already present by
    /// name (case-insensitive).
    /// </summary>
    private static async Task SeedBuiltInRoleDefinitionsAsync(AppDbContext db)
    {
        var seeds = new (string Name, string BaseRole)[]
        {
            ("Administrator",     Models.Roles.Admin),
            ("Program Manager",   Models.Roles.ProgramManager),
            ("Project Manager",   Models.Roles.Pm),
            ("Operations",        Models.Roles.Operations),
            ("Employee",          Models.Roles.Employee),
        };

        // Pull existing names once — cheaper than four round-trips.
        var existingNames = await db.RoleDefinitions
            .Select(r => r.Name)
            .ToListAsync();
        var existing = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        var toAdd = new List<Models.RoleDefinition>();
        foreach (var seed in seeds)
        {
            if (existing.Contains(seed.Name)) continue;
            toAdd.Add(new Models.RoleDefinition
            {
                Name = seed.Name,
                BaseRole = seed.BaseRole,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (toAdd.Count > 0)
        {
            db.RoleDefinitions.AddRange(toAdd);
            await db.SaveChangesAsync();
        }

        // Promote any existing rows that match a built-in name (e.g. a
        // manually inserted "Administrator") so they pick up the flag
        // and base role. Done after the insert so we don't fight unique
        // index races with the seed batch.
        var byName = seeds.ToDictionary(s => s.Name, s => s.BaseRole, StringComparer.OrdinalIgnoreCase);
        var rows = await db.RoleDefinitions.ToListAsync();
        var dirty = false;
        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.Name, out var canonical))
            {
                if (!row.IsBuiltIn) { row.IsBuiltIn = true; dirty = true; }
                if (!string.Equals(row.BaseRole, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    row.BaseRole = canonical;
                    dirty = true;
                }
            }
        }
        if (dirty) await db.SaveChangesAsync();
    }

    /// <summary>
    /// Adds the <c>BusinessUnits</c> column (semicolon-separated multi-BU
    /// list used by Program Managers) to <c>users</c> on both Postgres
    /// and SQLite. Idempotent and safe on every boot.
    /// </summary>
    private static async Task EnsureBusinessUnitsColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"BusinessUnits\" VARCHAR(500) NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("BusinessUnits"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN BusinessUnits TEXT NULL;");
        }
    }

    /// <summary>
    /// One-shot data normalisation: rewrite any legacy <c>BusinessUnit</c>
    /// values that still use the long "(India)" suffix to the ISO-3166
    /// "(IN)" code. Idempotent and cheap — the WHERE clause skips already-
    /// migrated rows. Runs on both Postgres and SQLite.
    /// </summary>
    private static async Task NormaliseLegacyCountryCodeAsync(AppDbContext db)
    {
        // Postgres uses quoted column names; SQLite is case-insensitive.
        var column = db.Database.IsNpgsql() ? "\"BusinessUnit\"" : "BusinessUnit";
        var sql =
            $"UPDATE users SET {column} = REPLACE({column}, '(India)', '(IN)') " +
            $"WHERE {column} LIKE '%(India)%';";
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // Old DBs may not even have the column yet — silently swallow;
            // EnsureBusinessUnitColumnAsync runs earlier and the next boot
            // will pick it up.
        }
    }

    /// <summary>
    /// Creates the <c>attendance_edit_requests</c> table for the employee
    /// override-request workflow (employees ask to fix a clock-in/out and
    /// an admin or PM approves before it lands). Runs on both Postgres
    /// and SQLite; idempotent via IF NOT EXISTS.
    /// </summary>
    private static async Task EnsureAttendanceEditRequestsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            const string pg = @"
                CREATE TABLE IF NOT EXISTS attendance_edit_requests (
                    ""Id""                  SERIAL PRIMARY KEY,
                    ""AttendanceId""        INTEGER NOT NULL REFERENCES attendance(""Id"") ON DELETE CASCADE,
                    ""RequestedByUserId""   INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE RESTRICT,
                    ""RequestedAt""         TIMESTAMP NOT NULL,
                    ""RequestedWorkDate""   DATE NOT NULL,
                    ""RequestedCheckIn""    TIMESTAMP NOT NULL,
                    ""RequestedCheckOut""   TIMESTAMP NULL,
                    ""Reason""              VARCHAR(500) NOT NULL DEFAULT '',
                    ""ProofPhoto""          TEXT NULL,
                    ""Status""              VARCHAR(16) NOT NULL DEFAULT 'Pending',
                    ""DecidedByUserId""     INTEGER NULL REFERENCES users(""Id"") ON DELETE SET NULL,
                    ""DecidedAt""           TIMESTAMP NULL,
                    ""DecisionNote""        VARCHAR(500) NULL
                );
                CREATE INDEX IF NOT EXISTS ix_aer_attendance ON attendance_edit_requests(""AttendanceId"");
                CREATE INDEX IF NOT EXISTS ix_aer_requester  ON attendance_edit_requests(""RequestedByUserId"");
                CREATE INDEX IF NOT EXISTS ix_aer_status     ON attendance_edit_requests(""Status"");
            ";
            await db.Database.ExecuteSqlRawAsync(pg);
            return;
        }

        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "attendance_edit_requests")) return;

        const string sqlite = @"
            CREATE TABLE attendance_edit_requests (
                Id                INTEGER NOT NULL CONSTRAINT PK_aer PRIMARY KEY AUTOINCREMENT,
                AttendanceId      INTEGER NOT NULL,
                RequestedByUserId INTEGER NOT NULL,
                RequestedAt       TEXT    NOT NULL,
                RequestedWorkDate TEXT    NOT NULL,
                RequestedCheckIn  TEXT    NOT NULL,
                RequestedCheckOut TEXT    NULL,
                Reason            TEXT    NOT NULL DEFAULT '',
                ProofPhoto        TEXT    NULL,
                Status            TEXT    NOT NULL DEFAULT 'Pending',
                DecidedByUserId   INTEGER NULL,
                DecidedAt         TEXT    NULL,
                DecisionNote      TEXT    NULL,
                FOREIGN KEY (AttendanceId)      REFERENCES attendance(Id) ON DELETE CASCADE,
                FOREIGN KEY (RequestedByUserId) REFERENCES users(Id)      ON DELETE RESTRICT,
                FOREIGN KEY (DecidedByUserId)   REFERENCES users(Id)      ON DELETE SET NULL
            );
            CREATE INDEX ix_aer_attendance ON attendance_edit_requests(AttendanceId);
            CREATE INDEX ix_aer_requester  ON attendance_edit_requests(RequestedByUserId);
            CREATE INDEX ix_aer_status     ON attendance_edit_requests(Status);
        ";
        await db.Database.ExecuteSqlRawAsync(sqlite);
    }

    /// <summary>
    /// Adds the optional evidence image column to attendance-edit requests.
    /// Safe to run on every startup for both Postgres and SQLite.
    /// </summary>
    private static async Task EnsureAttendanceEditRequestProofPhotoColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance_edit_requests ADD COLUMN IF NOT EXISTS \"ProofPhoto\" TEXT NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;
        var cols = await GetColumnsAsync(db, "attendance_edit_requests");
        if (!cols.Contains("ProofPhoto"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance_edit_requests ADD COLUMN ProofPhoto TEXT NULL;");
        }
    }

    /// <summary>
    /// Creates the <c>leave_requests</c> table (employee-filed leave of
    /// absence: Full / Half day / Undertime, with PM/admin approval).
    /// Idempotent on both Postgres and SQLite.
    /// </summary>
    private static async Task EnsureLeaveRequestsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            const string pg = @"
                CREATE TABLE IF NOT EXISTS leave_requests (
                    ""Id""                  SERIAL PRIMARY KEY,
                    ""UserId""              INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                    ""RequestedByUserId""   INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE RESTRICT,
                    ""RequestedAt""         TIMESTAMP NOT NULL,
                    ""StartDate""           TIMESTAMP NOT NULL,
                    ""EndDate""             TIMESTAMP NOT NULL,
                    ""LeaveType""           VARCHAR(16) NOT NULL DEFAULT 'Full',
                    ""HoursPerDay""         NUMERIC(4,2) NOT NULL DEFAULT 8,
                    ""Reason""              VARCHAR(500) NOT NULL DEFAULT '',
                    ""Status""              VARCHAR(16) NOT NULL DEFAULT 'Pending',
                    ""DecidedByUserId""     INTEGER NULL REFERENCES users(""Id"") ON DELETE SET NULL,
                    ""DecidedAt""           TIMESTAMP NULL,
                    ""DecisionNote""        VARCHAR(500) NULL
                );
                CREATE INDEX IF NOT EXISTS ix_leave_user   ON leave_requests(""UserId"");
                CREATE INDEX IF NOT EXISTS ix_leave_status ON leave_requests(""Status"");
                CREATE INDEX IF NOT EXISTS ix_leave_start  ON leave_requests(""StartDate"");
            ";
            await db.Database.ExecuteSqlRawAsync(pg);
            return;
        }

        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "leave_requests")) return;

        const string sqlite = @"
            CREATE TABLE leave_requests (
                Id                INTEGER NOT NULL CONSTRAINT PK_leave PRIMARY KEY AUTOINCREMENT,
                UserId            INTEGER NOT NULL,
                RequestedByUserId INTEGER NOT NULL,
                RequestedAt       TEXT    NOT NULL,
                StartDate         TEXT    NOT NULL,
                EndDate           TEXT    NOT NULL,
                LeaveType         TEXT    NOT NULL DEFAULT 'Full',
                HoursPerDay       NUMERIC NOT NULL DEFAULT 8,
                Reason            TEXT    NOT NULL DEFAULT '',
                Status            TEXT    NOT NULL DEFAULT 'Pending',
                DecidedByUserId   INTEGER NULL,
                DecidedAt         TEXT    NULL,
                DecisionNote      TEXT    NULL,
                FOREIGN KEY (UserId)            REFERENCES users(Id) ON DELETE CASCADE,
                FOREIGN KEY (RequestedByUserId) REFERENCES users(Id) ON DELETE RESTRICT,
                FOREIGN KEY (DecidedByUserId)   REFERENCES users(Id) ON DELETE SET NULL
            );
            CREATE INDEX ix_leave_user   ON leave_requests(UserId);
            CREATE INDEX ix_leave_status ON leave_requests(Status);
            CREATE INDEX ix_leave_start  ON leave_requests(StartDate);
        ";
        await db.Database.ExecuteSqlRawAsync(sqlite);
    }

    /// <summary>
    /// Creates the <c>schedule_amendments</c> table for the employee
    /// shift-change request workflow. Runs on both Postgres and SQLite;
    /// idempotent via IF NOT EXISTS.
    /// </summary>
    private static async Task EnsureScheduleAmendmentsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            const string pg = @"
                CREATE TABLE IF NOT EXISTS schedule_amendments (
                    ""Id""                  SERIAL PRIMARY KEY,
                    ""UserId""              INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                    ""RequestedByUserId""   INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE RESTRICT,
                    ""RequestedAt""         TIMESTAMP NOT NULL,
                    ""WorkDate""            DATE NOT NULL,
                    ""ProposedIsWorking""   BOOLEAN NOT NULL DEFAULT FALSE,
                    ""ProposedStartTime""   TIME NULL,
                    ""ProposedEndTime""     TIME NULL,
                    ""ProposedNote""        VARCHAR(200) NULL,
                    ""Reason""              VARCHAR(500) NOT NULL DEFAULT '',
                    ""Status""              VARCHAR(16) NOT NULL DEFAULT 'Pending',
                    ""DecidedByUserId""     INTEGER NULL REFERENCES users(""Id"") ON DELETE SET NULL,
                    ""DecidedAt""           TIMESTAMP NULL,
                    ""DecisionNote""        VARCHAR(500) NULL
                );
                CREATE INDEX IF NOT EXISTS ix_sched_amend_user      ON schedule_amendments(""UserId"");
                CREATE INDEX IF NOT EXISTS ix_sched_amend_requester ON schedule_amendments(""RequestedByUserId"");
                CREATE INDEX IF NOT EXISTS ix_sched_amend_status    ON schedule_amendments(""Status"");
            ";
            await db.Database.ExecuteSqlRawAsync(pg);
            return;
        }

        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "schedule_amendments")) return;

        const string sqlite = @"
            CREATE TABLE schedule_amendments (
                Id                INTEGER NOT NULL CONSTRAINT PK_sched_amend PRIMARY KEY AUTOINCREMENT,
                UserId            INTEGER NOT NULL,
                RequestedByUserId INTEGER NOT NULL,
                RequestedAt       TEXT    NOT NULL,
                WorkDate          TEXT    NOT NULL,
                ProposedIsWorking INTEGER NOT NULL DEFAULT 0,
                ProposedStartTime TEXT    NULL,
                ProposedEndTime   TEXT    NULL,
                ProposedNote      TEXT    NULL,
                Reason            TEXT    NOT NULL DEFAULT '',
                Status            TEXT    NOT NULL DEFAULT 'Pending',
                DecidedByUserId   INTEGER NULL,
                DecidedAt         TEXT    NULL,
                DecisionNote      TEXT    NULL,
                FOREIGN KEY (UserId)            REFERENCES users(Id) ON DELETE CASCADE,
                FOREIGN KEY (RequestedByUserId) REFERENCES users(Id) ON DELETE RESTRICT,
                FOREIGN KEY (DecidedByUserId)   REFERENCES users(Id) ON DELETE SET NULL
            );
            CREATE INDEX ix_sched_amend_user      ON schedule_amendments(UserId);
            CREATE INDEX ix_sched_amend_requester ON schedule_amendments(RequestedByUserId);
            CREATE INDEX ix_sched_amend_status    ON schedule_amendments(Status);
        ";
        await db.Database.ExecuteSqlRawAsync(sqlite);
    }

    /// <summary>
    /// Creates the <c>quota_reset_requests</c> table for the
    /// break/lunch counter reset approval workflow. Idempotent on both
    /// Postgres and SQLite.
    /// </summary>
    private static async Task EnsureQuotaResetRequestsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            const string pg = @"
                CREATE TABLE IF NOT EXISTS quota_reset_requests (
                    ""Id""                  SERIAL PRIMARY KEY,
                    ""UserId""              INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                    ""RequestedByUserId""   INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE RESTRICT,
                    ""RequestedAt""         TIMESTAMP NOT NULL,
                    ""Kind""                VARCHAR(8) NOT NULL,
                    ""TargetDate""          DATE NOT NULL,
                    ""Reason""              VARCHAR(500) NOT NULL DEFAULT '',
                    ""Status""              VARCHAR(16) NOT NULL DEFAULT 'Pending',
                    ""DecidedByUserId""     INTEGER NULL REFERENCES users(""Id"") ON DELETE SET NULL,
                    ""DecidedAt""           TIMESTAMP NULL,
                    ""DecisionNote""        VARCHAR(500) NULL
                );
                CREATE INDEX IF NOT EXISTS ix_qrr_user      ON quota_reset_requests(""UserId"");
                CREATE INDEX IF NOT EXISTS ix_qrr_requester ON quota_reset_requests(""RequestedByUserId"");
                CREATE INDEX IF NOT EXISTS ix_qrr_status    ON quota_reset_requests(""Status"");
                CREATE INDEX IF NOT EXISTS ix_qrr_kind      ON quota_reset_requests(""Kind"");
            ";
            await db.Database.ExecuteSqlRawAsync(pg);
            return;
        }

        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "quota_reset_requests")) return;

        const string sqlite = @"
            CREATE TABLE quota_reset_requests (
                Id                INTEGER NOT NULL CONSTRAINT PK_qrr PRIMARY KEY AUTOINCREMENT,
                UserId            INTEGER NOT NULL,
                RequestedByUserId INTEGER NOT NULL,
                RequestedAt       TEXT    NOT NULL,
                Kind              TEXT    NOT NULL,
                TargetDate        TEXT    NOT NULL,
                Reason            TEXT    NOT NULL DEFAULT '',
                Status            TEXT    NOT NULL DEFAULT 'Pending',
                DecidedByUserId   INTEGER NULL,
                DecidedAt         TEXT    NULL,
                DecisionNote      TEXT    NULL,
                FOREIGN KEY (UserId)            REFERENCES users(Id) ON DELETE CASCADE,
                FOREIGN KEY (RequestedByUserId) REFERENCES users(Id) ON DELETE RESTRICT,
                FOREIGN KEY (DecidedByUserId)   REFERENCES users(Id) ON DELETE SET NULL
            );
            CREATE INDEX ix_qrr_user      ON quota_reset_requests(UserId);
            CREATE INDEX ix_qrr_requester ON quota_reset_requests(RequestedByUserId);
            CREATE INDEX ix_qrr_status    ON quota_reset_requests(Status);
            CREATE INDEX ix_qrr_kind      ON quota_reset_requests(Kind);
        ";
        await db.Database.ExecuteSqlRawAsync(sqlite);
    }

    /// <summary>
    /// Adds the <c>Source</c> column to existing <c>schedule_entries</c>
    /// tables. Rows authored before this shipped are treated as manual
    /// (NULL); the Coalition roster save handler tags its own rows with
    /// the literal <c>"roster"</c> so it can later clean them up without
    /// touching manual edits.
    /// </summary>
    private static async Task EnsureScheduleEntrySourceColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE schedule_entries ADD COLUMN IF NOT EXISTS \"Source\" VARCHAR(20) NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;
        var existing = await GetColumnsAsync(db, "schedule_entries");
        if (existing.Contains("Source")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE schedule_entries ADD COLUMN Source TEXT NULL;");
    }

    /// <summary>
    /// Adds the <c>WorkType</c> column to existing <c>schedule_entries</c>
    /// tables. The column stores one of <c>"Onsite" | "Offsite" | "Dayoff"</c>
    /// and drives the per-region grace-period rules for the late-arrival
    /// notification (see <see cref="Services.LateCheck"/>). Idempotent.
    /// </summary>
    private static async Task EnsureScheduleEntryWorkTypeColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE schedule_entries ADD COLUMN IF NOT EXISTS \"WorkType\" VARCHAR(16) NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;
        var existing = await GetColumnsAsync(db, "schedule_entries");
        if (existing.Contains("WorkType")) return;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE schedule_entries ADD COLUMN WorkType TEXT NULL;");
    }

    /// <summary>
    /// Removes the bundled <c>jdoe</c> demo account if it still exists.
    /// Cascade-delete on attendance and schedule_entries (configured on
    /// the FK) cleans up dependent rows automatically.
    /// </summary>
    private static async Task RemoveDemoAccountAsync(AppDbContext db)
    {
        var demo = await db.Users.FirstOrDefaultAsync(u => u.Username == "jdoe");
        if (demo is null) return;
        db.Users.Remove(demo);
        await db.SaveChangesAsync();
    }

    private static async Task EnsureDefaultSchedulesAsync(AppDbContext db)
    {
        // Per-date scheduling has no template to seed — admins assign
        // specific calendar dates through the month editor. Kept as a
        // no-op so any future caller doesn't need to be re-wired.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Looks up the user's schedule rows for a calendar-date window
    /// (inclusive). Returns whatever's in the DB; missing dates are
    /// implicitly "off" — the new model deliberately does not auto-seed
    /// every date, so the schedule grows lazily as the admin assigns
    /// specific dates from the month editor.
    /// </summary>
    public static async Task<List<ScheduleEntry>> GetScheduleForRangeAsync(
        AppDbContext db, User user, DateOnly start, DateOnly end)
    {
        return await db.ScheduleEntries
            .Where(s => s.UserId == user.Id && s.WorkDate >= start && s.WorkDate <= end)
            .OrderBy(s => s.WorkDate)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the schedule row for the user's <paramref name="date"/>,
    /// or <c>null</c> when none exists (treated as "off").
    /// </summary>
    public static async Task<ScheduleEntry?> GetScheduleForDateAsync(
        AppDbContext db, User user, DateOnly date)
    {
        return await db.ScheduleEntries
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.WorkDate == date);
    }

    /// <summary>
    /// Returns the effective schedule row for the user's
    /// <paramref name="date"/>, falling back to the CSI default
    /// (Mon–Fri 09:00–18:00 Onsite, weekends Dayoff) when the admin has
    /// not authored a specific row. Admin and Support targets keep the
    /// nullable behaviour because they don't follow the standard shift:
    /// admins carry no schedule of their own and Support users run on a
    /// bespoke 24/7 rotation that defaults differently. The synthesized
    /// fallback is NOT persisted (Id stays 0) — it's a runtime stand-in
    /// so a freshly approved employee can still clock in and see the
    /// company-standard shift on their dashboard.
    /// </summary>
    public static async Task<ScheduleEntry?> GetEffectiveScheduleForDateAsync(
        AppDbContext db, User user, DateOnly date)
    {
        var row = await db.ScheduleEntries
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.WorkDate == date);
        if (row is not null) return row;
        if (string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase)
            || user.IsSupport)
        {
            return null;
        }
        return ScheduleEntry.CsiDefaultFor(user.Id, date);
    }

    /// <summary>
    /// Migrates the <c>schedule_entries</c> / <c>schedule_amendments</c>
    /// tables from the legacy weekly template (<c>Weekday INTEGER</c>)
    /// to the per-date model (<c>WorkDate</c>). Runs on both SQLite and
    /// Postgres because <see cref="DatabaseFacade.EnsureCreatedAsync"/>
    /// never adds/removes columns on pre-existing tables, so a Render
    /// deploy that flips the model leaves prod with the legacy column
    /// shape and every <c>WorkDate</c> query throws <em>column does not
    /// exist</em>. The legacy data is a per-weekday template that
    /// doesn't translate to specific calendar dates, so we drop and
    /// recreate rather than try to convert. Admins re-author the
    /// affected schedules from the month editor.
    /// </summary>
    private static async Task EnsurePerDateScheduleSchemaAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            // --- schedule_entries (Postgres) ---------------------------
            // If the production table still has the legacy "Weekday"
            // column, drop and recreate it. The new schema mirrors what
            // EF Core 8 / Npgsql would have produced from the current
            // model (DateOnly -> date, TimeOnly? -> time).
            if (await PgColumnExistsAsync(db, "schedule_entries", "Weekday"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TABLE IF EXISTS schedule_entries CASCADE;");
            }
            if (!await PgTableExistsAsync(db, "schedule_entries"))
            {
                const string pgEntries = @"
                    CREATE TABLE schedule_entries (
                        ""Id""         SERIAL PRIMARY KEY,
                        ""UserId""     INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                        ""WorkDate""   DATE NOT NULL,
                        ""StartTime""  TIME NULL,
                        ""EndTime""    TIME NULL,
                        ""IsWorking""  BOOLEAN NOT NULL DEFAULT FALSE,
                        ""WorkType""   VARCHAR(16) NULL,
                        ""Note""       VARCHAR(200) NULL,
                        ""UpdatedAt""  TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
                    );
                    CREATE INDEX ""IX_schedule_entries_UserId"" ON schedule_entries(""UserId"");
                    CREATE UNIQUE INDEX uq_user_workdate ON schedule_entries(""UserId"", ""WorkDate"");
                ";
                await db.Database.ExecuteSqlRawAsync(pgEntries);
            }

            // --- schedule_amendments (Postgres) ------------------------
            // EnsureScheduleAmendmentsTableAsync runs next and will
            // recreate the table with the new WorkDate shape via
            // CREATE TABLE IF NOT EXISTS, so we just drop the legacy
            // copy here when the old Weekday column is detected.
            if (await PgColumnExistsAsync(db, "schedule_amendments", "Weekday"))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP TABLE IF EXISTS schedule_amendments CASCADE;");
            }
            return;
        }

        if (!db.Database.IsSqlite()) return;

        // --- schedule_entries (SQLite) ---------------------------------
        if (await TableExistsAsync(db, "schedule_entries"))
        {
            var entryCols = await GetColumnsAsync(db, "schedule_entries");
            if (entryCols.Contains("Weekday") && !entryCols.Contains("WorkDate"))
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE schedule_entries;");
            }
        }
        if (!await TableExistsAsync(db, "schedule_entries"))
        {
            const string sql = @"
                CREATE TABLE schedule_entries (
                    Id         INTEGER NOT NULL CONSTRAINT PK_schedule_entries PRIMARY KEY AUTOINCREMENT,
                    UserId     INTEGER NOT NULL,
                    WorkDate   TEXT    NOT NULL,
                    StartTime  TEXT    NULL,
                    EndTime    TEXT    NULL,
                    IsWorking  INTEGER NOT NULL DEFAULT 0,
                    WorkType   TEXT    NULL,
                    Note       TEXT    NULL,
                    UpdatedAt  TEXT    NOT NULL,
                    FOREIGN KEY (UserId) REFERENCES users(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_schedule_entries_UserId ON schedule_entries(UserId);
                CREATE UNIQUE INDEX uq_user_workdate ON schedule_entries(UserId, WorkDate);
            ";
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        // --- schedule_amendments (SQLite) ------------------------------
        if (await TableExistsAsync(db, "schedule_amendments"))
        {
            var amendCols = await GetColumnsAsync(db, "schedule_amendments");
            if (amendCols.Contains("Weekday") && !amendCols.Contains("WorkDate"))
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE schedule_amendments;");
            }
        }
        // EnsureScheduleAmendmentsTableAsync handles the (re-)create for
        // SQLite when the table is absent, so nothing more to do here.
    }

    /// <summary>
    /// Postgres equivalent of <see cref="TableExistsAsync"/>. Looks up
    /// the current schema's tables via <c>information_schema</c>.
    /// </summary>
    private static async Task<bool> PgTableExistsAsync(AppDbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = @t LIMIT 1;";
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = table;
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Postgres equivalent of "does this column exist on this table".
    /// Uses <c>information_schema.columns</c> so we don't have to know
    /// the data type. Returns <c>false</c> when the table itself is
    /// missing, which is the right answer for our migration check
    /// (legacy column absent => already migrated or fresh DB).
    /// </summary>
    private static async Task<bool> PgColumnExistsAsync(
        AppDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM information_schema.columns "
                + "WHERE table_schema = current_schema() "
                + "AND table_name = @t AND column_name = @c LIMIT 1;";
            var pt = cmd.CreateParameter();
            pt.ParameterName = "@t";
            pt.Value = table;
            cmd.Parameters.Add(pt);
            var pc = cmd.CreateParameter();
            pc.ParameterName = "@c";
            pc.Value = column;
            cmd.Parameters.Add(pc);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Returns the <c>information_schema.columns.data_type</c> value
    /// for the given column (e.g. <c>"date"</c>, <c>"text"</c>,
    /// <c>"character varying"</c>) or <c>null</c> when the table /
    /// column does not exist. Postgres-only.
    /// </summary>
    private static async Task<string?> PgColumnDataTypeAsync(
        AppDbContext db, string table, string column)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT data_type FROM information_schema.columns "
                + "WHERE table_schema = current_schema() "
                + "AND table_name = @t AND column_name = @c LIMIT 1;";
            var pt = cmd.CreateParameter();
            pt.ParameterName = "@t";
            pt.Value = table;
            cmd.Parameters.Add(pt);
            var pc = cmd.CreateParameter();
            pc.ParameterName = "@c";
            pc.Value = column;
            cmd.Parameters.Add(pc);
            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Migrates legacy <c>DateOnly</c>-backed columns that earlier
    /// deploys created as <c>TEXT</c> on Postgres over to native
    /// <c>DATE</c>. EF Core 8 / Npgsql 8 send <c>DateOnly</c> parameters
    /// as <c>date</c>, so a <c>text</c> column makes every range query
    /// throw <em>operator does not exist: text &gt;= date</em>. ISO
    /// <c>yyyy-MM-dd</c> strings (which is the only format we ever
    /// inserted) cast cleanly via <c>::date</c>, and any index on the
    /// column is rebuilt automatically by Postgres when the type
    /// changes. Idempotent — re-runs are no-ops once the columns are
    /// already <c>date</c>. SQLite is unaffected (it keeps TEXT).
    /// </summary>
    private static async Task EnsureDateColumnTypesAsync(AppDbContext db)
    {
        if (!db.Database.IsNpgsql()) return;

        var targets = new (string Table, string Column)[]
        {
            ("attendance",               "WorkDate"),
            ("attendance_edit_requests", "RequestedWorkDate"),
            ("schedule_amendments",      "WorkDate"),
            ("quota_reset_requests",     "TargetDate"),
        };

        foreach (var (table, column) in targets)
        {
            var current = await PgColumnDataTypeAsync(db, table, column);
            if (current is null) continue;          // table/column absent
            if (current == "date") continue;        // already migrated

            var sql =
                $"ALTER TABLE {table} "
                + $"ALTER COLUMN \"{column}\" TYPE DATE USING (\"{column}\"::date);";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    /// <summary>
    /// Promotes <c>leave_requests.StartDate</c> / <c>EndDate</c> from
    /// <c>DATE</c> (or legacy <c>TEXT</c>) to <c>TIMESTAMP WITHOUT TIME ZONE</c>
    /// on Postgres so the request form can capture the time-of-day the
    /// worker will be out. <c>DATE → TIMESTAMP</c> is a widening cast in
    /// Postgres (existing rows pick up <c>00:00:00</c> as their time) and
    /// any index on the column is rebuilt automatically. Idempotent — a
    /// no-op once the column reports <c>timestamp without time zone</c>.
    /// SQLite is unaffected because both <c>DateOnly</c> and <c>DateTime</c>
    /// land in the same ISO TEXT column.
    /// </summary>
    private static async Task EnsureLeaveRequestDateTimeColumnsAsync(AppDbContext db)
    {
        if (!db.Database.IsNpgsql()) return;
        if (!await PgTableExistsAsync(db, "leave_requests")) return;

        foreach (var column in new[] { "StartDate", "EndDate" })
        {
            var current = await PgColumnDataTypeAsync(db, "leave_requests", column);
            if (current is null) continue;
            if (current == "timestamp without time zone") continue;

            var sql =
                "ALTER TABLE leave_requests "
                + $"ALTER COLUMN \"{column}\" TYPE TIMESTAMP "
                + $"USING (\"{column}\"::timestamp);";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task EnsureLeaveRequestProofPhotoColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE leave_requests ADD COLUMN IF NOT EXISTS \"ProofPhoto\" TEXT NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;
        var cols = await GetColumnsAsync(db, "leave_requests");
        if (!cols.Contains("ProofPhoto"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE leave_requests ADD COLUMN ProofPhoto TEXT NULL;");
        }
    }

    private static async Task EnsureScheduleAmendmentProofPhotoColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE schedule_amendments ADD COLUMN IF NOT EXISTS \"ProofPhoto\" TEXT NULL;");
            return;
        }

        if (!db.Database.IsSqlite()) return;
        var cols = await GetColumnsAsync(db, "schedule_amendments");
        if (!cols.Contains("ProofPhoto"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE schedule_amendments ADD COLUMN ProofPhoto TEXT NULL;");
        }
    }

    // ------------------------------------------------------------------
    // v2026 feature-drop migrations (idempotent on both providers).
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds <c>ActivityTag</c>, <c>CheckInLatitude</c>, <c>CheckInLongitude</c>,
    /// <c>CheckInAccuracy</c>, <c>CheckInSiteId</c>, <c>FaceMatchStatus</c>,
    /// and <c>FaceMatchDistance</c> to <c>attendance</c> on legacy DBs.
    /// </summary>
    private static async Task EnsureAttendanceActivityAndGpsColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"ActivityTag\" VARCHAR(64) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"CheckInLatitude\" DOUBLE PRECISION NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"CheckInLongitude\" DOUBLE PRECISION NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"CheckInAccuracy\" DOUBLE PRECISION NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"CheckInSiteId\" INTEGER NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"FaceMatchStatus\" VARCHAR(16) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE attendance ADD COLUMN IF NOT EXISTS \"FaceMatchDistance\" INTEGER NULL;");
            return;
        }
        if (!db.Database.IsSqlite()) return;

        var cols = await GetColumnsAsync(db, "attendance");
        if (!cols.Contains("ActivityTag"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN ActivityTag TEXT NULL;");
        if (!cols.Contains("CheckInLatitude"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN CheckInLatitude REAL NULL;");
        if (!cols.Contains("CheckInLongitude"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN CheckInLongitude REAL NULL;");
        if (!cols.Contains("CheckInAccuracy"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN CheckInAccuracy REAL NULL;");
        if (!cols.Contains("CheckInSiteId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN CheckInSiteId INTEGER NULL;");
        if (!cols.Contains("FaceMatchStatus"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN FaceMatchStatus TEXT NULL;");
        if (!cols.Contains("FaceMatchDistance"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance ADD COLUMN FaceMatchDistance INTEGER NULL;");
    }

    /// <summary>Adds <c>FaceHash</c> and <c>FaceEnrolledAt</c> to <c>users</c>.</summary>
    private static async Task EnsureIsActiveColumnAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"IsActive\" BOOLEAN NOT NULL DEFAULT TRUE;");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_users_IsActive\" ON users (\"IsActive\");");
            return;
        }

        if (!db.Database.IsSqlite()) return;

        var existing = await GetColumnsAsync(db, "users");
        if (!existing.Contains("IsActive"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1;");
        }
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_users_IsActive ON users(IsActive);");
        }
        catch { /* old SQLite without IF NOT EXISTS — ignore */ }
    }

    private static async Task EnsureUserFaceColumnsAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"FaceHash\" VARCHAR(32) NULL;");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS \"FaceEnrolledAt\" TIMESTAMP NULL;");
            return;
        }
        if (!db.Database.IsSqlite()) return;
        var cols = await GetColumnsAsync(db, "users");
        if (!cols.Contains("FaceHash"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE users ADD COLUMN FaceHash TEXT NULL;");
        if (!cols.Contains("FaceEnrolledAt"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE users ADD COLUMN FaceEnrolledAt TEXT NULL;");
    }

    private static async Task EnsureHolidaysTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS holidays (
                    ""Id""        SERIAL PRIMARY KEY,
                    ""Date""      DATE NOT NULL,
                    ""Country""   VARCHAR(8) NOT NULL,
                    ""Name""      VARCHAR(120) NOT NULL,
                    ""Kind""      VARCHAR(20) NOT NULL DEFAULT 'Regular',
                    ""CreatedAt"" TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_holiday_country_date ON holidays(\"Country\", \"Date\");");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_holidays_Date ON holidays(\"Date\");");
            return;
        }
        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "holidays")) return;
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE holidays (
                Id        INTEGER NOT NULL CONSTRAINT PK_holidays PRIMARY KEY AUTOINCREMENT,
                Date      TEXT NOT NULL,
                Country   TEXT NOT NULL,
                Name      TEXT NOT NULL,
                Kind      TEXT NOT NULL DEFAULT 'Regular',
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX uq_holiday_country_date ON holidays(Country, Date);
            CREATE INDEX IX_holidays_Date ON holidays(Date);");
    }

    private static async Task EnsureSitesTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS sites (
                    ""Id""           SERIAL PRIMARY KEY,
                    ""Name""         VARCHAR(120) NOT NULL,
                    ""Latitude""     DOUBLE PRECISION NOT NULL,
                    ""Longitude""    DOUBLE PRECISION NOT NULL,
                    ""RadiusMeters"" INTEGER NOT NULL DEFAULT 150,
                    ""BusinessUnit"" VARCHAR(120) NULL,
                    ""Notes""        VARCHAR(500) NULL,
                    ""IsActive""     BOOLEAN NOT NULL DEFAULT TRUE,
                    ""CreatedAt""    TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                    ""UpdatedAt""    TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_sites_BusinessUnit ON sites(\"BusinessUnit\");");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_sites_IsActive ON sites(\"IsActive\");");
            return;
        }
        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "sites")) return;
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE sites (
                Id           INTEGER NOT NULL CONSTRAINT PK_sites PRIMARY KEY AUTOINCREMENT,
                Name         TEXT NOT NULL,
                Latitude     REAL NOT NULL,
                Longitude    REAL NOT NULL,
                RadiusMeters INTEGER NOT NULL DEFAULT 150,
                BusinessUnit TEXT NULL,
                Notes        TEXT NULL,
                IsActive     INTEGER NOT NULL DEFAULT 1,
                CreatedAt    TEXT NOT NULL,
                UpdatedAt    TEXT NOT NULL
            );
            CREATE INDEX IX_sites_BusinessUnit ON sites(BusinessUnit);
            CREATE INDEX IX_sites_IsActive ON sites(IsActive);");
    }

    private static async Task EnsureLeaveBalancesTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS leave_balances (
                    ""Id""              SERIAL PRIMARY KEY,
                    ""UserId""          INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                    ""LeaveType""       VARCHAR(32) NOT NULL,
                    ""HoursRemaining""  NUMERIC(8,2) NOT NULL DEFAULT 0,
                    ""HoursAccruedYtd"" NUMERIC(8,2) NOT NULL DEFAULT 0,
                    ""HoursUsedYtd""    NUMERIC(8,2) NOT NULL DEFAULT 0,
                    ""LastAccrualMonth"" DATE NULL,
                    ""UpdatedAt""       TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_leave_balance_user_type ON leave_balances(\"UserId\", \"LeaveType\");");
            return;
        }
        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "leave_balances")) return;
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE leave_balances (
                Id               INTEGER NOT NULL CONSTRAINT PK_leave_balances PRIMARY KEY AUTOINCREMENT,
                UserId           INTEGER NOT NULL,
                LeaveType        TEXT NOT NULL,
                HoursRemaining   REAL NOT NULL DEFAULT 0,
                HoursAccruedYtd  REAL NOT NULL DEFAULT 0,
                HoursUsedYtd     REAL NOT NULL DEFAULT 0,
                LastAccrualMonth TEXT NULL,
                UpdatedAt        TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES users(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX uq_leave_balance_user_type ON leave_balances(UserId, LeaveType);");
    }

    private static async Task EnsurePushSubscriptionsTableAsync(AppDbContext db)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS push_subscriptions (
                    ""Id""         SERIAL PRIMARY KEY,
                    ""UserId""     INTEGER NOT NULL REFERENCES users(""Id"") ON DELETE CASCADE,
                    ""Endpoint""   VARCHAR(500) NOT NULL,
                    ""P256dh""     VARCHAR(256) NOT NULL,
                    ""Auth""       VARCHAR(64) NOT NULL,
                    ""UserAgent""  VARCHAR(256) NULL,
                    ""CreatedAt""  TIMESTAMP NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                    ""LastUsedAt"" TIMESTAMP NULL
                );");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS uq_push_user_endpoint ON push_subscriptions(\"UserId\", \"Endpoint\");");
            return;
        }
        if (!db.Database.IsSqlite()) return;
        if (await TableExistsAsync(db, "push_subscriptions")) return;
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE push_subscriptions (
                Id         INTEGER NOT NULL CONSTRAINT PK_push_subscriptions PRIMARY KEY AUTOINCREMENT,
                UserId     INTEGER NOT NULL,
                Endpoint   TEXT NOT NULL,
                P256dh     TEXT NOT NULL,
                Auth       TEXT NOT NULL,
                UserAgent  TEXT NULL,
                CreatedAt  TEXT NOT NULL,
                LastUsedAt TEXT NULL,
                FOREIGN KEY (UserId) REFERENCES users(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX uq_push_user_endpoint ON push_subscriptions(UserId, Endpoint);");
    }

    /// <summary>
    /// Seeds the well-known PH + IN statutory holidays for the current
    /// and next calendar year. Re-runnable: existing rows are kept,
    /// only missing (country, date) tuples are added.
    /// </summary>
    private static async Task SeedDefaultHolidaysAsync(AppDbContext db)
    {
        var seed = new List<Holiday>
        {
            // ---- 2026 — Philippines ----------------------------------
            new() { Country = "PH", Date = new(2026, 1, 1),  Name = "New Year's Day",       Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 4, 2),  Name = "Maundy Thursday",       Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 4, 3),  Name = "Good Friday",           Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 4, 9),  Name = "Araw ng Kagitingan",    Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 5, 1),  Name = "Labor Day",             Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 6, 12), Name = "Independence Day",      Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 8, 31), Name = "National Heroes Day",   Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 11, 30), Name = "Bonifacio Day",        Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 12, 25), Name = "Christmas Day",        Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 12, 30), Name = "Rizal Day",            Kind = "Regular" },
            new() { Country = "PH", Date = new(2026, 11, 1),  Name = "All Saints' Day",      Kind = "Special" },
            new() { Country = "PH", Date = new(2026, 12, 8),  Name = "Feast of the Immaculate Conception", Kind = "Special" },
            new() { Country = "PH", Date = new(2026, 12, 31), Name = "Last Day of the Year", Kind = "Special" },

            // ---- 2026 — India (national restricted holidays) ---------
            new() { Country = "IN", Date = new(2026, 1, 26),  Name = "Republic Day",         Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 3, 4),   Name = "Holi",                 Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 4, 14),  Name = "Dr. Ambedkar Jayanti", Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 5, 1),   Name = "May Day",              Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 8, 15),  Name = "Independence Day",     Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 10, 2),  Name = "Gandhi Jayanti",       Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 11, 9),  Name = "Diwali",               Kind = "Regular" },
            new() { Country = "IN", Date = new(2026, 12, 25), Name = "Christmas Day",        Kind = "Regular" },

            // ---- 2027 — Philippines (highlights) ---------------------
            new() { Country = "PH", Date = new(2027, 1, 1),  Name = "New Year's Day",      Kind = "Regular" },
            new() { Country = "PH", Date = new(2027, 5, 1),  Name = "Labor Day",           Kind = "Regular" },
            new() { Country = "PH", Date = new(2027, 6, 12), Name = "Independence Day",    Kind = "Regular" },
            new() { Country = "PH", Date = new(2027, 12, 25), Name = "Christmas Day",      Kind = "Regular" },
            new() { Country = "PH", Date = new(2027, 12, 30), Name = "Rizal Day",          Kind = "Regular" },

            // ---- 2027 — India (highlights) ---------------------------
            new() { Country = "IN", Date = new(2027, 1, 26), Name = "Republic Day",       Kind = "Regular" },
            new() { Country = "IN", Date = new(2027, 8, 15), Name = "Independence Day",   Kind = "Regular" },
            new() { Country = "IN", Date = new(2027, 10, 2), Name = "Gandhi Jayanti",     Kind = "Regular" },
        };

        // Lookup what's already present to avoid the unique-index clash.
        var existing = await db.Holidays
            .Select(h => new { h.Country, h.Date })
            .ToListAsync();
        var existingKeys = existing
            .Select(x => $"{x.Country}:{x.Date:yyyy-MM-dd}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd = seed
            .Where(h => !existingKeys.Contains($"{h.Country}:{h.Date:yyyy-MM-dd}"))
            .ToList();
        if (toAdd.Count == 0) return;
        db.Holidays.AddRange(toAdd);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Resolves the user's country code from their Business Unit string.
    /// "BU2 (IN)" → "IN"; "BU2 (PH)" / "BU1" → "PH" (the default region).
    /// </summary>
    public static string CountryFor(User user)
    {
        var bu = user.BusinessUnit ?? string.Empty;
        if (bu.Contains("(IN)", StringComparison.OrdinalIgnoreCase)) return "IN";
        return "PH";
    }

    /// <summary>
    /// Returns the holiday row matching <paramref name="date"/> for the
    /// user's resolved country, or null if it's a regular day.
    /// </summary>
    public static async Task<Holiday?> GetHolidayForAsync(
        AppDbContext db, User user, DateOnly date)
    {
        var country = CountryFor(user);
        return await db.Holidays
            .Where(h => h.Date == date && (h.Country == country || h.Country == "ALL"))
            .OrderBy(h => h.Country == country ? 0 : 1)
            .FirstOrDefaultAsync();
    }
}
