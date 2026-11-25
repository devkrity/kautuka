using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.NetworkAccessController;

public sealed record RegistryEntry(
    string Ip,
    string? UserId,
    string? AgentId,
    string ContainerId,
    string Kind,
    string? ConnectorId,
    string ExecContext,
    string Egress,
    string Attribution,
    string? CompatProfile,
    DateTimeOffset RegisteredUtc,
    string? Purpose = null);

public sealed class WorkloadContainerEventArgs(string containerId) : EventArgs
{
    public string ContainerId { get; } = containerId;
}

public sealed class WorkloadContainerIdPromotedEventArgs(string previousContainerId, string actualContainerId) : EventArgs
{
    public string PreviousContainerId { get; } = previousContainerId;

    public string ActualContainerId { get; } = actualContainerId;
}

public interface IWorkloadRegistry
{
    long RegistryConflicts { get; }

    int Count { get; }

    event EventHandler<WorkloadContainerEventArgs>? ContainerDeregistered
    {
        add { }
        remove { }
    }

    event EventHandler<WorkloadContainerIdPromotedEventArgs>? ContainerIdPromoted
    {
        add { }
        remove { }
    }

    void Register(RegistryEntry entry);

    bool Deregister(string ip, string? containerId = null);
    bool DeregisterBuilder(string ip, DateTimeOffset registeredAt);

    bool UpdateContainerId(string ip, string previousContainerId, string actualContainerId) => false;

    /// <summary>Atomically changes a matching workload identity to deny-all without replacing a different container.</summary>
    bool TryQuarantine(string ip, string expectedContainerId, string? alternateContainerId = null) => false;

    /// <summary>Atomically promotes a quarantined exact identity to caller-supplied verified policy.</summary>
    bool TryPromoteVerified(RegistryEntry expectedQuarantined, RegistryEntry desired) => false;

    /// <summary>Registers a restricted candidate only when its IP is not already owned.</summary>
    bool TryRegisterQuarantined(RegistryEntry candidate) => false;

    /// <summary>Returns whether this exact workload identity was verified in the current process.</summary>
    bool IsVerified(string ip, string containerId) => false;

    void Rebuild(IEnumerable<RegistryEntry> entries);

    bool TryGetByIp(string? ip, out RegistryEntry? entry);

    RegistryEntry? FindByIp(string? ip) => TryGetByIp(ip, out var entry) ? entry : null;

    IReadOnlyList<RegistryEntry> GetAll();
}

/// <summary>
/// Thread-safe in-memory map from workload IPv4 address to actor identity.
/// Owned inside Manager and updated in-process during container launch and reconcile.
/// </summary>
public sealed class WorkloadRegistry(ILogger<WorkloadRegistry>? logger = null) : IWorkloadRegistry
{
    private readonly ConcurrentDictionary<string, RegistryEntry> entriesByIp = new(StringComparer.Ordinal);
    private readonly HashSet<(string Ip, string ContainerId)> verifiedIdentities = [];
    private readonly object writeLock = new();
    private readonly ILogger<WorkloadRegistry> logger = logger ?? NullLogger<WorkloadRegistry>.Instance;
    private long registryConflicts;

    public long RegistryConflicts => Interlocked.Read(ref registryConflicts);

    public int Count => entriesByIp.Count;

    public event EventHandler<WorkloadContainerEventArgs>? ContainerDeregistered;

    public event EventHandler<WorkloadContainerIdPromotedEventArgs>? ContainerIdPromoted;

    public void Register(RegistryEntry entry)
    {
        RegistryEntry normalized = ValidateAndNormalize(entry);
        string? evictedContainerId = null;
        lock (writeLock)
        {
            if (entriesByIp.TryGetValue(normalized.Ip, out RegistryEntry? existing)
                && !string.Equals(existing.ContainerId, normalized.ContainerId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref registryConflicts);
                evictedContainerId = existing.ContainerId;
                logger.LogWarning(
                    "Workload IP {Ip} registration conflict: replacing container {OldContainerId} with {NewContainerId}.",
                    normalized.Ip,
                    existing.ContainerId,
                    normalized.ContainerId);
            }

            entriesByIp[normalized.Ip] = normalized;
            verifiedIdentities.Remove((normalized.Ip, normalized.ContainerId));
        }

        if (!string.IsNullOrEmpty(evictedContainerId))
        {
            ContainerDeregistered?.Invoke(this, new WorkloadContainerEventArgs(evictedContainerId));
        }
    }

    public bool Deregister(string ip, string? containerId = null)
        => DeregisterCore(ip, containerId, builderRegisteredAt: null);

    public bool DeregisterBuilder(string ip, DateTimeOffset registeredAt)
        => DeregisterCore(ip, containerId: null, builderRegisteredAt: registeredAt);

