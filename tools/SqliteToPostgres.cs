using Microsoft.Data.Sqlite;
using Npgsql;

namespace AttendanceMonitoring.Tools;

/// <summary>
/// One-shot migration tool that copies every row from a local SQLite
/// <c>attendance.db</c> file into a remote PostgreSQL database (Render's free
/// managed Postgres). Invoked from <c>Program.cs</c> when the first command
/// line argument is <c>migrate-sqlite</c>:
/// <code>
/// dotnet run --project AttendanceMonitoring.csproj -- \
///   migrate-sqlite \
///   --sqlite "C:\Users\you\Downloads\attendance.db" \
///   --pg "postgres://user:pass@host.singapore-postgres.render.com/db"
/// </code>
/// Behavior:
///   1. Force-checkpoints the SQLite WAL so all uncommitted writes are read.
///   2. Truncates the four app tables in Postgres (destroys the seeded
///      <c>admin</c>/<c>jdoe</c> rows).
///   3. Re-inserts every row preserving primary keys, then re-aligns each PK
///      sequence so subsequent inserts don't collide.
/// Safe to re-run: each run starts with a fresh TRUNCATE.
/// </summary>
public static class SqliteToPostgres
{
    public static async Task<int> RunAsync(string[] args)
    {
        var sqlitePath = GetArg(args, "--sqlite");
        var pgUrl = GetArg(args, "--pg");

        if (string.IsNullOrWhiteSpace(sqlitePath) || string.IsNullOrWhiteSpace(pgUrl))
        {
            Console.Error.WriteLine("""
                Usage:
                  dotnet run -- migrate-sqlite --sqlite "<path-to-attendance.db>" --pg "<postgres-url>"

                  --sqlite : full path to the local attendance.db file. Its
                             .db-wal / .db-shm side files must be in the same
                             folder (don't move them away).
                  --pg     : External Database URL from Render → csiph-db →
                             Connect tab. Starts with postgres:// or
                             postgresql:// and contains the password inline.

                Example:
                  dotnet run -- migrate-sqlite `
                    --sqlite "$env:USERPROFILE\Downloads\attendance.db" `
                    --pg "postgres://csiph_user:abc123@dpg-xxx-a.singapore-postgres.render.com/csiph_attendance"
                """);
            return 2;
        }

        if (!File.Exists(sqlitePath))
        {
            Console.Error.WriteLine($"[migrate] ERROR: file not found: {sqlitePath}");
            return 3;
        }

        Console.WriteLine($"[migrate] source : {sqlitePath}");
        Console.WriteLine($"[migrate] target : {MaskUrl(pgUrl)}");

        var npgsqlConnString = ConvertUrlToNpgsql(pgUrl);

        // -------- 1) Open source SQLite, force WAL checkpoint --------------
        await using var src = new SqliteConnection($"Data Source={sqlitePath}");
        await src.OpenAsync();
        await ExecSqliteAsync(src, "PRAGMA journal_mode=WAL;");
        await ExecSqliteAsync(src, "PRAGMA wal_checkpoint(TRUNCATE);");

        // -------- 2) Open target Postgres ----------------------------------
        await using var dst = new NpgsqlConnection(npgsqlConnString);
        await dst.OpenAsync();
        Console.WriteLine("[migrate] connected to postgres");

        // -------- 3) Wipe target tables (FK-safe order) --------------------
        await ExecPgAsync(dst, """
            TRUNCATE
              "notification_log",
              "attendance",
              "schedule_entries",
              "users"
            RESTART IDENTITY CASCADE;
            """);
        Console.WriteLine("[migrate] target tables truncated");

        // -------- 4) Copy each table preserving primary keys ---------------
        var userCount = await CopyUsersAsync(src, dst);
        Console.WriteLine($"[migrate] users            : {userCount}");

        // Build the set of valid user IDs so we can skip orphaned schedule /
        // attendance / notification rows that point at deleted users. These
        // would otherwise hit "FK violation" on Postgres (SQLite was happy
        // because foreign keys are off by default).
        var validUserIds = await GetValidUserIdsAsync(dst);
        Console.WriteLine($"[migrate] valid userIds    : {string.Join(", ", validUserIds.OrderBy(x => x))}");

        var schedCount = await CopySchedulesAsync(src, dst, validUserIds);
        Console.WriteLine($"[migrate] schedule_entries : {schedCount}");
        var attCount = await CopyAttendanceAsync(src, dst, validUserIds);
        Console.WriteLine($"[migrate] attendance       : {attCount}");
        var logCount = await CopyNotificationLogAsync(src, dst, validUserIds);
        Console.WriteLine($"[migrate] notification_log : {logCount}");

        // -------- 5) Re-align Postgres serial sequences --------------------
        await ResetSequenceAsync(dst, "users", "Id");
        await ResetSequenceAsync(dst, "attendance", "Id");
        await ResetSequenceAsync(dst, "schedule_entries", "Id");
        await ResetSequenceAsync(dst, "notification_log", "Id");

        Console.WriteLine("[migrate] done.");
        return 0;
    }

