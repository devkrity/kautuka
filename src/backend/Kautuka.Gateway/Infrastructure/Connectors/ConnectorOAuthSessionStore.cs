namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed class ConnectorOAuthSession
{
    public required string UserId { get; init; }

    public required string ConnectorId { get; init; }

    public required string CodeVerifier { get; init; }

    public string? DpopPrivateJwk { get; init; }

    public string? RedirectUri { get; init; }

    public string? ReturnOrigin { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// One in-memory authorization-code session per state. The PKCE verifier stays here until the token request.
/// </summary>
internal sealed class ConnectorOAuthSessionStore
{
    private const int maximumSessions = 4096;
    private readonly object gate = new();
    private readonly Dictionary<string, ConnectorOAuthSession> sessions = new(StringComparer.Ordinal);

    public bool Save(string state, ConnectorOAuthSession session)
    {
        lock (gate)
        {
            PruneExpired(DateTimeOffset.UtcNow);
            if (!sessions.ContainsKey(state) && sessions.Count >= maximumSessions)
            {
                return false;
            }

            sessions[state] = session;
            return true;
        }
    }

    public bool TryGet(string state, out ConnectorOAuthSession? session)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(state, out session)) return false;
            if (session.ExpiresAt > DateTimeOffset.UtcNow) return true;
            _ = sessions.Remove(state);
            session = null;
            return false;
        }
    }

    public bool TryTake(string state, out ConnectorOAuthSession? session)
    {
        lock (gate)
        {
            bool removed = sessions.Remove(state, out ConnectorOAuthSession? found);
            session = found;
            return removed;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach ((string state, ConnectorOAuthSession session) in sessions)
        {
            if (session.ExpiresAt <= now)
            {
                _ = sessions.Remove(state);
            }
        }
    }
}
