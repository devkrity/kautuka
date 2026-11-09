using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record RemoteConnectorPermission(NetworkAccessIdentity Identity, string Destination, int Port, bool AllowPrivateNetwork);
public sealed record RemoteConnectorNetworkDecision(bool Enabled, string Decision, string Message, string RouteBinding = "", bool AllowPrivateNetwork = false);

public sealed partial class ContainerEgressRuntime
{
    private static readonly NetworkAccessIdentity gatewayNetworkIdentity = new("core", "system", "gateway", "core", "gateway");

    internal static RemoteConnectorPermission[] RemotePermissions(IReadOnlyList<ContainerEgressGrant> grants)
        => [.. grants.Where(IsRemoteConnectorPermissionGrant)
            .Select(static grant => new RemoteConnectorPermission(NetworkAccessPolicyStore.Identity(grant),
                EnvoyEgressConfiguration.Normalize(new("192.0.2.1", grant.Destination, grant.Port)).Destination,
                grant.Port, grant.AllowPrivateNetwork))
            .Distinct().OrderBy(static item => item.Identity.ScopeType, StringComparer.Ordinal)
            .ThenBy(static item => item.Identity.UserId, StringComparer.Ordinal).ThenBy(static item => item.Identity.AgentId, StringComparer.Ordinal)
            .ThenBy(static item => item.Identity.ComponentId, StringComparer.Ordinal).ThenBy(static item => item.Destination, StringComparer.Ordinal)
            .ThenBy(static item => item.Port).ThenBy(static item => item.AllowPrivateNetwork)];

    private static bool IsRemoteConnectorPermissionGrant(ContainerEgressGrant grant)
        => grant.Role == "remote-connector"
            || (grant.Role == "agent" && grant.ScopeType == "agent" && grant.ComponentId == grant.AgentId);

    // Gateway supplies this subject from its authenticated operation context. This
    // does not turn a shared Gateway socket into a per-connector packet identity.
    public RemoteConnectorNetworkDecision CheckRemoteConnectorNetwork(NetworkAccessActor actor, NetworkAccessIdentity identity,
        string scheme, string hostname, int port, string? pinnedIpv4 = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        NetworkAccessPolicyStore.ValidateIdentity(identity);
        bool agentIdentity = identity.ScopeType == "agent" && identity.Role == "agent" && identity.ComponentId == identity.AgentId;
        bool managementIdentity = identity.ScopeType == "management" && identity.AgentId == "management"
            && identity.Role == "remote-connector" && identity.ComponentId.StartsWith(actor.UserId + "::", StringComparison.Ordinal);
        if ((!agentIdentity && !managementIdentity)
            || identity.UserId != actor.UserId || identity.UserId == "system")
            throw new UnauthorizedAccessException("A remote connector must use the authenticated caller's own scoped identity.");
        if (scheme is not ("http" or "https")) throw new ArgumentException("Remote connectors require HTTP or HTTPS.");
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(hostname) || hostname.Length > 253 || hostname.Any(char.IsControl))
            throw new ArgumentException("A bounded endpoint hostname and valid port are required.");
        // Preserve legacy SSRF/address-family validation in Gateway when NAC is off.
        if (!Enabled) return new(false, "allowed", "Network access control is disabled.");
        string host = EnvoyEgressConfiguration.Normalize(new("192.0.2.1", hostname, port)).Destination;
        bool literal = IPAddress.TryParse(host, out IPAddress? hostIp);
        IPAddress? pin = null;
        if (!string.IsNullOrEmpty(pinnedIpv4) && (!IPAddress.TryParse(pinnedIpv4, out pin) || pin.AddressFamily != AddressFamily.InterNetwork))
            throw new ArgumentException("A transport pin must be an IPv4 address.");
        if (literal && pin is not null && !hostIp!.Equals(pin)) throw new ArgumentException("A literal endpoint cannot use a different transport address.");
        if (ProtectedRemoteAddress(host) || (pin is not null && ProtectedRemoteAddress(pin.ToString())))
            return new(Enabled, "blocked", "Cloud metadata and platform control destinations cannot be approved.");
        if (scheme == "http" && !literal && pin is null)
            return new(true, "pin_required", "An HTTP hostname requires a transport-pinned IPv4 address before authorization.");

