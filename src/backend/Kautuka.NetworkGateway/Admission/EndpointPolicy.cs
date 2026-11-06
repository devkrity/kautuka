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

    public EndpointPolicy(long generation, IEnumerable<SourceRegistration> sources, IEnumerable<EndpointRule> rules)
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
            ArgumentOutOfRangeException.ThrowIfZero(source.IngressInterfaceIndex);
            RequireAddress(source.Address);
        }

        this.sources = registrations.ToFrozenDictionary(source => source.Address);
        EndpointRule[] endpoints = rules.ToArray();
        foreach (EndpointRule rule in endpoints)
        {
            RequireAddress(rule.Destination);
            if (rule.Port == 0 || rule.Protocol is not (6 or 17) || !registrations.Any(source => source.Subject == rule.Subject))
            {
                throw new ArgumentException("Rules require a registered subject and an explicit TCP/UDP endpoint.", nameof(rules));
            }
        }

        this.rules = endpoints.ToFrozenSet();
    }

    public long Generation { get; }

    public IEnumerable<long> IdentityGenerationsFor(string subject)
        => sources.Values.Where(source => source.Subject == subject).Select(source => source.Generation).Distinct();

    public SourceRegistration? Resolve(FlowKey flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return sources.GetValueOrDefault(flow.Source);
    }

    public bool Allows(SourceRegistration source, FlowKey flow)
        => flow.ConntrackId != 0
            && source.IngressInterfaceIndex == flow.IngressInterfaceIndex
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
