using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record NetworkAccessActor(string UserId, bool IsAdmin);
public sealed record NetworkAccessIdentity(string ScopeType, string UserId, string AgentId, string Role, string ComponentId);
public sealed record NetworkAccessRule(string Id, NetworkAccessIdentity Identity, string Destination, int Port, bool Allowed, DateTimeOffset UpdatedAt)
{
    public bool AllowPrivateNetwork { get; init; }
    public bool PrivateNetworkApprovalExplicit { get; init; }
}
public sealed record NetworkAccessRequest(string Id, NetworkAccessIdentity Identity, string Destination, int Port,
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, long Attempts, string Status)
{
    public string? GatewayDestination { get; init; }
}
public sealed record NetworkAccessPolicyState(int SchemaVersion, long Revision, IReadOnlyList<NetworkAccessRule> Rules, IReadOnlyList<NetworkAccessRequest> Requests);
internal enum AgentScopeNetworkDecision { None, Allowed, Blocked }

/// <summary>Durable scoped decisions. Recording a decision does not claim that Envoy has accepted it.</summary>
public sealed class NetworkAccessPolicyStore
{
    private const int requestLimit = 500;
    private const int ruleLimit = 10000;
    private readonly object gate = new();
    private readonly string path;
    private readonly Action<string> syncDirectory;
    private NetworkAccessPolicyState? state;
    private IOException? persistenceFailure;

    public NetworkAccessPolicyStore(string path) : this(path, FlushDirectory) { }

    internal NetworkAccessPolicyStore(string path, Action<string> syncDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(syncDirectory);
        this.path = Path.GetFullPath(path);
        this.syncDirectory = syncDirectory;
    }