        EgressAuthorizationState? state = Volatile.Read(ref authorization);
        if (!Volatile.Read(ref ready) || recoveryRequiresProxyStop || state?.Current is not { } current
            || !RepresentsDesiredRemotePolicy(state, networkPolicy.Revision))
            return RemoteTransition();
        RemoteConnectorPermission? permission = SelectRemotePermission(current, identity, host, port);
        (string? source, EgressTcpRule? route) = SelectRemoteOuterRoute(current, host, port, scheme, pin);
        if (state.Candidate is { } candidate
            && (permission != SelectRemotePermission(candidate, identity, host, port)
                || (source, route) != SelectRemoteOuterRoute(candidate, host, port, scheme, pin)
                || (source is not null && (!candidate.Allocations.TryGetValue(source, out string? nextAllocation)
                    || !current.Allocations.TryGetValue(source, out string? allocation) || allocation != nextAllocation))))
            return RemoteTransition();
        if (!ReferenceEquals(state, Volatile.Read(ref authorization)) || !RepresentsDesiredRemotePolicy(state, networkPolicy.Revision)) return RemoteTransition();
        AgentScopeNetworkDecision scopeDecision = agentIdentity
            ? networkPolicy.GetAgentScopeDecision(actor, identity, host, port)
            : AgentScopeNetworkDecision.None;
        if (scopeDecision == AgentScopeNetworkDecision.Blocked)
            return new(true, "blocked", "This agent is blocked from reaching the requested destination.");
        if (source is null || !current.Allocations.TryGetValue(source, out string? gatewayAllocation)) return RemoteTransition();
        string outerDestination = scheme == "http" && !literal ? pin!.ToString() : host;
        NetworkAccessRule? outerDecision = networkPolicy.Read(new("system", true)).Rules.SingleOrDefault(rule =>
            rule.Identity == gatewayNetworkIdentity && rule.Destination == outerDestination && rule.Port == port);
        if (outerDecision is { Allowed: false })
            return new(true, "blocked", "Gateway is explicitly blocked from reaching the requested destination.");
        if (permission is null)
        {
            if (scopeDecision == AgentScopeNetworkDecision.Allowed)
                return RemoteTransition();
            NetworkAccessRule? decision = networkPolicy.Read(actor).Rules.SingleOrDefault(rule => rule.Identity == identity && rule.Destination == host && rule.Port == port);
            if (decision is { Allowed: false }) return new(true, "blocked", "This remote connector is blocked by network policy.");
            networkPolicy.ObserveRemoteDenied(identity, host, port, outerDestination, DateTimeOffset.UtcNow);
            return new(true, "missing_permission", "An administrator must approve network access for this agent and the shared Gateway route, then retry.");
        }
        if (pin is not null && NetworkDestinationPolicy.IsNonPublic(pin.ToString()) && !permission.AllowPrivateNetwork)
        {
            networkPolicy.ObserveRemoteDenied(identity, host, port, outerDestination, DateTimeOffset.UtcNow);
            return new(true, "private_route_conflict", "The selected destination is on a local or special-purpose network. An administrator must explicitly approve local-network access for this agent and Gateway.");
        }
        if (route is null)
        {
            // Recheck the accepted generation immediately before creating an
            // administrator request. A transitioning or detached Gateway source
            // must not manufacture approval work.
            if (!ReferenceEquals(state, Volatile.Read(ref authorization)) || !RepresentsDesiredRemotePolicy(state, networkPolicy.Revision)) return RemoteTransition();
            networkPolicy.ObserveRemoteDenied(identity, host, port, outerDestination, DateTimeOffset.UtcNow);
            return new(true, "outer_route_required", $"An administrator must approve the shared Gateway route to {outerDestination}:{port}, then retry.");
        }
        if (permission.AllowPrivateNetwork != route.AllowPrivateNetwork)
        {
            networkPolicy.ObserveRemoteDenied(identity, host, port, outerDestination, DateTimeOffset.UtcNow);
            return new(true, "private_route_conflict", "The agent's local-network permission differs from Gateway's shared route. An administrator must explicitly align both permissions; an agent approval does not grant shared Gateway LAN access.");
        }
        if (!ReferenceEquals(state, Volatile.Read(ref authorization)) || !RepresentsDesiredRemotePolicy(state, networkPolicy.Revision)) return RemoteTransition();
        string binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            identity.ScopeType, identity.UserId, identity.AgentId, identity.ComponentId, host, port.ToString(CultureInfo.InvariantCulture),
            source, gatewayAllocation, route.Destination, route.AllowPrivateNetwork ? "private" : "public"))));
        return new(true, "allowed", string.Empty, binding, permission.AllowPrivateNetwork);
    }

    private static bool RepresentsDesiredRemotePolicy(EgressAuthorizationState state, long revision)
        => state.Current?.PolicyRevision == revision || state.Candidate?.PolicyRevision == revision;

    private static bool ProtectedRemoteAddress(string value)
        => NetworkDestinationPolicy.IsMetadata(value) || value == NetworkDestinationPolicy.PlatformControlAddress
            || value == NetworkDestinationPolicy.PrivateSourceAddress || (IPAddress.TryParse(value, out IPAddress? ip) && IPAddress.IsLoopback(ip));

    private static RemoteConnectorNetworkDecision RemoteTransition()
        => new(true, "transition_unavailable", "Network policy is unavailable or changing. Retry after policy activation; no new approval is required.");

    private static RemoteConnectorPermission? SelectRemotePermission(EgressAuthorizationSnapshot snapshot, NetworkAccessIdentity identity, string host, int port)
        => snapshot.RemotePermissions.SingleOrDefault(permission => permission.Identity == identity && permission.Destination == host && permission.Port == port);

    private static (string? Source, EgressTcpRule? Rule) SelectRemoteOuterRoute(EgressAuthorizationSnapshot snapshot, string host, int port, string scheme, IPAddress? pin)
    {
        string? source = snapshot.Sources.SingleOrDefault(static pair => pair.Value == gatewayNetworkIdentity).Key;
        if (source is null) return (null, null);
        string destination = scheme == "http" && !IPAddress.TryParse(host, out _) ? pin!.ToString() : host;
        return (source, snapshot.Rules.SingleOrDefault(rule => rule.SourceAddress == source && rule.Destination == destination && rule.Port == port));
    }
}
