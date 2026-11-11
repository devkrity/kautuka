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
    DateTimeOffset RegisteredUtc);

public interface IWorkloadRegistry
{
    long RegistryConflicts { get; }

    int Count { get; }

    void Register(RegistryEntry entry);

    bool Deregister(string ip, string? containerId = null);

    void Rebuild(IEnumerable<RegistryEntry> entries);

    bool TryGetByIp(string? ip, out RegistryEntry? entry);

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

    public void Register(RegistryEntry entry)
    {
        RegistryEntry normalized = ValidateAndNormalize(entry);
        lock (writeLock)
        {
            if (entriesByIp.TryGetValue(normalized.Ip, out RegistryEntry? existing)
                && !string.Equals(existing.ContainerId, normalized.ContainerId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref registryConflicts);
                logger.LogWarning(
                    "Workload IP {Ip} registration conflict: replacing container {OldContainerId} with {NewContainerId}.",
                    normalized.Ip,
                    existing.ContainerId,
                    normalized.ContainerId);
            }

            entriesByIp[normalized.Ip] = normalized;
        }
    }

    public bool Deregister(string ip, string? containerId = null)
    {
        if (!NetworkDestinationNormalizer.TryNormalizeIpv4(ip, out string normalizedIp, out _))
        {
            return false;
        }

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

            return entriesByIp.TryRemove(normalizedIp, out _);
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

        lock (writeLock)
        {
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

        if (entry.Egress is "deny-all" or "proxy-only")
        {
            if (entry.Attribution != "none")
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

            if (entry.Attribution == "user")
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
            else if (entry.Attribution == "none")
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
            // Non-systemwide normal egress container:
            if (entry.Attribution == "user-agent")
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(agentId))
                {
                    throw new ArgumentException("Ordinary container requires both userId and agentId.", nameof(entry));
                }
            }
            else if (entry.Attribution == "user")
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
            CompatProfile = NormalizeOptional(entry.CompatProfile),
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
