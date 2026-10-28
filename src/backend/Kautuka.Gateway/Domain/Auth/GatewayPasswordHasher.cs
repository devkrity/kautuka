using System.Security.Cryptography;

namespace Kautuka.Gateway.Domain.Auth;

internal static class GatewayPasswordHasher
{
    private const string prefix = "pbkdf2-sha256";
    private const int iterations = 210_000;
    private const int saltSize = 16;
    private const int hashSize = 32;
    private const string dummyStoredHash = "pbkdf2-sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        byte[] salt = RandomNumberGenerator.GetBytes(saltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, hashSize);
        return $"{prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyOrEqualize(string? storedHash, string candidate)
    {
        if (!string.IsNullOrWhiteSpace(storedHash))
        {
            return Verify(storedHash, candidate);
        }

        Verify(dummyStoredHash, candidate);
        return false;
    }

    public static bool Verify(string storedHash, string candidate)
    {
        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], prefix, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out int storedIterations) || storedIterations < 100_000)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < saltSize || expected.Length < hashSize)
            {
                return false;
            }

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, storedIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
