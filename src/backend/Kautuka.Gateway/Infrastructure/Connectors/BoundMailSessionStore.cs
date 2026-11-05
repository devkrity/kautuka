using System.Security.Cryptography;
using System.Text;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed class BoundMailSession
{
    public required string UserId { get; init; }
    public required string ConnectorId { get; init; }
    public required string CatalogConnectorId { get; init; }
    public required string Provider { get; init; }
    public required string FlowId { get; init; }
    public required string ConnectionId { get; init; }
    public required string Verifier { get; init; }
    public required string DpopPrivateJwk { get; init; }
    public required string Origin { get; init; }
    public required string BrokerOrigin { get; init; }
    public required long ExpectedGeneration { get; init; }
    public required string CookieHash { get; init; }
    public required string TabHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public bool BrowserConfirmed { get; set; }
    public bool Busy { get; set; }
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public override string ToString() => "BoundMailSession { Credentials = [redacted] }";
}

// State remains local to the Gateway and is never a substitute for checking
// the current authenticated user, origin, cookie and tab at every operation.
internal sealed class BoundMailSessionStore
{
    internal sealed class StartIntent(string userId, string connectorId)
    {
        public string UserId { get; } = userId;
        public string ConnectorId { get; } = connectorId;
        public bool Cancelled { get; set; }
    }
    private const int MaximumSessions = 4096;
    private readonly Lock gate = new();
    private readonly Dictionary<string, BoundMailSession> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<StartIntent> starts = [];
    private readonly HashSet<BoundMailSession> completions = [];
    private bool brokerSwitchPending;
    private bool brokerRotationInProgress;
    private readonly Dictionary<string, (DateTimeOffset Window, int Count)> attempts = new(StringComparer.Ordinal);

