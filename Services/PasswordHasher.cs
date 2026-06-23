using System.Security.Cryptography;
using System.Text;

namespace AttendanceMonitoring.Services;

/// <summary>
/// Werkzeug-compatible-ish PBKDF2 hasher. The on-disk format is:
/// <c>pbkdf2-sha256$&lt;iter&gt;$&lt;salt-base64&gt;$&lt;hash-base64&gt;</c>.
/// This is a self-contained re-implementation so we don't pull the whole
/// ASP.NET Identity stack just for password hashing.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 260_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Algo = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password must not be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(password, salt, Iterations, HashBytes);
        return $"{Algo}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!parts[0].Equals(Algo, StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(parts[1], out var iter) || iter <= 0) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Pbkdf2(password, salt, iter, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations, int bytes)
    {
        using var deriver = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256);
        return deriver.GetBytes(bytes);
    }
}
