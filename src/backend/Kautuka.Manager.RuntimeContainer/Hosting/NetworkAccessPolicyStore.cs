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
    DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, long Attempts, string Status);
public sealed record NetworkAccessPolicyState(int SchemaVersion, long Revision, IReadOnlyList<NetworkAccessRule> Rules, IReadOnlyList<NetworkAccessRequest> Requests);
internal enum AgentScopeNetworkDecision { None, Allowed, Blocked, Conflict }

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
                Requests = [.. current.Requests.Where(request => CanAccess(actor, request.Identity))],
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
            NetworkAccessRule[] rules = [.. current.Rules.Where(item => IsCanonicalAgentIdentity(identity)
                    ? !IsSameAgentEndpoint(item.Identity, item.Destination, item.Port, identity, normalized.Destination, port)
                    : item.Id != id), rule];
            if (rules.Length > ruleLimit) throw new InvalidOperationException("The network policy rule limit has been reached.");
            NetworkAccessRequest[] relatedRequests = [.. current.Requests.Where(item => item.Id == id
                || (IsCanonicalAgentIdentity(identity)
                    && IsSameAgentEndpoint(item.Identity, item.Destination, item.Port, identity, normalized.Destination, port)))];
            NetworkAccessRequest[] updatedRequests;
            if (IsCanonicalAgentIdentity(identity) && relatedRequests.Length > 0)
            {
                long attempts = 0;
                foreach (NetworkAccessRequest existing in relatedRequests)
                    attempts = attempts > long.MaxValue - existing.Attempts ? long.MaxValue : attempts + existing.Attempts;
                NetworkAccessRequest consolidated = new(id, identity, normalized.Destination, port,
                    relatedRequests.Min(static item => item.CreatedAt), relatedRequests.Max(static item => item.LastSeenAt),
                    Math.Max(1, attempts), allowed ? "allowed" : "blocked");
                var relatedIds = relatedRequests.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
                updatedRequests = [.. current.Requests.Where(item => !relatedIds.Contains(item.Id)), consolidated];
            }
            else
            {
                updatedRequests = [.. current.Requests.Select(item => item.Id == id ? item with { Status = allowed ? "allowed" : "blocked" } : item)];
            }
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
            return SetRule(actor, request.Identity, request.Destination, request.Port, allowed, expectedRevision, allowPrivateNetwork);
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
            NetworkAccessRule[] sharedDecisions = [.. current.Rules.Where(rule => IsSharedAgentIdentity(rule.Identity)
                && IsSameAgentEndpoint(rule.Identity, rule.Destination, rule.Port, identity, normalized.Destination, port))];
            if (sharedDecisions.Any(static rule => !rule.Allowed)) return;
            if (sharedDecisions.Length > 1 && (sharedDecisions.Select(static rule => rule.AllowPrivateNetwork).Distinct().Skip(1).Any()
                || sharedDecisions.Any(static rule => !IsEffectiveAllow(rule)))) return;
            if (sharedDecisions.Length > 0 && sharedDecisions.All(IsEffectiveAllow)) return;
            // Preserve explicit Blocks and effective Allows, but an older public-only
            // Allow for a private literal cannot satisfy this connection's approval.
            NetworkAccessRule? decision = current.Rules.SingleOrDefault(item => item.Id == id);
            if (decision is not null && (!decision.Allowed || IsEffectiveAllow(decision))) return;
            NetworkAccessRequest[] relatedRequests = [.. current.Requests.Where(item => item.Id == id
                || (IsCanonicalAgentIdentity(identity)
                    && IsSameAgentEndpoint(item.Identity, item.Destination, item.Port, identity, normalized.Destination, port)))];
            long attempts = 0;
            foreach (NetworkAccessRequest existing in relatedRequests)
                attempts = attempts > long.MaxValue - existing.Attempts ? long.MaxValue : attempts + existing.Attempts;
            attempts = attempts == long.MaxValue ? long.MaxValue : attempts + 1;
            DateTimeOffset createdAt = relatedRequests.Length == 0 ? observedAt : relatedRequests.Min(static item => item.CreatedAt);
            DateTimeOffset lastSeenAt = relatedRequests.Length == 0 || observedAt > relatedRequests.Max(static item => item.LastSeenAt)
                ? observedAt : relatedRequests.Max(static item => item.LastSeenAt);
            NetworkAccessRequest request = new(id, identity, normalized.Destination, port, createdAt, lastSeenAt, attempts, "pending");
            if (relatedRequests is [{ Id: var previousId, Identity: var previousIdentity, Status: "pending" }]
                && previousId == id && previousIdentity == identity)
            {
                // Retries of an already durable canonical request only update
                // the in-memory counter. A first request or legacy consolidation
                // still goes through the durable replacement path below.
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
                // A deliberate rule for this user-agent endpoint replaces all
                // shared bootstrap defaults for that endpoint. Component-scoped
                // legacy rules still conflict with each other below and fail closed.
                return !IsSharedAgentGrant(grant) || !overrides.Any(rule => IsSharedAgentIdentity(rule.Identity)
                    && rule.Identity.UserId == grant.UserId && rule.Identity.AgentId == grant.AgentId
                    && rule.Destination == destination && rule.Port == grant.Port);
            })];
            var agentDecisions = new List<(string UserId, string AgentId, string Destination, int Port, bool Allowed, bool AllowPrivateNetwork, bool Effective)>();
            foreach (ContainerEgressGrant grant in unmatchedBootstrap.Where(IsSharedAgentGrant))
            {
                string destination = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", grant.Destination, grant.Port)).Destination;
                agentDecisions.Add((grant.UserId, grant.AgentId, destination, grant.Port, true, grant.AllowPrivateNetwork, true));
            }
            foreach (NetworkAccessRule rule in overrides.Where(rule => IsSharedAgentIdentity(rule.Identity)))
                agentDecisions.Add((rule.Identity.UserId, rule.Identity.AgentId, rule.Destination, rule.Port,
                    rule.Allowed, rule.AllowPrivateNetwork, IsEffectiveAllow(rule)));

            ContainerEgressGrant[] nonAgentGrants = [.. unmatchedBootstrap.Where(static grant => !IsSharedAgentGrant(grant)),
                .. overrides.Where(rule => !IsSharedAgentIdentity(rule.Identity) && IsEffectiveAllow(rule)).Select(rule => new ContainerEgressGrant
                {
                    ScopeType = rule.Identity.ScopeType, UserId = rule.Identity.UserId, AgentId = rule.Identity.AgentId,
                    Role = rule.Identity.Role, ComponentId = rule.Identity.ComponentId, Destination = rule.Destination, Port = rule.Port,
                    AllowPrivateNetwork = rule.AllowPrivateNetwork,
                })];

            var sharedAgentGrants = new List<ContainerEgressGrant>();
            foreach (var group in agentDecisions.GroupBy(static item => (item.UserId, item.AgentId, item.Destination, item.Port)))
            {
                var decisions = group.ToArray();
                // Old component-scoped decisions may disagree. Do not silently
                // merge a Block with an Allow or public-only with private approval.
                if (decisions.Any(static item => !item.Allowed)
                    || decisions.Select(static item => item.AllowPrivateNetwork).Distinct().Skip(1).Any()
                    || decisions.Any(static item => !item.Effective))
                    continue;

                var first = decisions[0];
                sharedAgentGrants.Add(new ContainerEgressGrant
                {
                    ScopeType = "agent",
                    UserId = first.UserId,
                    AgentId = first.AgentId,
                    Role = "agent",
                    ComponentId = first.AgentId,
                    Destination = first.Destination,
                    Port = first.Port,
                    AllowPrivateNetwork = first.AllowPrivateNetwork,
                });
            }

            return [.. nonAgentGrants, .. sharedAgentGrants];
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
            NetworkAccessRule[] decisions = [.. Load().Rules.Where(rule => IsSharedAgentIdentity(rule.Identity)
                && rule.Identity.UserId == identity.UserId && rule.Identity.AgentId == identity.AgentId
                && rule.Destination == destination && rule.Port == port)];
            if (decisions.Length == 0) return AgentScopeNetworkDecision.None;
            bool hasAllow = decisions.Any(static rule => rule.Allowed);
            bool hasBlock = decisions.Any(static rule => !rule.Allowed);
            if (hasAllow && hasBlock) return AgentScopeNetworkDecision.Conflict;
            if (hasBlock) return AgentScopeNetworkDecision.Blocked;
            if (decisions.Select(static rule => rule.AllowPrivateNetwork).Distinct().Skip(1).Any()
                || (NetworkDestinationPolicy.IsNonPublic(destination) && decisions.Any(static rule => !rule.AllowPrivateNetwork)))
                return AgentScopeNetworkDecision.Conflict;
            return AgentScopeNetworkDecision.Allowed;
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

    private static bool IsSameAgentEndpoint(NetworkAccessIdentity candidate, string candidateDestination, int candidatePort,
        NetworkAccessIdentity canonical, string destination, int port)
        => IsSharedAgentIdentity(candidate) && candidate.ScopeType == canonical.ScopeType
            && candidate.UserId == canonical.UserId && candidate.AgentId == canonical.AgentId
            && candidateDestination == destination && candidatePort == port;

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
            EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", rule.Destination, rule.Port));
            if (normalized.Destination != rule.Destination || rule.Id != Key(rule.Identity, rule.Destination, rule.Port))
                throw new InvalidDataException("Network policy contains an invalid rule identity.");
        }
        foreach (NetworkAccessRequest request in loaded.Requests)
        {
            ValidateIdentity(request.Identity);
            EgressTcpRule normalized = EnvoyEgressConfiguration.Normalize(new EgressTcpRule("192.0.2.1", request.Destination, request.Port));
            NetworkAccessRule? decision = loaded.Rules.SingleOrDefault(rule => rule.Id == request.Id);
            if (normalized.Destination != request.Destination || request.Id != Key(request.Identity, normalized.Destination, request.Port) || request.Attempts < 1
                || request.Status is not ("pending" or "allowed" or "blocked"))
                throw new InvalidDataException("Network policy contains an invalid request.");
            if (request.Status == "pending" ? decision is not null && (!decision.Allowed || IsEffectiveAllow(decision))
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