    public NetworkAccessPolicyState Read(NetworkAccessActor actor)
    {
        ValidateActor(actor);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            return current with
            {
                Rules = [.. current.Rules.Where(rule => CanAccess(actor, rule.Identity))],
                Requests = [.. current.Requests.Where(request => CanAccess(actor, request.Identity) && (request.GatewayDestination is null || actor.IsAdmin))],
            };
        }
    }

    public long Revision { get { lock (gate) return Load().Revision; } }
    internal bool RequestCapacityReached
    {
        get { lock (gate) { NetworkAccessPolicyState current = Load(); return current.Requests.Count >= requestLimit && current.Requests.All(item => item.Status == "pending"); } }
    }

    public NetworkAccessRule SetRule(NetworkAccessActor actor, NetworkAccessIdentity identity, string destination,
        int port, bool allowed, long expectedRevision, bool allowPrivateNetwork = false)
    {
        ValidateActor(actor);
        ValidateIdentity(identity);
        identity = CanonicalDecisionIdentity(identity);
        if (!CanAccess(actor, identity)) throw new UnauthorizedAccessException("This network scope belongs to another owner or requires an administrator.");
        if (allowPrivateNetwork && !actor.IsAdmin) throw new UnauthorizedAccessException("Only an administrator may request private-network access.");
        EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", destination, port));
        if (allowed && NetworkDestinationPolicy.IsMetadata(normalized.Destination))
            throw new UnauthorizedAccessException("Cloud metadata and container credential endpoints cannot be allowed, including by administrators.");
        if (allowed && normalized.Destination == NetworkDestinationPolicy.PrivateSourceAddress)
            throw new UnauthorizedAccessException("The reserved proxy source address cannot be used as a destination.");
        if (allowed && normalized.Destination == NetworkDestinationPolicy.PlatformControlAddress)
            throw new UnauthorizedAccessException("Cloud platform control endpoints cannot be granted. Only the configured resolver may use their DNS service.");
        if (allowed && !allowPrivateNetwork && NetworkDestinationPolicy.IsNonPublic(normalized.Destination))
            throw new UnauthorizedAccessException("A LAN or special-purpose destination requires explicit private-network approval by an administrator.");
        string id = Key(identity, normalized.Destination, port);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            if (current.Revision != expectedRevision) throw new InvalidOperationException("Network policy changed. Refresh before saving the decision.");
            var rule = new NetworkAccessRule(id, identity, normalized.Destination, port, allowed, DateTimeOffset.UtcNow)
            {
                AllowPrivateNetwork = allowed && actor.IsAdmin && allowPrivateNetwork,
                PrivateNetworkApprovalExplicit = allowed && actor.IsAdmin && allowPrivateNetwork,
            };
            NetworkAccessRule[] rules = [.. current.Rules.Where(item => item.Id != id), rule];
            if (rules.Length > ruleLimit) throw new InvalidOperationException("The network policy rule limit has been reached.");
            NetworkAccessRequest[] updatedRequests = [.. current.Requests.Select(item => item.Id == id && (!allowed || item.GatewayDestination is null)
                ? item with { Status = allowed ? "allowed" : "blocked" } : item)];
            Save(current with
            {
                Revision = checked(current.Revision + 1),
                Rules = rules,
                Requests = updatedRequests,
            });
            return rule;
        }
    }

    public NetworkAccessRule DecideRequest(NetworkAccessActor actor, string requestId, bool allowed, long expectedRevision, bool allowPrivateNetwork = false)
    {
        ValidateActor(actor);
        lock (gate)
        {
            NetworkAccessRequest request = Load().Requests.SingleOrDefault(item => item.Id == requestId && CanAccess(actor, item.Identity))
                ?? throw new KeyNotFoundException("The network access request no longer exists.");
            if (request.GatewayDestination is not null)
                return DecideRemoteRequest(actor, request, allowed, expectedRevision, allowPrivateNetwork);
            return SetRule(actor, request.Identity, request.Destination, request.Port, allowed, expectedRevision, allowPrivateNetwork);
        }
    }

    private NetworkAccessRule DecideRemoteRequest(NetworkAccessActor actor, NetworkAccessRequest request, bool allowed, long expectedRevision, bool allowPrivateNetwork)
    {
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Combined Gateway approval requires an administrator.");
        NetworkAccessPolicyState current = Load();
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Network policy changed. Refresh before saving the decision.");
        var gateway = new NetworkAccessIdentity("core", "system", "gateway", "core", "gateway");
        string outer = request.GatewayDestination!;
        string outerId = Key(gateway, outer, request.Port);
        NetworkAccessRule? existingOuter = current.Rules.SingleOrDefault(rule => rule.Id == outerId);
        if (allowed && existingOuter is { Allowed: false }) throw new InvalidOperationException("Gateway is explicitly blocked from this destination.");
        if (allowed && existingOuter is { Allowed: true, AllowPrivateNetwork: true } && !allowPrivateNetwork)
            throw new InvalidOperationException("Gateway already has local-network access. Select local-network access or change the existing Gateway rule first.");
        if (allowed && !allowPrivateNetwork && (NetworkDestinationPolicy.IsNonPublic(request.Destination) || NetworkDestinationPolicy.IsNonPublic(outer)))
            throw new UnauthorizedAccessException("A LAN or special-purpose destination requires explicit private-network approval by an administrator.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var decision = new NetworkAccessRule(request.Id, request.Identity, request.Destination, request.Port, allowed, now)
        {
            AllowPrivateNetwork = allowed && allowPrivateNetwork,
            PrivateNetworkApprovalExplicit = allowed && allowPrivateNetwork,
        };
        var rules = current.Rules.Where(rule => rule.Id != request.Id).ToList();
        rules.Add(decision);
        if (allowed)
        {
            rules.RemoveAll(rule => rule.Id == outerId);
            rules.Add(new NetworkAccessRule(outerId, gateway, outer, request.Port, true, now)
            {
                AllowPrivateNetwork = allowPrivateNetwork,
                PrivateNetworkApprovalExplicit = allowPrivateNetwork,
            });
        }
        if (rules.Count > ruleLimit) throw new InvalidOperationException("The network policy rule limit has been reached.");
        Save(current with
        {
            Revision = checked(current.Revision + 1),
            Rules = rules,
            Requests = [.. current.Requests.Select(item => item.Id == request.Id ? item with { Status = allowed ? "allowed" : "blocked" }
                : allowed && item.Id == outerId ? item with { Status = "allowed" } : item)]
        });
        return decision;
    }

    internal void ObserveRemoteDenied(NetworkAccessIdentity identity, string destination, int port, string gatewayDestination, DateTimeOffset observedAt)
    {
        ValidateIdentity(identity);
        identity = CanonicalDecisionIdentity(identity);
        string host = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", destination, port)).Destination;
        string outer = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", gatewayDestination, port)).Destination;
        if (NetworkDestinationPolicy.IsMetadata(host) || NetworkDestinationPolicy.IsMetadata(outer)
            || host == NetworkDestinationPolicy.PlatformControlAddress || outer == NetworkDestinationPolicy.PlatformControlAddress
            || host == NetworkDestinationPolicy.PrivateSourceAddress || outer == NetworkDestinationPolicy.PrivateSourceAddress)
            throw new UnauthorizedAccessException("Protected destinations cannot be approved.");
        string id = Key(identity, host, port);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            NetworkAccessRule? agentRule = current.Rules.SingleOrDefault(rule => rule.Id == id);
            var gateway = new NetworkAccessIdentity("core", "system", "gateway", "core", "gateway");
            NetworkAccessRule? gatewayRule = current.Rules.SingleOrDefault(rule => rule.Id == Key(gateway, outer, port));
            if (agentRule is { Allowed: false } || gatewayRule is { Allowed: false }) return;
            NetworkAccessRequest? previous = current.Requests.SingleOrDefault(item => item.Id == id);
            var request = new NetworkAccessRequest(id, identity, host, port, previous?.CreatedAt ?? observedAt, observedAt,
                previous is null ? 1 : previous.Attempts == long.MaxValue ? long.MaxValue : previous.Attempts + 1, "pending")
            { GatewayDestination = outer };
            if (previous is { Status: "pending" } && previous.GatewayDestination == outer)
            {
                state = current with { Requests = [.. current.Requests.Select(item => item.Id == id ? request : item)] };
                return;
            }
            string? retire = null;
            if (previous is null && current.Requests.Count >= requestLimit)
                retire = current.Requests.Where(item => item.Status != "pending").MinBy(item => item.LastSeenAt)?.Id
                    ?? throw new InvalidOperationException("The pending network approval capacity has been reached.");
            Save(current with
            {
                Revision = previous is null ? current.Revision : checked(current.Revision + 1),
                Requests = [.. current.Requests.Where(item => item.Id != id && item.Id != retire), request]
            });
        }
    }

    /// <summary>The denied connection has already closed. This records an asynchronous request, never a continuation.</summary>
    public void ObserveDenied(NetworkAccessIdentity identity, string destination, int port, DateTimeOffset observedAt)
    {
        ValidateIdentity(identity);
        identity = CanonicalDecisionIdentity(identity);
        EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", destination, port));
        string id = Key(identity, normalized.Destination, port);
        lock (gate)
        {
            NetworkAccessPolicyState current = Load();
            // Preserve explicit Blocks and effective Allows, but an older public-only
            // Allow for a private literal cannot satisfy this connection's approval.
            NetworkAccessRule? decision = current.Rules.SingleOrDefault(item => item.Id == id);
            if (decision is not null && (!decision.Allowed || IsEffectiveAllow(decision))) return;
            NetworkAccessRequest[] relatedRequests = [.. current.Requests.Where(item => item.Id == id)];
            long attempts = 0;
            foreach (NetworkAccessRequest existing in relatedRequests)
                attempts = attempts > long.MaxValue - existing.Attempts ? long.MaxValue : attempts + existing.Attempts;
            attempts = attempts == long.MaxValue ? long.MaxValue : attempts + 1;
            DateTimeOffset createdAt = relatedRequests.Length == 0 ? observedAt : relatedRequests.Min(static item => item.CreatedAt);
            DateTimeOffset lastSeenAt = relatedRequests.Length == 0 || observedAt > relatedRequests.Max(static item => item.LastSeenAt)
                ? observedAt : relatedRequests.Max(static item => item.LastSeenAt);
            NetworkAccessRequest request = new(id, identity, normalized.Destination, port, createdAt, lastSeenAt, attempts, "pending")
            { GatewayDestination = relatedRequests.SingleOrDefault()?.GatewayDestination };
            if (relatedRequests is [{ Id: var previousId, Identity: var previousIdentity, Status: "pending" }]
                && previousId == id && previousIdentity == identity)
            {
                // Retries of an already durable canonical request only update
                // the in-memory counter. A first request remains durable.
                state = current with { Requests = [.. current.Requests.Select(item => item.Id == id ? request : item)] };
                return;
            }
            string? retire = null;
            if (relatedRequests.Length == 0 && current.Requests.Count >= requestLimit)
                retire = current.Requests.Where(item => item.Status != "pending").MinBy(item => item.LastSeenAt)?.Id
                    ?? throw new InvalidOperationException("The pending network approval capacity has been reached.");
            var relatedIds = relatedRequests.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
            Save(current with { Requests = [.. current.Requests.Where(item => !relatedIds.Contains(item.Id) && item.Id != retire), request] });
        }
    }

    internal IReadOnlyList<ContainerEgressGrant> EffectiveGrants(IReadOnlyList<ContainerEgressGrant> bootstrap)
    {
        lock (gate)
        {
            IReadOnlyList<NetworkAccessRule> overrides = Load().Rules;
            var keys = overrides.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            ContainerEgressGrant[] unmatchedBootstrap = [.. bootstrap.Where(grant =>
            {
                string destination = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", grant.Destination, grant.Port)).Destination;
                if (keys.Contains(Key(Identity(grant), destination, grant.Port))) return false;
                // A deliberate rule replaces shared bootstrap defaults for this agent endpoint.
                return !IsSharedAgentGrant(grant) || !overrides.Any(rule => IsSharedAgentIdentity(rule.Identity)
                    && rule.Identity.UserId == grant.UserId && rule.Identity.AgentId == grant.AgentId
                    && rule.Destination == destination && rule.Port == grant.Port);
            })];
            // Canonical agent decisions replace all bootstrap grants for that endpoint.
            ContainerEgressGrant[] explicitGrants = [.. overrides.Where(IsEffectiveAllow).Select(rule => new ContainerEgressGrant
            {
                ScopeType = rule.Identity.ScopeType, UserId = rule.Identity.UserId, AgentId = rule.Identity.AgentId,
                Role = rule.Identity.Role, ComponentId = rule.Identity.ComponentId, Destination = rule.Destination, Port = rule.Port,
                AllowPrivateNetwork = rule.AllowPrivateNetwork,
            })];
            ContainerEgressGrant[] agentDefaults = [.. unmatchedBootstrap.Where(IsSharedAgentGrant)
                .GroupBy(grant => (grant.UserId, grant.AgentId, Destination: EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", grant.Destination, grant.Port)).Destination, grant.Port))
                .Where(group => group.Select(grant => grant.AllowPrivateNetwork).Distinct().Count() == 1)
                .Select(group => new ContainerEgressGrant { ScopeType = "agent", UserId = group.Key.UserId, AgentId = group.Key.AgentId,
                    Role = "agent", ComponentId = group.Key.AgentId, Destination = group.Key.Destination, Port = group.Key.Port,
                    AllowPrivateNetwork = group.First().AllowPrivateNetwork })];
            return [.. unmatchedBootstrap.Where(grant => !IsSharedAgentGrant(grant)), .. agentDefaults, .. explicitGrants];
        }
    }

    internal AgentScopeNetworkDecision GetAgentScopeDecision(NetworkAccessActor actor, NetworkAccessIdentity identity,
        string destination, int port)
    {
        ValidateActor(actor);
        ValidateIdentity(identity);
        if (!IsCanonicalAgentIdentity(identity) || !CanAccess(actor, identity))
            throw new UnauthorizedAccessException("An agent network decision requires the caller's canonical user-agent identity.");

        lock (gate)
        {
            NetworkAccessRule? decision = Load().Rules.SingleOrDefault(rule => rule.Id == Key(identity, destination, port));
            if (decision is null) return AgentScopeNetworkDecision.None;
            return IsEffectiveAllow(decision) ? AgentScopeNetworkDecision.Allowed : AgentScopeNetworkDecision.Blocked;
        }
    }

    private static bool IsSharedAgentGrant(ContainerEgressGrant grant)
        => grant.ScopeType == "agent" && (grant.Role is "agent" or "connector" or "remote-connector");

    internal static bool IsSharedAgentIdentity(NetworkAccessIdentity identity)
        => identity.ScopeType == "agent" && (identity.Role is "agent" or "connector" or "remote-connector");

    private static bool IsCanonicalAgentIdentity(NetworkAccessIdentity identity)
        => identity.ScopeType == "agent" && identity.Role == "agent" && identity.ComponentId == identity.AgentId;

    private static NetworkAccessIdentity CanonicalDecisionIdentity(NetworkAccessIdentity identity)
        => IsSharedAgentIdentity(identity)
            ? new NetworkAccessIdentity("agent", identity.UserId, identity.AgentId, "agent", identity.AgentId)
            : identity;

    internal static NetworkAccessIdentity Identity(ContainerEgressGrant grant)
        => new(grant.ScopeType, grant.UserId, grant.AgentId, grant.Role, grant.ComponentId);

    public static bool IsEffectiveAllow(NetworkAccessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Allowed && !NetworkDestinationPolicy.IsMetadata(rule.Destination)
            && rule.Destination != NetworkDestinationPolicy.PlatformControlAddress
            && rule.Destination != NetworkDestinationPolicy.PrivateSourceAddress
            && (!NetworkDestinationPolicy.IsNonPublic(rule.Destination)
                || (rule.AllowPrivateNetwork && rule.PrivateNetworkApprovalExplicit));
    }

    private NetworkAccessPolicyState Load()
    {
        if (persistenceFailure is not null) throw new IOException("Network policy persistence requires recovery after an unconfirmed replacement.", persistenceFailure);
        if (state is not null) return state;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return state = new(1, 0, [], []); }
        catch (DirectoryNotFoundException) { return state = new(1, 0, [], []); }
        NetworkAccessPolicyState loaded = JsonSerializer.Deserialize(bytes, NetworkAccessPolicyJsonContext.Default.NetworkAccessPolicyState)
            ?? throw new InvalidDataException("Network policy state is empty.");
        if (loaded.SchemaVersion != 1 || loaded.Revision < 0 || loaded.Rules is null || loaded.Requests is null
            || loaded.Rules.Count > ruleLimit || loaded.Requests.Count > requestLimit)
            throw new InvalidDataException("Unsupported network policy state.");
        foreach (NetworkAccessRule rule in loaded.Rules)
        {
            ValidateIdentity(rule.Identity);
            if (IsSharedAgentIdentity(rule.Identity) && !IsCanonicalAgentIdentity(rule.Identity))
                throw new InvalidDataException("Unreleased component-scoped network policy is unsupported; reset the development policy file.");
            EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", rule.Destination, rule.Port));
            if (normalized.Destination != rule.Destination || rule.Id != Key(rule.Identity, rule.Destination, rule.Port))
                throw new InvalidDataException("Network policy contains an invalid rule identity.");
        }
        foreach (NetworkAccessRequest request in loaded.Requests)
        {
            ValidateIdentity(request.Identity);
            if (IsSharedAgentIdentity(request.Identity) && !IsCanonicalAgentIdentity(request.Identity))
                throw new InvalidDataException("Unreleased component-scoped network policy is unsupported; reset the development policy file.");
            EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", request.Destination, request.Port));
            if (request.GatewayDestination is { } outer && (EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", outer, request.Port)).Destination != outer
                || NetworkDestinationPolicy.IsMetadata(outer) || outer == NetworkDestinationPolicy.PlatformControlAddress || outer == NetworkDestinationPolicy.PrivateSourceAddress))
                throw new InvalidDataException("Network policy contains an invalid Gateway destination.");
            NetworkAccessRule? decision = loaded.Rules.SingleOrDefault(rule => rule.Id == request.Id);
            if (normalized.Destination != request.Destination || request.Id != Key(request.Identity, normalized.Destination, request.Port) || request.Attempts < 1
                || request.Status is not ("pending" or "allowed" or "blocked"))
                throw new InvalidDataException("Network policy contains an invalid request.");
            if (request.Status == "pending" ? decision is not null && (!decision.Allowed || (request.GatewayDestination is null && IsEffectiveAllow(decision)))
                : decision is null || decision.Allowed != (request.Status == "allowed"))
                throw new InvalidDataException("Network policy request and decision disagree.");
        }
        if (loaded.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Rules.Count
            || loaded.Requests.Select(request => request.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Requests.Count)
            throw new InvalidDataException("Network policy contains duplicate identities.");
        return state = loaded with { Rules = [.. loaded.Rules.Select(rule => rule.PrivateNetworkApprovalExplicit ? rule : rule with { AllowPrivateNetwork = false })] };
    }

    private void Save(NetworkAccessPolicyState next)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var created = new List<string>();
        for (string? parent = directory; parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
            created.Add(parent);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(directory, ".network-policy-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool replaced = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(file, next, NetworkAccessPolicyJsonContext.Default.NetworkAccessPolicyState);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            replaced = true;
            syncDirectory(directory);
            foreach (string parent in created)
            {
                string? ancestor = Path.GetDirectoryName(parent);
                if (ancestor is not null) syncDirectory(ancestor);
            }
            state = next;
        }
        catch (Exception ex)
        {
            if (replaced) persistenceFailure = new IOException("The replacement policy could not be confirmed durable.", ex);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        int handle = open(directory, 0x10000 | 0x80000); // O_RDONLY | O_DIRECTORY | O_CLOEXEC
        if (handle < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open network policy directory for durability.");
        try
        {
            if (fsync(handle) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not persist network policy directory.");
        }
        finally { _ = close(handle); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Called only on Linux; libc open requires an explicitly marshaled UTF-8 pathname.")]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int handle);

    internal static bool CanAccess(NetworkAccessActor actor, NetworkAccessIdentity identity)
        => actor.IsAdmin || (identity.ScopeType != "core" && identity.UserId != "system"
            && string.Equals(identity.UserId, actor.UserId, StringComparison.Ordinal));

    private static void ValidateActor(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidatePart(actor.UserId);
    }

    internal static void ValidateIdentity(NetworkAccessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.ScopeType is not ("agent" or "management" or "core") || identity.Role is not ("connector" or "agent" or "core" or "package" or "materializer" or "remote-connector"))
            throw new ArgumentException("Unknown network scope or component role.");
        ValidatePart(identity.UserId); ValidatePart(identity.AgentId); ValidatePart(identity.ComponentId);
    }

    private static void ValidatePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new ArgumentException("Network identities require bounded, nonempty values without control characters.");
    }

    private static string Key(NetworkAccessIdentity identity, string destination, int port)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            identity.ScopeType, identity.UserId, identity.AgentId, identity.Role, identity.ComponentId, destination,
            port.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NetworkAccessPolicyState))]
internal sealed partial class NetworkAccessPolicyJsonContext : JsonSerializerContext
{
}
