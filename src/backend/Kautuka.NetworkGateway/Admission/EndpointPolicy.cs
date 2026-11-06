using System.Collections.Frozen;
using System.Net;

namespace Kautuka.NetworkGateway.Admission;

/// <summary>
/// Immutable exact-endpoint policy for the Gate 1 admission prototype.
/// No DNS, connector identity enrichment, or production policy publisher is implied.
/// </summary>
internal sealed class EndpointPolicy
{
    private readonly FrozenDictionary<IPAddress, SourceRegistration> sources;
    private readonly FrozenSet<EndpointRule> rules;
    private readonly FrozenDictionary<(IPAddress Address, uint Uid, uint Egress, long Namespace), LocalOutputRegistration> localOutputs;

    public EndpointPolicy(long generation, IEnumerable<SourceRegistration> sources, IEnumerable<EndpointRule> rules, IEnumerable<LocalOutputRegistration>? localOutputs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rules);
        Generation = generation;
        SourceRegistration[] registrations = sources.ToArray();
        foreach (SourceRegistration source in registrations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Subject);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Generation);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.NamespaceGeneration);
            ArgumentOutOfRangeException.ThrowIfZero(source.IngressInterfaceIndex);
            RequireAddress(source.Address);
        }

        this.sources = registrations.ToFrozenDictionary(source => source.Address);
        LocalOutputRegistration[] outputs = localOutputs?.ToArray() ?? [];
        foreach (LocalOutputRegistration output in outputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(output.Subject);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.Generation);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.NamespaceGeneration);
            ArgumentOutOfRangeException.ThrowIfZero(output.SocketUid);
            ArgumentOutOfRangeException.ThrowIfZero(output.EgressInterfaceIndex);
            RequireAddress(output.Address);
        }
        this.localOutputs = outputs.ToFrozenDictionary(output => (output.Address, output.SocketUid, output.EgressInterfaceIndex, output.NamespaceGeneration));
        ActorRegistration[] actors = [.. registrations, .. outputs];
        EndpointRule[] endpoints = rules.ToArray();
        foreach (EndpointRule rule in endpoints)
        {
            RequireAddress(rule.Destination);
            if (rule.Port == 0 || rule.Protocol is not (6 or 17) || !actors.Any(source => source.Subject == rule.Subject))
            {
                throw new ArgumentException("Rules require a registered subject and an explicit TCP/UDP endpoint.", nameof(rules));
            }
        }

        this.rules = endpoints.ToFrozenSet();
    }

    public long Generation { get; }

    public IEnumerable<long> IdentityGenerationsFor(string subject)
        => sources.Values.Cast<ActorRegistration>().Concat(localOutputs.Values).Where(source => source.Subject == subject).Select(source => source.Generation).Distinct();

    public ActorRegistration? Resolve(FlowKey flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Path switch
        {
            AdmissionPath.Forwarded => sources.GetValueOrDefault(flow.Source),
            AdmissionPath.LocalOutput when flow.SocketUid is uint uid
                => localOutputs.GetValueOrDefault((flow.Source, uid, flow.EgressInterfaceIndex, flow.NamespaceGeneration)),
            _ => null,
        };
    }

    public bool Allows(ActorRegistration source, FlowKey flow)
        => flow.ConntrackId != 0
            && source == Resolve(flow)
            && source.NamespaceGeneration == flow.NamespaceGeneration
            && (source switch
            {
                SourceRegistration forwarded => flow.Path == AdmissionPath.Forwarded
                    && forwarded.IngressInterfaceIndex == flow.IngressInterfaceIndex && flow.SocketUid is null,
                LocalOutputRegistration output => flow.Path == AdmissionPath.LocalOutput
                    && flow.IngressInterfaceIndex == 0 && output.SocketUid == flow.SocketUid
                    && output.EgressInterfaceIndex == flow.EgressInterfaceIndex,
                _ => false,
            })
            && flow.SourcePort != 0
            && flow.Source.AddressFamily == flow.Destination.AddressFamily
            && rules.Contains(new EndpointRule(source.Subject, flow.Destination, flow.DestinationPort, flow.Protocol));

    private static void RequireAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
            || address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("Policy addresses must be explicit native IPv4 or IPv6 addresses.", nameof(address));
        }
    }
}
