using System.Security.Cryptography;
using System.Text;

namespace Kautuka.Gateway.Domain.Auth;

internal sealed class ExternalLoginSession
{
    public required string Provider { get; init; }

    public required string CodeVerifier { get; init; }

    public required string Nonce { get; init; }

    public required string ReturnOrigin { get; init; }

    public required string CookieBindingHash { get; init; }

    public required string Intent { get; init; }

    public string? LinkUserId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class ExternalLoginSessionStore
{
    internal const int MaximumSessions = 2048;
    private readonly object gate = new();
    private readonly Dictionary<string, ExternalLoginSession> sessions = new(StringComparer.Ordinal);

    public static string CreateCookieBinding()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Save(string state, ExternalLoginSession session)
    {
        lock (gate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            if (sessions.Count >= MaximumSessions)
            {
                string? earliest = sessions.MinBy(item => item.Value.ExpiresAt).Key;
                if (earliest is not null)
                {
                    sessions.Remove(earliest);
                }
            }

            sessions[state] = session;
        }
    }

    public bool TryRemove(string state, string? cookieBinding, DateTimeOffset now, out ExternalLoginSession? session)
    {
        lock (gate)
        {
            PruneExpired(now);
            if (!sessions.TryGetValue(state, out ExternalLoginSession? found))
            {
                session = null;
                return false;
            }

            // Consume state even when the binding is wrong, so a guessed or stolen state cannot be retried.
            sessions.Remove(state);
            session = found;
            return cookieBinding is not null && FixedTimeEquals(found.CookieBindingHash, HashBinding(cookieBinding));
        }
    }

    internal static string HashBinding(string binding)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (string state in sessions.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
        {
            sessions.Remove(state);
        }
    }

    private static bool FixedTimeEquals(string expectedHex, string actualHex)
    {
        byte[] expected = Encoding.ASCII.GetBytes(expectedHex);
        byte[] actual = Encoding.ASCII.GetBytes(actualHex);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
