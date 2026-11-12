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
    bool Systemwide,
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

    bool UpdateContainerId(string ip, string previousContainerId, string actualContainerId) => false;

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
        }

        if (!string.IsNullOrEmpty(evictedContainerId))
        {
            ContainerDeregistered?.Invoke(this, new WorkloadContainerEventArgs(evictedContainerId));
        }
    }

    public bool Deregister(string ip, string? containerId = null)
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

            removed = entriesByIp.TryRemove(normalizedIp, out _);
            if (removed)
            {
                removedContainerId = existing.ContainerId;
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
            promoted = true;
        }

        if (promoted)
        {
            ContainerIdPromoted?.Invoke(this, new WorkloadContainerIdPromotedEventArgs(previousContainerId.Trim(), trimmedActual));
        }

        return true;
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

        if (entry.ContainerId.StartsWith("system::", StringComparison.Ordinal)
            || (!entry.Systemwide && string.IsNullOrEmpty(agentId) && !string.Equals(entry.Purpose, "probe", StringComparison.OrdinalIgnoreCase)))
        {
            egress = "deny-all";
            attribution = "none";
        }

        if (egress is "deny-all" or "proxy-only")
        {
            if (attribution != "none")
            {
                throw new ArgumentException("deny-all and proxy-only workloads must have attribution = 'none'.", nameof(entry));
            }
        }
        else if (entry.Systemwide)
        {
            if (string.IsNullOrEmpty(connectorId))
            {
                throw new ArgumentException("Systemwide connector requires a connectorId.", nameof(entry));
            }

            if (attribution == "user")
            {
                if (string.IsNullOrEmpty(userId))
                {
                    throw new ArgumentException("Systemwide connector with attribution = 'user' requires a userId.", nameof(entry));
                }

                if (!string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("Systemwide connector entry cannot carry an agentId.", nameof(entry));
                }
            }
            else if (attribution == "none")
            {
                if (!string.IsNullOrEmpty(userId) || !string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("Shared systemwide connector with attribution = 'none' cannot carry userId or agentId.", nameof(entry));
                }
            }
            else
            {
                throw new ArgumentException("Systemwide connector cannot have attribution = 'user-agent'.", nameof(entry));
            }
        }
        else
        {
            if (attribution == "user-agent")
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("Ordinary container requires both userId and agentId.", nameof(entry));
                }
            }
            else if (attribution == "user")
            {
                // Connector probe running under requesting user's identity (A1)
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(connectorId))
                {
                    throw new ArgumentException("User-attributed connector probe requires both userId and connectorId.", nameof(entry));
                }
            }
            else
            {
                throw new ArgumentException("Normal egress container must be attributed to a user or user+agent.", nameof(entry));
            }
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
