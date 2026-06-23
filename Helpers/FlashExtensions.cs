using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace AttendanceMonitoring.Helpers;

/// <summary>
/// Tiny "flash messages" helper that mirrors Flask's <c>flash()</c> /
/// <c>get_flashed_messages()</c>. Messages are stored in <see cref="ITempDataDictionary"/>
/// and consumed once on the next request.
/// </summary>
public static class FlashExtensions
{
    private const string Key = "__flashes";

    public record FlashMessage(string Category, string Message);

    public static void Flash(this ITempDataDictionary temp, string message, string category = "info")
    {
        var list = ReadList(temp);
        list.Add(new FlashMessage(category, message));
        temp[Key] = JsonSerializer.Serialize(list);
    }

    public static IReadOnlyList<FlashMessage> ConsumeFlashes(this ITempDataDictionary temp)
    {
        if (!temp.TryGetValue(Key, out var raw) || raw is not string json || string.IsNullOrEmpty(json))
            return Array.Empty<FlashMessage>();

        // Reading from TempData removes the entry on save; force-remove just in case.
        temp.Remove(Key);
        try
        {
            var list = JsonSerializer.Deserialize<List<FlashMessage>>(json);
            return list ?? new List<FlashMessage>();
        }
        catch (JsonException)
        {
            return Array.Empty<FlashMessage>();
        }
    }

    private static List<FlashMessage> ReadList(ITempDataDictionary temp)
    {
        if (!temp.TryGetValue(Key, out var raw) || raw is not string json || string.IsNullOrEmpty(json))
            return new List<FlashMessage>();
        try
        {
            return JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? new List<FlashMessage>();
        }
        catch (JsonException)
        {
            return new List<FlashMessage>();
        }
    }
}