    // -----------------------------------------------------------------------
    // Table copiers — read every row from SQLite, write to Postgres.
    // -----------------------------------------------------------------------

    private static async Task<int> CopyUsersAsync(SqliteConnection src, NpgsqlConnection dst)
    {
        var n = 0;
        var cols = await GetSqliteColumnsAsync(src, "users");

        // Build the SELECT dynamically so we don't blow up on legacy DBs that
        // pre-date Approved / PresenceState / login-telemetry / lunch.
        string Col(string name, string fallback)
            => cols.Contains(name) ? $"\"{name}\"" : $"{fallback} AS \"{name}\"";

        var sql = $"""
            SELECT "Id", "Username", "Email", "FullName",
                   {Col("EmployeeId", "NULL")},
                   {Col("BusinessUnit", "NULL")},
                   "PasswordHash", "Role",
                   {Col("Approved", "1")},
                   "CreatedAt",
                   {Col("LastSeen", "NULL")},
                   {Col("PresenceState", "'offline'")},
                   {Col("LunchStartedAt", "NULL")},
                   {Col("LastLoginIp", "NULL")},
                   {Col("LastLoginHost", "NULL")},
                   {Col("LastLoginAt", "NULL")}
            FROM users
            ORDER BY Id;
            """;

        using var read = src.CreateCommand();
        read.CommandText = sql;
        using var r = await read.ExecuteReaderAsync();

        const string insertSql = """
            INSERT INTO "users"
                ("Id", "Username", "Email", "FullName", "EmployeeId", "BusinessUnit",
                 "PasswordHash", "Role", "Approved", "CreatedAt", "LastSeen",
                 "PresenceState", "LunchStartedAt", "LastLoginIp", "LastLoginHost",
                 "LastLoginAt")
            VALUES (@id, @u, @em, @fn, @eid, @bu, @ph, @ro, @ap, @ca, @ls,
                    @ps, @lsa, @ip, @ho, @lla);
            """;

        while (await r.ReadAsync())
        {
            await using var ins = dst.CreateCommand();
            ins.CommandText = insertSql;
            AddInt(ins, "@id", r.GetInt32(0));
            AddText(ins, "@u", r.GetString(1));
            AddText(ins, "@em", r.GetString(2));
            AddText(ins, "@fn", r.GetString(3));
            AddNullableText(ins, "@eid", r, 4);
            AddNullableText(ins, "@bu", r, 5);
            AddText(ins, "@ph", r.GetString(6));
            AddText(ins, "@ro", r.GetString(7));
            // Approved comes back as 0/1 for column rows, integer literal 1
            // for the fallback. Both work with GetInt32.
            AddBool(ins, "@ap", !r.IsDBNull(8) && r.GetInt32(8) != 0);
            AddDateTime(ins, "@ca", r.GetDateTime(9));
            AddNullableDateTime(ins, "@ls", r, 10);
            AddText(ins, "@ps", r.IsDBNull(11) ? "offline" : r.GetString(11));
            AddNullableDateTime(ins, "@lsa", r, 12);
            AddNullableText(ins, "@ip", r, 13);
            AddNullableText(ins, "@ho", r, 14);
            AddNullableDateTime(ins, "@lla", r, 15);
            await ins.ExecuteNonQueryAsync();
            n++;
        }
        return n;
    }