    public bool AllowAttempt(string user, DateTimeOffset now)
    {
        lock (gate)
        {
            if (attempts.Count >= MaximumSessions)
                foreach (string key in attempts.Where(pair => now - pair.Value.Window >= TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) attempts.Remove(key);
            if (!attempts.TryGetValue(user, out var previous))
            {
                if (attempts.Count >= MaximumSessions) return false;
                attempts[user] = (now, 1);
                return true;
            }
            var next = now - previous.Window >= TimeSpan.FromMinutes(1) ? (now, 1) : (previous.Window, previous.Count + 1);
            attempts[user] = next;
            return next.Item2 <= 20;
        }
    }

    public StartIntent? BeginStart(string user, string connectorId)
    {
        lock (gate)
        {
            if (brokerSwitchPending || starts.Count >= MaximumSessions) return null;
            var intent = new StartIntent(user, connectorId);
            starts.Add(intent);
            return intent;
        }
    }

    public bool SaveStarted(StartIntent intent, string state, BoundMailSession session, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!starts.Contains(intent) || intent.Cancelled || intent.UserId != session.UserId || intent.ConnectorId != session.ConnectorId) return false;
            return TrySave(state, session, now);
        }
    }

    public void EndStart(StartIntent intent) { lock (gate) starts.Remove(intent); }

    public bool TryFenceBrokerSwitch(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (string key in sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) sessions.Remove(key);
            if (starts.Count != 0 || sessions.Count != 0 || completions.Count != 0 || brokerRotationInProgress) return false;
            brokerSwitchPending = true;
            return true;
        }
    }

    public void ClearBrokerSwitchFence() { lock (gate) brokerSwitchPending = false; }
    public bool IsBrokerSwitchPending { get { lock (gate) return brokerSwitchPending; } }
    public bool TryBeginBrokerRotation()
    {
        lock (gate)
        {
            if (brokerSwitchPending || brokerRotationInProgress) return false;
            brokerRotationInProgress = true;
            return true;
        }
    }
    public void EndBrokerRotation() { lock (gate) brokerRotationInProgress = false; }

    public bool TrySave(string state, BoundMailSession session, DateTimeOffset now)
    {
        if (!IsToken(state) || !IsToken(session.FlowId) || !IsToken(session.ConnectionId)
            || !IsToken(session.CookieHash) || !IsToken(session.TabHash) || session.ExpiresAt <= now
            || string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.ConnectorId) || string.IsNullOrWhiteSpace(session.CatalogConnectorId)
            || session.Provider is not ("google" or "microsoft") || !IsVerifier(session.Verifier)
            || session.ExpectedGeneration is < 0 or > 9007199254740991L
            || !Uri.TryCreate(session.BrokerOrigin, UriKind.Absolute, out Uri? broker) || broker.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(broker.UserInfo) || broker.GetLeftPart(UriPartial.Authority) != session.BrokerOrigin) return false;
        lock (gate)
        {
            foreach (string key in sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) sessions.Remove(key);
            if (sessions.ContainsKey(state) || sessions.Count >= MaximumSessions) return false;
            sessions.Add(state, session);
            return true;
        }
    }

    public bool TryGet(string state, string? user, string? origin, string? cookie, string? tab,
        DateTimeOffset now, out BoundMailSession? session)
    {
        lock (gate) return TryGetCore(state, user, origin, cookie, tab, now, out session);
    }

    public bool Confirm(string state, string? user, string? origin, string? cookie, string? tab, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!TryGetCore(state, user, origin, cookie, tab, now, out BoundMailSession? session) || session is null) return false;
            session.BrowserConfirmed = true;
            return true;
        }
    }

    public bool TryBegin(string state, string? user, string? origin, string? cookie, string? tab,
        DateTimeOffset now, out BoundMailSession? session)
    {
        lock (gate)
        {
            if (!TryGetCore(state, user, origin, cookie, tab, now, out session) || session is null || !session.BrowserConfirmed || session.Busy || session.Completed) return false;
            session.Busy = true;
            completions.Add(session);
            return true;
        }
    }

    public void End(string state, BoundMailSession session, bool completed)
    {
        lock (gate)
        {
            completions.Remove(session);
            if (!sessions.TryGetValue(state, out BoundMailSession? current) || !ReferenceEquals(current, session) || current.Cancelled) return;
            if (completed) current.Completed = true;
            current.Busy = false;
        }
    }

    public bool Cancel(string state, string? user, string? origin, string? cookie, string? tab, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!TryGetCore(state, user, origin, cookie, tab, now, out BoundMailSession? session, allowCancelled: true) || session is null) return false;
            session.Cancelled = true;
            return true;
        }
    }

    public bool TryGetForCancellation(string state, string? user, string? origin, string? cookie, string? tab,
        DateTimeOffset now, out BoundMailSession? session)
    {
        lock (gate) return TryGetCore(state, user, origin, cookie, tab, now, out session, allowCancelled: true);
    }

    // Preserve cancellation metadata until broker cancellation is confirmed;
    // an in-flight completion must revalidate before staging or activation.
    public IReadOnlyList<BoundMailSession> CancelForConnector(string user, string connectorId, DateTimeOffset now)
    {
        lock (gate)
        {
            var result = new List<BoundMailSession>();
            foreach (StartIntent intent in starts)
                if (intent.UserId == user && intent.ConnectorId == connectorId) intent.Cancelled = true;
            foreach (BoundMailSession session in sessions.Values)
            {
                if (session.ExpiresAt <= now || session.UserId != user || session.ConnectorId != connectorId) continue;
                session.Cancelled = true;
                if (!session.Completed) result.Add(session);
            }
            return result;
        }
    }

    public void RemoveCancelled(BoundMailSession session)
    {
        lock (gate)
        {
            string? key = sessions.FirstOrDefault(pair => ReferenceEquals(pair.Value, session) && session.Cancelled).Key;
            if (key is not null) sessions.Remove(key);
        }
    }

    public static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private bool TryGetCore(string state, string? user, string? origin, string? cookie, string? tab,
        DateTimeOffset now, out BoundMailSession? session, bool allowCancelled = false)
    {
        session = null;
        if (!IsToken(state) || !IsToken(cookie) || !IsToken(tab) || !sessions.TryGetValue(state, out BoundMailSession? found)) return false;
        if (found.ExpiresAt <= now) { sessions.Remove(state); return false; }
        if (found.Cancelled && !allowCancelled) return false;
        if (!string.Equals(found.UserId, user, StringComparison.Ordinal) || !string.Equals(found.Origin, origin, StringComparison.Ordinal)
            || !Equal(found.CookieHash, Hash(cookie!)) || !Equal(found.TabHash, Hash(tab!))) return false;
        session = found;
        return true;
    }

    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    internal static bool IsToken(string? value) => value is { Length: 43 }
        && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
    private static bool IsVerifier(string? value) => value is { Length: >= 43 and <= 128 }
        && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.' or '~');
}
