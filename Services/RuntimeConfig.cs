using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// In-memory runtime settings cache backed by the runtime_settings table.
/// </summary>
public static class RuntimeConfig
{
    public const string GraceOnsitePhKey = "grace.ph.onsite";
    public const string GraceOffsitePhKey = "grace.ph.offsite";
    public const string GraceIndiaKey = "grace.india";
    public const string BusinessUnitsKey = "business_units";

    private static readonly object Gate = new();
    private static Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static async Task LoadFromDbAsync(AppDbContext db, IConfiguration config)
    {
        var rows = await db.RuntimeSettings.AsNoTracking().ToListAsync();
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [GraceOnsitePhKey] = (config["AttendanceMonitoring:Grace:OnsiteMinutes"] ?? "30").Trim(),
            [GraceOffsitePhKey] = (config["AttendanceMonitoring:Grace:OffsiteMinutes"] ?? "0").Trim(),
            [GraceIndiaKey] = (config["AttendanceMonitoring:Grace:IndiaMinutes"] ?? "60").Trim(),
            [BusinessUnitsKey] = string.Join(";", Constants.BusinessUnits),
        };

        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.Key))
            {
                next[r.Key.Trim()] = r.Value?.Trim() ?? string.Empty;
            }
        }

        lock (Gate)
        {
            _values = next;
        }
    }

    public static int GraceOnsitePh => GetInt(GraceOnsitePhKey, 30);
    public static int GraceOffsitePh => GetInt(GraceOffsitePhKey, 0);
    public static int GraceIndia => GetInt(GraceIndiaKey, 60);

    public static int GetInt(string key, int fallback)
    {
        var raw = Get(key);
        if (!int.TryParse(raw, out var v)) return fallback;
        return Math.Clamp(v, 0, 240);
    }

    public static string Get(string key)
    {
        lock (Gate)
        {
            return _values.TryGetValue(key, out var v) ? v : string.Empty;
        }
    }

    public static List<string> GetBusinessUnits()
    {
        var raw = Get(BusinessUnitsKey);
        var parts = raw
            .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Constants.NormaliseBusinessUnit(p) ?? p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parts.Count == 0)
        {
            return Constants.BusinessUnits.ToList();
        }

        return parts;
    }

    public static string? NormaliseBusinessUnit(string? raw)
    {
        var v = Constants.NormaliseBusinessUnit(raw);
        if (string.IsNullOrWhiteSpace(v)) return null;
        var all = GetBusinessUnits();
        var canonical = all.FirstOrDefault(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
        return canonical ?? v;
    }

    public static bool IsKnownBusinessUnit(string? raw)
    {
        var v = NormaliseBusinessUnit(raw);
        if (string.IsNullOrWhiteSpace(v)) return false;
        return GetBusinessUnits().Any(b => string.Equals(b, v, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task SaveAsync(AppDbContext db, Dictionary<string, string> updates)
    {
        var keys = updates.Keys.ToList();
        var existing = await db.RuntimeSettings.Where(r => keys.Contains(r.Key)).ToDictionaryAsync(r => r.Key);

        foreach (var kv in updates)
        {
            if (existing.TryGetValue(kv.Key, out var row))
            {
                row.Value = kv.Value;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.RuntimeSettings.Add(new RuntimeSetting
                {
                    Key = kv.Key,
                    Value = kv.Value,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync();
    }
}
