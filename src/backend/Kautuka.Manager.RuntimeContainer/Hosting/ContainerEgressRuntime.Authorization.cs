using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record EgressAuthorizationSnapshot(string Generation,
    IReadOnlyDictionary<string, NetworkAccessIdentity> Sources, IReadOnlyList<EgressTcpRule> Rules);

public sealed partial class ContainerEgressRuntime
{
    private EgressAuthorizationSnapshot? authorization;
    public string AuthorizationSocketPath => Path.Combine(stateRoot, "egress", "authorization", "check.sock");

    // This path must never take the lifecycle gate: startup and policy activation
    // can themselves need connections passing through Envoy.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "DNS names use the compiler's canonical lowercase representation.")]
    public bool AuthorizeNetworkConnection(string generation, string source, string destination, int port, string? serverName)
    {
        EgressAuthorizationSnapshot? snapshot = Volatile.Read(ref authorization);
        if (snapshot is null || generation != snapshot.Generation || port is < 1 or > 65535
            || !IPAddress.TryParse(source, out IPAddress? sourceIp) || sourceIp.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.TryParse(destination, out IPAddress? targetIp) || targetIp.AddressFamily != AddressFamily.InterNetwork
            || !snapshot.Sources.TryGetValue(sourceIp.ToString(), out NetworkAccessIdentity? identity)) return false;
        string target = targetIp.ToString();
        if (NetworkDestinationPolicy.IsMetadata(target) || target == NetworkDestinationPolicy.PlatformControlAddress
            || target == NetworkDestinationPolicy.PrivateSourceAddress || IPAddress.IsLoopback(targetIp)) return false;
        string? sni = string.IsNullOrWhiteSpace(serverName) ? null : serverName.TrimEnd('.').ToLowerInvariant();
        if (sni is not null && (Uri.CheckHostName(sni) != UriHostNameType.Dns || NetworkDestinationPolicy.IsMetadata(sni))) return false;
        EgressTcpRule? grant = snapshot.Rules.FirstOrDefault(rule => rule.SourceAddress == sourceIp.ToString()
            && rule.Port == port && sni is not null && rule.Destination == sni)
            ?? snapshot.Rules.FirstOrDefault(rule => rule.SourceAddress == sourceIp.ToString() && rule.Port == port && rule.Destination == target);
        if (grant is not null) return true; // Existing cluster selection and output firewall still govern the real upstream.
        if (!ReferenceEquals(snapshot, Volatile.Read(ref authorization))) return false;
        networkPolicy.ObserveDenied(identity, sni ?? target, port, DateTimeOffset.UtcNow);
        return false;
    }
}