    private bool DeregisterCore(string ip, string? containerId, DateTimeOffset? builderRegisteredAt)
    {
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

        string? removedContainerId = null;
        bool removed;
        lock (writeLock)
        {
            if (!entriesByIp.TryGetValue(normalizedIp, out RegistryEntry? existing))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(containerId)
                && !string.Equals(existing.ContainerId, containerId, StringComparison.Ordinal))
            {
                return false;
            }

            if (builderRegisteredAt.HasValue
                && (existing.Kind != "builder" || existing.RegisteredUtc != builderRegisteredAt.Value))
            {
                return false;
            }

            removed = entriesByIp.TryRemove(normalizedIp, out _);
            if (removed)
            {
                removedContainerId = existing.ContainerId;
                verifiedIdentities.Remove((normalizedIp, existing.ContainerId));
            }
        }

        if (removed && !string.IsNullOrEmpty(removedContainerId))
        {
            ContainerDeregistered?.Invoke(this, new WorkloadContainerEventArgs(removedContainerId));
        }

        return removed;
    }

    public bool UpdateContainerId(string ip, string previousContainerId, string actualContainerId)
    {
        if (string.IsNullOrWhiteSpace(previousContainerId)
            || string.IsNullOrWhiteSpace(actualContainerId)
            || !NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

        string trimmedActual = actualContainerId.Trim();
        bool promoted = false;
        lock (writeLock)
        {
            if (!entriesByIp.TryGetValue(normalizedIp, out RegistryEntry? existing))
            {
                return false;
            }

            if (string.Equals(existing.ContainerId, trimmedActual, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(existing.ContainerId, previousContainerId.Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            entriesByIp[normalizedIp] = existing with { ContainerId = trimmedActual };
            verifiedIdentities.Remove((normalizedIp, existing.ContainerId));
            verifiedIdentities.Remove((normalizedIp, trimmedActual));
            promoted = true;
        }

        if (promoted)
        {
            ContainerIdPromoted?.Invoke(this, new WorkloadContainerIdPromotedEventArgs(previousContainerId.Trim(), trimmedActual));
        }

        return true;
    }

    public bool TryQuarantine(string ip, string expectedContainerId, string? alternateContainerId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContainerId);
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

        string? probeContainerId = null;
        lock (writeLock)
        {
            if (!entriesByIp.TryGetValue(normalizedIp, out RegistryEntry? current)
                || (!string.Equals(current.ContainerId, expectedContainerId, StringComparison.Ordinal)
                    && !string.Equals(current.ContainerId, alternateContainerId, StringComparison.Ordinal)))
            {
                return false;
            }

            entriesByIp[normalizedIp] = current with { Egress = "deny-all", Attribution = "none" };
            verifiedIdentities.Remove((normalizedIp, current.ContainerId));
            if (string.Equals(current.Purpose, "probe", StringComparison.OrdinalIgnoreCase))
            {
                probeContainerId = current.ContainerId;
            }
        }

        if (probeContainerId is not null)
        {
            // Existing probe grants are ephemeral and must not remain usable after quarantine.
            ContainerDeregistered?.Invoke(this, new WorkloadContainerEventArgs(probeContainerId));
        }

        return true;
    }

    public bool TryPromoteVerified(RegistryEntry expectedQuarantined, RegistryEntry desired)
    {
        RegistryEntry expected = ValidateAndNormalize(expectedQuarantined);
        RegistryEntry promoted = ValidateAndNormalize(desired);
        if (expected.Egress is not ("deny-all" or "proxy-only") || expected.Attribution != "none"
            || !string.Equals(expected.Ip, promoted.Ip, StringComparison.Ordinal)
            || !string.Equals(expected.ContainerId, promoted.ContainerId, StringComparison.Ordinal)
            || (expected with { Egress = promoted.Egress, Attribution = promoted.Attribution }) != promoted)
        {
            return false;
        }

        lock (writeLock)
        {
            if (!entriesByIp.TryGetValue(expected.Ip, out RegistryEntry? current) || current != expected)
            {
                return false;
            }

            entriesByIp[expected.Ip] = promoted;
            verifiedIdentities.Add((promoted.Ip, promoted.ContainerId));
            return true;
        }
    }

    public bool TryRegisterQuarantined(RegistryEntry candidate)
    {
        RegistryEntry normalized = ValidateAndNormalize(candidate);
        if (normalized.Egress is not ("deny-all" or "proxy-only") || normalized.Attribution != "none")
        {
            return false;
        }

        lock (writeLock)
        {
            if (entriesByIp.ContainsKey(normalized.Ip))
            {
                return false;
            }

            entriesByIp[normalized.Ip] = normalized;
            verifiedIdentities.Remove((normalized.Ip, normalized.ContainerId));
            return true;
        }
    }

    public bool IsVerified(string ip, string containerId)
    {
        if (string.IsNullOrWhiteSpace(containerId)
            || !NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

        lock (writeLock)
        {
            return verifiedIdentities.Contains((normalizedIp, containerId.Trim()))
                && entriesByIp.TryGetValue(normalizedIp, out RegistryEntry? entry)
                && string.Equals(entry.ContainerId, containerId.Trim(), StringComparison.Ordinal);
        }
    }

    public void Rebuild(IEnumerable<RegistryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var validated = new List<RegistryEntry>();
        foreach (RegistryEntry entry in entries)
        {
            validated.Add(ValidateAndNormalize(entry));
        }

        var activeIds = validated.Select(static e => e.ContainerId).ToHashSet(StringComparer.Ordinal);
        List<string> removedIds;
        lock (writeLock)
        {
            verifiedIdentities.Clear();
            removedIds = entriesByIp.Values
                .Select(static e => e.ContainerId)
                .Where(id => !activeIds.Contains(id))
                .ToList();

            entriesByIp.Clear();
            foreach (RegistryEntry normalized in validated)
            {
                if (entriesByIp.TryGetValue(normalized.Ip, out RegistryEntry? existing)
                    && !string.Equals(existing.ContainerId, normalized.ContainerId, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref registryConflicts);
                    logger.LogWarning(
                        "Workload IP {Ip} conflict during rebuild: replacing container {OldContainerId} with {NewContainerId}.",
                        normalized.Ip,
                        existing.ContainerId,
                        normalized.ContainerId);
                }

                entriesByIp[normalized.Ip] = normalized;
            }
        }

        foreach (string removedId in removedIds)
        {
            ContainerDeregistered?.Invoke(this, new WorkloadContainerEventArgs(removedId));
        }
    }

    public bool TryGetByIp(string? ip, out RegistryEntry? entry)
    {
        entry = null;
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

        return entriesByIp.TryGetValue(normalizedIp, out entry);
    }

    public RegistryEntry? FindByIp(string? ip) => TryGetByIp(ip, out var entry) ? entry : null;

    public IReadOnlyList<RegistryEntry> GetAll()
        => [.. entriesByIp.Values];

    public static RegistryEntry ValidateAndNormalize(RegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(entry.Ip, out string normalizedIp, out IPAddress? _))
        {
            throw new ArgumentException("Registry entry requires a valid IPv4 address.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.ContainerId))
        {
            throw new ArgumentException("Registry entry requires a non-empty containerId.", nameof(entry));
        }

        if (entry.Kind is not ("agent-sandbox" or "connector" or "builder" or "system"))
        {
            throw new ArgumentException($"Unsupported workload kind '{entry.Kind}'.", nameof(entry));
        }

        if (entry.Kind == "system")
        {
            if (entry.Egress != "deny-all")
            {
                throw new ArgumentException("System workload kind is restricted strictly to system:: containers with deny-all egress.", nameof(entry));
            }
        }

        if (entry.ExecContext is not ("isolated" or "isolated-with-agent-files" or "agent-sandbox" or "builder" or "system"))
        {
            throw new ArgumentException($"Unsupported execution context '{entry.ExecContext}'.", nameof(entry));
        }

        if (entry.Egress is not ("normal" or "deny-all" or "proxy-only"))
        {
            throw new ArgumentException($"Unsupported egress mode '{entry.Egress}'.", nameof(entry));
        }

        if (entry.Attribution is not ("user-agent" or "user" or "none"))
        {
            throw new ArgumentException($"Unsupported attribution mode '{entry.Attribution}'.", nameof(entry));
        }

        string? userId = NormalizeOptional(entry.UserId);
        string? agentId = NormalizeOptional(entry.AgentId);
        string? connectorId = NormalizeOptional(entry.ConnectorId);

        if (userId == "-" || agentId == "-")
        {
            throw new ArgumentException("Placeholder '-' identities are not allowed.", nameof(entry));
        }

        string egress = entry.Egress;
        string attribution = entry.Attribution;

        if (entry.ContainerId.StartsWith("system::", StringComparison.Ordinal))
        {
            egress = "deny-all";
            attribution = "none";
        }

        if (egress is "deny-all" or "proxy-only")
        {
            if (attribution != "none")
                throw new ArgumentException("deny-all and proxy-only workloads must have attribution = 'none'.", nameof(entry));
        }
        else if (attribution == "user-agent")
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(agentId))
                throw new ArgumentException("Agent-attributed workload requires both userId and agentId.", nameof(entry));
        }
        else if (attribution == "user")
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(connectorId) || !string.IsNullOrEmpty(agentId))
                throw new ArgumentException("User-attributed connector requires userId and connectorId without agentId.", nameof(entry));
            if (entry.Kind == "connector" && entry.Purpose is not ("probe" or "save-discovery"))
                throw new ArgumentException("A connector without an agent must be a probe or save discovery.", nameof(entry));
        }
        else
        {
            throw new ArgumentException("Normal egress container must be attributed to a user or user+agent.", nameof(entry));
        }
        return entry with
        {
            Ip = normalizedIp,
            UserId = userId,
            AgentId = agentId,
            ConnectorId = connectorId,
            Egress = egress,
            Attribution = attribution,
            CompatProfile = NormalizeOptional(entry.CompatProfile),
            Purpose = NormalizeOptional(entry.Purpose),
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