    private static async Task<HashSet<string>> GetSqliteColumnsAsync(SqliteConnection src, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = src.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            set.Add(r.GetString(1));
        }
        return set;
    }

    private static async Task<int> CopySchedulesAsync(SqliteConnection src, NpgsqlConnection dst, HashSet<int> validUserIds)
    {
        var n = 0; var skipped = 0;
        using var read = src.CreateCommand();
        read.CommandText = """
            SELECT Id, UserId, Weekday, StartTime, EndTime, IsWorking, Note, UpdatedAt
            FROM schedule_entries
            ORDER BY Id;
            """;
        using var r = await read.ExecuteReaderAsync();

        const string insertSql = """
            INSERT INTO "schedule_entries"
                ("Id", "UserId", "Weekday", "StartTime", "EndTime", "IsWorking", "Note", "UpdatedAt")
            VALUES (@id, @uid, @wd, @st, @et, @iw, @no, @ua);
            """;

        while (await r.ReadAsync())
        {
            var uid = r.GetInt32(1);
            if (!validUserIds.Contains(uid)) { skipped++; continue; }

            await using var ins = dst.CreateCommand();
            ins.CommandText = insertSql;
            AddInt(ins, "@id", r.GetInt32(0));
            AddInt(ins, "@uid", uid);
            AddInt(ins, "@wd", r.GetInt32(2));
            AddNullableTimeOnly(ins, "@st", r, 3);
            AddNullableTimeOnly(ins, "@et", r, 4);
            AddBool(ins, "@iw", r.GetInt32(5) != 0);
            AddNullableText(ins, "@no", r, 6);
            AddDateTime(ins, "@ua", r.GetDateTime(7));
            await ins.ExecuteNonQueryAsync();
            n++;
        }
        if (skipped > 0) Console.WriteLine($"[migrate]   (skipped {skipped} orphan schedule rows)");
        return n;
    }

    private static async Task<int> CopyAttendanceAsync(SqliteConnection src, NpgsqlConnection dst, HashSet<int> validUserIds)
    {
        var n = 0; var skipped = 0;
        var cols = await GetSqliteColumnsAsync(src, "attendance");
        string Col(string name, string fallback)
            => cols.Contains(name) ? $"\"{name}\"" : $"{fallback} AS \"{name}\"";

        var sql = $"""
            SELECT "Id", "UserId", "WorkDate", "CheckIn",
                   {Col("CheckOut", "NULL")},
                   {Col("CheckInPhoto", "NULL")},
                   {Col("CheckOutPhoto", "NULL")}
            FROM attendance
            ORDER BY Id;
            """;

        using var read = src.CreateCommand();
        read.CommandText = sql;
        using var r = await read.ExecuteReaderAsync();

        const string insertSql = """
            INSERT INTO "attendance"
                ("Id", "UserId", "WorkDate", "CheckIn", "CheckOut", "CheckInPhoto", "CheckOutPhoto")
            VALUES (@id, @uid, @wd, @ci, @co, @cip, @cop);
            """;

        while (await r.ReadAsync())
        {
            var uid = r.GetInt32(1);
            if (!validUserIds.Contains(uid)) { skipped++; continue; }

            await using var ins = dst.CreateCommand();
            ins.CommandText = insertSql;
            AddInt(ins, "@id", r.GetInt32(0));
            AddInt(ins, "@uid", uid);
            AddDateOnly(ins, "@wd", DateOnly.Parse(r.GetString(2)));
            AddDateTime(ins, "@ci", r.GetDateTime(3));
            AddNullableDateTime(ins, "@co", r, 4);
            AddNullableText(ins, "@cip", r, 5);
            AddNullableText(ins, "@cop", r, 6);
            await ins.ExecuteNonQueryAsync();
            n++;
        }
        if (skipped > 0) Console.WriteLine($"[migrate]   (skipped {skipped} orphan attendance rows)");
        return n;
    }

    private static async Task<int> CopyNotificationLogAsync(SqliteConnection src, NpgsqlConnection dst, HashSet<int> validUserIds)
    {
        // The notification_log table only exists if the SQLite DB was opened
        // by a recent build of the app. Older backups won't have it.
        if (!await SqliteTableExistsAsync(src, "notification_log"))
        {
            Console.WriteLine("[migrate] notification_log table missing in SQLite — skipped.");
            return 0;
        }

        var n = 0;
        using var read = src.CreateCommand();
        read.CommandText = """
            SELECT Id, SentAt, Level, Status, UserId, UserFullName, Username,
                   Recipients, Subject, OfflineMinutes, ErrorMessage
            FROM notification_log
            ORDER BY Id;
            """;
        using var r = await read.ExecuteReaderAsync();

        const string insertSql = """
            INSERT INTO "notification_log"
                ("Id", "SentAt", "Level", "Status", "UserId", "UserFullName",
                 "Username", "Recipients", "Subject", "OfflineMinutes", "ErrorMessage")
            VALUES (@id, @sa, @lv, @st, @uid, @ufn, @un, @rc, @sub, @om, @err);
            """;

        while (await r.ReadAsync())
        {
            await using var ins = dst.CreateCommand();
            ins.CommandText = insertSql;
            AddInt(ins, "@id", r.GetInt32(0));
            AddDateTime(ins, "@sa", r.GetDateTime(1));
            AddText(ins, "@lv", r.GetString(2));
            AddText(ins, "@st", r.GetString(3));
            // notification_log.UserId has no FK in the schema but we still
            // null out references to users that no longer exist for cleanliness.
            int? uid = r.IsDBNull(4) ? null : r.GetInt32(4);
            if (uid is not null && !validUserIds.Contains(uid.Value)) uid = null;
            if (uid is null)
                ins.Parameters.Add(new NpgsqlParameter("@uid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = DBNull.Value });
            else
                AddInt(ins, "@uid", uid.Value);
            AddNullableText(ins, "@ufn", r, 5);
            AddNullableText(ins, "@un", r, 6);
            AddText(ins, "@rc", r.IsDBNull(7) ? "" : r.GetString(7));
            AddText(ins, "@sub", r.IsDBNull(8) ? "" : r.GetString(8));
            AddInt(ins, "@om", r.IsDBNull(9) ? 0 : r.GetInt32(9));
            AddNullableText(ins, "@err", r, 10);
            await ins.ExecuteNonQueryAsync();
            n++;
        }
        return n;
    }

    private static async Task<HashSet<int>> GetValidUserIdsAsync(NpgsqlConnection dst)
    {
        var set = new HashSet<int>();
        await using var cmd = dst.CreateCommand();
        cmd.CommandText = "SELECT \"Id\" FROM \"users\";";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetInt32(0));
        return set;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<bool> SqliteTableExistsAsync(SqliteConnection src, string name)
    {
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$n";
        p.Value = name;
        cmd.Parameters.Add(p);
        var v = await cmd.ExecuteScalarAsync();
        return v is not null;
    }

    private static async Task ResetSequenceAsync(NpgsqlConnection dst, string table, string idColumn)
    {
        // EF Core / Npgsql names identity sequences <table>_<column>_seq. The
        // helper pg_get_serial_sequence() does the right thing for both serial
        // and identity columns.
        var sql = $"""
            SELECT setval(
                pg_get_serial_sequence('"{table}"', '{idColumn}'),
                COALESCE((SELECT MAX("{idColumn}") FROM "{table}"), 0) + 1,
                false);
            """;
        await ExecPgAsync(dst, sql);
    }

    private static async Task ExecSqliteAsync(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecPgAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddInt(NpgsqlCommand c, string name, int v) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Integer) { Value = v });

    private static void AddNullableInt(NpgsqlCommand c, string name, SqliteDataReader r, int idx) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Integer)
        { Value = r.IsDBNull(idx) ? DBNull.Value : r.GetInt32(idx) });

    private static void AddBool(NpgsqlCommand c, string name, bool v) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Boolean) { Value = v });

    private static void AddText(NpgsqlCommand c, string name, string v) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Text) { Value = v });

    private static void AddNullableText(NpgsqlCommand c, string name, SqliteDataReader r, int idx) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Text)
        { Value = r.IsDBNull(idx) ? DBNull.Value : r.GetString(idx) });

    private static void AddDateTime(NpgsqlCommand c, string name, DateTime v) =>
        // Render Postgres stores timestamps in UTC; tag and convert.
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.TimestampTz)
        { Value = DateTime.SpecifyKind(v, DateTimeKind.Utc) });

    private static void AddNullableDateTime(NpgsqlCommand c, string name, SqliteDataReader r, int idx)
    {
        if (r.IsDBNull(idx))
        {
            c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.TimestampTz)
            { Value = DBNull.Value });
        }
        else
        {
            var v = DateTime.SpecifyKind(r.GetDateTime(idx), DateTimeKind.Utc);
            c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.TimestampTz)
            { Value = v });
        }
    }

    private static void AddDateOnly(NpgsqlCommand c, string name, DateOnly v) =>
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Date) { Value = v });

    private static void AddNullableTimeOnly(NpgsqlCommand c, string name, SqliteDataReader r, int idx)
    {
        if (r.IsDBNull(idx))
        {
            c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Time)
            { Value = DBNull.Value });
            return;
        }
        var s = r.GetString(idx);
        var t = TimeOnly.Parse(s);
        c.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Time) { Value = t });
    }

    // -----------------------------------------------------------------------
    // Argument + URL parsing
    // -----------------------------------------------------------------------

    private static string? GetArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static string ConvertUrlToNpgsql(string url)
    {
        // Mirror the conversion in Program.cs so the migration tool uses the
        // exact same connection settings as the deployed app.
        if (!url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return url; // assume already in keyword form
        }
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        return
            $"Host={uri.Host};" +
            $"Port={(uri.Port > 0 ? uri.Port : 5432)};" +
            $"Database={uri.AbsolutePath.TrimStart('/')};" +
            $"Username={Uri.UnescapeDataString(userInfo[0])};" +
            $"Password={Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty)};" +
            "SSL Mode=Require;Trust Server Certificate=true;";
    }

    private static string MaskUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var user = uri.UserInfo.Split(':', 2)[0];
            return $"{uri.Scheme}://{user}:***@{uri.Host}{uri.AbsolutePath}";
        }
        catch { return "<unparseable>"; }
    }
}
