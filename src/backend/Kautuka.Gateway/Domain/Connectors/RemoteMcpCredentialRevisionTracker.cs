namespace Kautuka.Gateway.Domain.Connectors;

// Opaque process-local epochs prevent reuse of persisted remote catalogues across restarts.
internal sealed class RemoteMcpCredentialRevisionTracker
{
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private readonly Dictionary<(string ConnectorId, string Scope), Revision> revisions = new();
    private readonly Dictionary<(string Kind, string Id), int> policyChanges = new();
    private readonly HashSet<RemoteMcpOperationLease> leases = [];
    private readonly HashSet<(string ConnectorId, string Scope)> exclusiveChanges = [];
    private readonly object sync = new();
    private sealed record Revision(string Token, int Pending);

    public void BeginChange(string connectorId, string scope)
    {
        RemoteMcpOperationLease[] affected;
        lock (sync)
        {
            if (exclusiveChanges.Contains((connectorId, scope))) throw new InvalidOperationException("Connector credential activation is in progress.");
            revisions.TryGetValue((connectorId, scope), out Revision? previous);
            revisions[(connectorId, scope)] = new(Guid.NewGuid().ToString("N"), (previous?.Pending ?? 0) + 1);
            affected = [.. leases.Where(lease => lease.ConnectorId == connectorId
                && (scope == "system" ? lease.IncludeSystem : scope == "user:" + lease.UserId))];
        }
        Cancel(affected);
    }

    public IDisposable BeginExclusiveChange(string connectorId, string scope)
    {
        RemoteMcpOperationLease[] affected;
        lock (sync)
        {
            revisions.TryGetValue((connectorId, scope), out Revision? previous);
            if ((previous?.Pending ?? 0) != 0 || !exclusiveChanges.Add((connectorId, scope)))
                throw new InvalidOperationException("Connector credentials are being updated. Retry the operation.");
            revisions[(connectorId, scope)] = new(Guid.NewGuid().ToString("N"), 1);
            affected = [.. leases.Where(lease => lease.ConnectorId == connectorId
                && (scope == "system" ? lease.IncludeSystem : scope == "user:" + lease.UserId))];
        }
        Cancel(affected);
        return new ExclusiveChange(this, connectorId, scope);
    }

    private void EndExclusiveChange(string connectorId, string scope)
    {
        lock (sync)
        {
            exclusiveChanges.Remove((connectorId, scope));
            revisions[(connectorId, scope)] = new(Guid.NewGuid().ToString("N"), 0);
        }
    }

    public void EndChange(string connectorId, string scope)
    {
        lock (sync)
        {
            Revision previous = revisions[(connectorId, scope)];
            revisions[(connectorId, scope)] = new(Guid.NewGuid().ToString("N"), previous.Pending - 1);
        }
    }

    public IDisposable BeginPolicyChange(string kind, string id)
    {
        RemoteMcpOperationLease[] affected;
        lock (sync)
        {
            var key = (kind, id);
            policyChanges.TryGetValue(key, out int pending);
            policyChanges[key] = pending + 1;
            affected = [.. leases.Where(lease => Matches(lease, kind, id))];
        }
        Cancel(affected);
        return new PolicyChange(this, kind, id);
    }

    public RemoteMcpOperationLease Acquire(string connectorId, string userId, string? agentId, bool includeSystem, string? draftId = null)
    {
        lock (sync)
        {
            EnsureAvailable(connectorId, userId, agentId, includeSystem);
            if (draftId is not null && policyChanges.ContainsKey(("draft", draftId)))
                throw new InvalidOperationException("Connector draft is being canceled.");
            var lease = new RemoteMcpOperationLease(this, connectorId, userId, agentId, includeSystem, draftId);
            leases.Add(lease);
            return lease;
        }
    }

    public string Get(string connectorId, string userId, bool includeSystem)
    {
        lock (sync)
        {
            EnsureAvailable(connectorId, userId, null, includeSystem);
            revisions.TryGetValue((connectorId, "user:" + userId), out Revision? user);
            Revision? system = null;
            if (includeSystem) revisions.TryGetValue((connectorId, "system"), out system);
            return string.Concat(epoch, ":", user?.Token, ":", system?.Token);
        }
    }

    private void EnsureAvailable(string connectorId, string userId, string? agentId, bool includeSystem)
    {
        revisions.TryGetValue((connectorId, "user:" + userId), out Revision? user);
        Revision? system = null;
        if (includeSystem) revisions.TryGetValue((connectorId, "system"), out system);
        if ((user?.Pending ?? 0) > 0 || (system?.Pending ?? 0) > 0
            || policyChanges.ContainsKey(("connector", connectorId))
            || policyChanges.ContainsKey(("caller", userId))
            || (agentId is not null && policyChanges.ContainsKey(("agent", agentId))))
            throw new InvalidOperationException("Remote MCP authorization is changing. Retry the operation.");
    }

    private static bool Matches(RemoteMcpOperationLease lease, string kind, string id) => kind switch
    {
        "connector" => lease.ConnectorId == id,
        "caller" => lease.UserId == id,
        "agent" => lease.AgentId == id,
        "draft" => lease.DraftId == id,
        _ => false,
    };
    private static void Cancel(RemoteMcpOperationLease[] affected)
    {
        foreach (RemoteMcpOperationLease lease in affected) lease.Cancel();
    }
    private void EndPolicyChange(string kind, string id)
    {
        lock (sync)
        {
            var key = (kind, id);
            int remaining = policyChanges[key] - 1;
            if (remaining == 0) policyChanges.Remove(key); else policyChanges[key] = remaining;
        }
    }
    internal void Release(RemoteMcpOperationLease lease) { lock (sync) leases.Remove(lease); }
    internal int ActiveLeaseCount { get { lock (sync) return leases.Count; } }

    private sealed class ExclusiveChange(RemoteMcpCredentialRevisionTracker owner, string connectorId, string scope) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.EndExclusiveChange(connectorId, scope); }
    }

    private sealed class PolicyChange(RemoteMcpCredentialRevisionTracker owner, string kind, string id) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.EndPolicyChange(kind, id); }
    }
}

internal sealed class RemoteMcpOperationLease(RemoteMcpCredentialRevisionTracker owner, string connectorId, string userId, string? agentId, bool includeSystem, string? draftId) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Timer? expiryTimer;
    private int disposed;
    public string ConnectorId { get; } = connectorId;
    public string UserId { get; } = userId;
    public string? AgentId { get; } = agentId;
    public bool IncludeSystem { get; } = includeSystem;
    public string? DraftId { get; } = draftId;
    public CancellationToken Token => cancellation.Token;
    public void ScheduleExpiry(DateTimeOffset expiresAt)
    {
        TimeSpan delay = expiresAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) { Cancel(); return; }
        expiryTimer = new Timer(static state => ((RemoteMcpOperationLease)state!).Cancel(), this, delay, Timeout.InfiniteTimeSpan);
    }
    internal void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        // All registered callbacks are attempted by Cancel(). A faulty callback must not
        // strand a policy mutation or prevent cancellation of other affected operations.
        catch (AggregateException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Release(this);
        expiryTimer?.Dispose();
        Cancel();
        cancellation.Dispose();
    }
}
