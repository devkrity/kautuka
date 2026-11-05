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

    public string? TabBindingHash { get; init; }

    public string? BrokerFlowId { get; init; }

    public bool BrowserConfirmed { get; set; }

    public string? BrokerOrigin { get; init; }

    public bool CompletionBusy { get; set; }

    public string? CompletionFingerprint { get; set; }

    public AuthLoginResponse? CompletedResponse { get; set; }

    public string? CompletedRefreshToken { get; set; }

    public DateTimeOffset CompletedAccessExpiresAt { get; set; }

    public DateTimeOffset CompletedRefreshExpiresAt { get; set; }

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
                string? earliest = sessions.Where(item => item.Value.TabBindingHash is null)
                    .OrderBy(item => item.Value.ExpiresAt).Select(item => item.Key).FirstOrDefault();
                if (earliest is null) return;
                sessions.Remove(earliest);
            }

            sessions[state] = session;
        }
    }

    public bool TrySaveBound(string state, ExternalLoginSession session)
    {
        lock (gate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            if (sessions.Count >= MaximumSessions || sessions.ContainsKey(state)) return false;
            sessions.Add(state, session);
            return true;
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

            // A legacy callback must never consume a tab-bound operation.
            if (found.TabBindingHash is not null)
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

    public bool TryGetBound(string state, string? cookieBinding, string? tabBinding, DateTimeOffset now, out ExternalLoginSession? session)
    {
        lock (gate)
        {
            PruneExpired(now);
            if (sessions.TryGetValue(state, out ExternalLoginSession? found)
                && cookieBinding is not null && tabBinding is not null && found.TabBindingHash is not null
                && FixedTimeEquals(found.CookieBindingHash, HashBinding(cookieBinding))
                && FixedTimeEquals(found.TabBindingHash, HashBinding(tabBinding)))
            {
                session = found;
                return true;
            }
            session = null;
            return false;
        }
    }

    public bool TryConfirmBrowser(string state, string? cookieBinding, string? tabBinding, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!TryGetBound(state, cookieBinding, tabBinding, now, out ExternalLoginSession? session) || session is null) return false;
            session.BrowserConfirmed = true;
            return true;
        }
    }

    public bool TryBeginCompletion(string state, string? cookieBinding, string? tabBinding, DateTimeOffset now, out ExternalLoginSession? session)
    {
        lock (gate)
        {
            if (!TryGetBound(state, cookieBinding, tabBinding, now, out session) || session is null
                || !session.BrowserConfirmed || session.CompletionBusy) return false;
            session.CompletionBusy = true;
            return true;
        }
    }

    public void EndCompletion(ExternalLoginSession session)
    {
        lock (gate) session.CompletionBusy = false;
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
