using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Lightweight perceptual hash ("aHash") implementation used for the
/// liveness sanity check on check-in selfies. The image is downscaled
/// to 8×8 greyscale, then every pixel above the mean contributes a 1
/// bit to the resulting 64-bit fingerprint.
///
/// This is intentionally NOT a real face-recognition stack. We're not
/// shipping ONNX Runtime + an embedding model in a Render free-tier
/// container — instead we detect gross mismatches (someone clocking in
/// with a completely different face). A Hamming distance ≤
/// <see cref="Models.Constants.FaceMatchMaxDistance"/> against the
/// enrolled hash is treated as a match.
///
/// Uses <c>System.Drawing.Common</c>, which is Windows-only. On Linux
/// runtimes (Render's deploy environment) the runtime package supports
/// libgdiplus once the Dockerfile installs it, but we also gate the
/// feature behind a try/catch so a decode failure simply records
/// FaceMatchStatus = "Unavailable" instead of blocking check-ins.
/// </summary>
public static class FaceHash
{
    /// <summary>
    /// Computes a 16-hex-char (64-bit) average hash from a data-URL or
    /// raw JPEG/PNG byte array. Returns null when the bitmap cannot be
    /// decoded (corrupt image, unsupported platform, …).
    /// </summary>
    public static string? Compute(string? dataUrlOrBase64)
    {
        if (string.IsNullOrWhiteSpace(dataUrlOrBase64)) return null;
        byte[] bytes;
        try
        {
            var b64 = dataUrlOrBase64;
            var comma = b64.IndexOf(',');
            if (comma >= 0) b64 = b64[(comma + 1)..];
            bytes = Convert.FromBase64String(b64);
        }
        catch { return null; }
        return Compute(bytes);
    }

    /// <summary>Computes the 64-bit perceptual hash from image bytes.</summary>
    public static string? Compute(byte[] imageBytes)
    {
        if (imageBytes is null || imageBytes.Length < 64) return null;
        try
        {
            using var ms = new MemoryStream(imageBytes);
#pragma warning disable CA1416 // gated by try/catch for non-Windows
            using var original = Image.FromStream(ms);
            using var resized = new Bitmap(8, 8);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, 8, 8);
            }

            // Build greyscale buffer + running sum for the threshold.
            Span<int> grey = stackalloc int[64];
            long sum = 0;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var p = resized.GetPixel(x, y);
                    var v = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                    grey[y * 8 + x] = v;
                    sum += v;
                }
            }
            var mean = sum / 64;
            ulong hash = 0UL;
            for (int i = 0; i < 64; i++)
            {
                if (grey[i] > mean) hash |= 1UL << i;
            }
            return hash.ToString("X16", CultureInfo.InvariantCulture);
#pragma warning restore CA1416
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hamming distance between two 16-hex-char hashes. Returns 64
    /// (maximum) when either operand is malformed.
    /// </summary>
    public static int Distance(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 64;
        if (!ulong.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x)) return 64;
        if (!ulong.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var y)) return 64;
        return System.Numerics.BitOperations.PopCount(x ^ y);
    }
}
