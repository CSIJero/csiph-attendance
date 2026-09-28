using System.Text.Json;

namespace AttendanceMonitoring.Services;

public static class FaceDescriptor
{
    public const int ValueCount = 128;
    public const double MaximumEuclideanDistance = 0.55;

    public static bool TryParse(string? raw, out double[] values)
    {
        values = Array.Empty<double>();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<double[]>(raw);
            if (parsed is null || parsed.Length != ValueCount) return false;
            if (parsed.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10))
                return false;
            values = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Serialize(IEnumerable<double> values) =>
        JsonSerializer.Serialize(values);

    public static double Distance(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != ValueCount || right.Count != ValueCount)
            return double.PositiveInfinity;

        double sum = 0;
        for (var i = 0; i < ValueCount; i++)
        {
            var difference = left[i] - right[i];
            sum += difference * difference;
        }
        return Math.Sqrt(sum);
    }
}
