using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record EgressAuthorizationSnapshot(string Generation,
    IReadOnlyDictionary<string, NetworkAccessIdentity> Sources, IReadOnlyList<EgressTcpRule> Rules,
    IReadOnlyDictionary<string, string> Allocations);
internal sealed record EgressRetiredAuthorization(EgressAuthorizationSnapshot Snapshot, long ExpiresAt);
internal sealed record EgressAuthorizationState(EgressAuthorizationSnapshot? Current,
    EgressAuthorizationSnapshot? Candidate, IReadOnlyList<EgressRetiredAuthorization> Retired);

public sealed partial class ContainerEgressRuntime
{
    private EgressAuthorizationState? authorization;
    private const int retainedAuthorizationLimit = 8;
    private const long retainedAuthorizationMilliseconds = 10000;
    public string AuthorizationSocketPath => Path.Combine(stateRoot, "egress", "authorization", "check.sock");

    internal void BeginAuthorizationTransition(EgressAuthorizationSnapshot candidate)
    {
        EgressAuthorizationState? previous = Volatile.Read(ref authorization);
        Volatile.Write(ref authorization, new(previous?.Current, candidate, previous?.Retired ?? []));
    }

    internal void CompleteAuthorizationTransition(EgressAuthorizationSnapshot accepted)
    {
        EgressAuthorizationState? previous = Volatile.Read(ref authorization);
        long now = Environment.TickCount64;
        IEnumerable<EgressRetiredAuthorization> retired = previous?.Retired ?? [];
        if (previous?.Current is { } current && current.Generation != accepted.Generation)
            retired = retired.Prepend(new(current, now + retainedAuthorizationMilliseconds));
        Volatile.Write(ref authorization, new(accepted, null,
            [.. retired.Where(item => item.ExpiresAt > now && item.Snapshot.Generation != accepted.Generation)
                .DistinctBy(item => item.Snapshot.Generation).Take(retainedAuthorizationLimit)]));
    }

    internal void RestoreAuthorizationTransition()
    {
        EgressAuthorizationState? previous = Volatile.Read(ref authorization);
        // A candidate may briefly have loaded before activation failed. Preserve
        // its route descriptor after confirmed rollback, never its permissions.
        if (previous?.Current is not { } current)
        {
            Volatile.Write(ref authorization, null);
            return;
        }
        long now = Environment.TickCount64;
        IEnumerable<EgressRetiredAuthorization> retired = previous.Retired;
        if (previous.Candidate is { } candidate && candidate.Generation != current.Generation)
            retired = retired.Prepend(new(candidate, now + retainedAuthorizationMilliseconds));
        Volatile.Write(ref authorization, new(current, null,
            [.. retired.Where(item => item.ExpiresAt > now && item.Snapshot.Generation != current.Generation)
                .DistinctBy(item => item.Snapshot.Generation).Take(retainedAuthorizationLimit)]));
    }

    // This path must never take the lifecycle gate: startup and policy activation
    // can themselves need connections passing through Envoy.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "DNS names use the compiler's canonical lowercase representation.")]
    public bool AuthorizeNetworkConnection(string generation, string source, string destination, int port, string? serverName)
    {
        if (port is < 1 or > 65535
            || !IPAddress.TryParse(source, out IPAddress? sourceIp) || sourceIp.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.TryParse(destination, out IPAddress? targetIp) || targetIp.AddressFamily != AddressFamily.InterNetwork) return false;
        EgressAuthorizationState state = Volatile.Read(ref authorization)
            ?? throw new InvalidOperationException("Network authorization has no accepted routing snapshot yet.");
        EgressAuthorizationSnapshot current = state.Current
            ?? throw new InvalidOperationException("Network authorization is waiting for its first accepted routing snapshot.");
        EgressAuthorizationSnapshot? route = generation == current.Generation ? current
            : generation == state.Candidate?.Generation ? state.Candidate
            : state.Retired.FirstOrDefault(item => item.Snapshot.Generation == generation && item.ExpiresAt > Environment.TickCount64)?.Snapshot;
        if (route is null) throw new InvalidOperationException("The network routing generation is no longer available. Retry the connection.");
        string address = sourceIp.ToString();
        if (!route.Sources.TryGetValue(address, out NetworkAccessIdentity? identity)) return false;
        string target = targetIp.ToString();
        if (NetworkDestinationPolicy.IsMetadata(target) || target == NetworkDestinationPolicy.PlatformControlAddress
            || target == NetworkDestinationPolicy.PrivateSourceAddress || IPAddress.IsLoopback(targetIp)) return false;
        string? sni = string.IsNullOrWhiteSpace(serverName) ? null : serverName.TrimEnd('.').ToLowerInvariant();
        if (sni is not null && (Uri.CheckHostName(sni) != UriHostNameType.Dns || NetworkDestinationPolicy.IsMetadata(sni))) return false;
        EgressTcpRule? grant = SelectAuthorizationRule(route, address, target, port, sni);
        if (!CompatibleAuthorizationRoute(route, current, address, target, port, sni, grant)
            || (state.Candidate is { } candidate && !CompatibleAuthorizationRoute(route, candidate, address, target, port, sni, grant)))
            throw new InvalidOperationException("Network routing or source ownership is changing. Retry after policy activation.");
        if (!ReferenceEquals(state, Volatile.Read(ref authorization)))
            throw new InvalidOperationException("Network routing changed during authorization. Retry the connection.");
        if (grant is not null) return true; // Existing cluster selection and output firewall still govern the real upstream.
        networkPolicy.ObserveDenied(identity, sni ?? target, port, DateTimeOffset.UtcNow);
        return false;
    }

    private static EgressTcpRule? SelectAuthorizationRule(EgressAuthorizationSnapshot snapshot, string source,
        string target, int port, string? sni)
        => snapshot.Rules.FirstOrDefault(rule => rule.SourceAddress == source && rule.Port == port
                && sni is not null && rule.Destination == sni)
            ?? snapshot.Rules.FirstOrDefault(rule => rule.SourceAddress == source && rule.Port == port && rule.Destination == target);

    private static bool CompatibleAuthorizationRoute(EgressAuthorizationSnapshot route, EgressAuthorizationSnapshot permitted,
        string source, string target, int port, string? sni, EgressTcpRule? selected)
        => route.Sources.TryGetValue(source, out NetworkAccessIdentity? routeIdentity)
            && permitted.Sources.TryGetValue(source, out NetworkAccessIdentity? permittedIdentity) && routeIdentity == permittedIdentity
            && route.Allocations.TryGetValue(source, out string? allocation)
            && permitted.Allocations.TryGetValue(source, out string? permittedAllocation) && allocation == permittedAllocation
            && selected == SelectAuthorizationRule(permitted, source, target, port, sni);
}
