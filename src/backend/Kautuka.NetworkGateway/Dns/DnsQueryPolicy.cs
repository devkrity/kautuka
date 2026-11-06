using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Dns;

/// <summary>Metadata supplied by a trusted, kernel-constrained listener; never read from DNS payloads.</summary>
internal sealed record DnsClientContext(IPAddress Address, ushort Port, byte Protocol, uint IngressInterfaceIndex, long NamespaceGeneration);
internal sealed record DnsNameRule(string Subject, string Name, IPAddress IPv4, IPAddress IPv6);

/// <summary>Exact-name, fixed-answer coarse-owner policy for the first resolver fixture.</summary>
internal sealed class DnsQueryPolicy
{
    private readonly FrozenDictionary<(IPAddress Address, uint Ingress, long Namespace), SourceRegistration> clients;
    private readonly FrozenDictionary<(string Subject, string Name), DnsNameRule> names;

    public DnsQueryPolicy(long generation, IEnumerable<SourceRegistration> clients, IEnumerable<DnsNameRule> names)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(names);
        Generation = generation;
        SourceRegistration[] registrations = clients.ToArray();
        foreach (SourceRegistration client in registrations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(client.Subject);
            if (client.Subject.Length > 128)
            {
                throw new ArgumentException("DNS owner identity exceeds fixture bounds.", nameof(clients));
            }
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(client.Generation);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(client.NamespaceGeneration);
            ArgumentOutOfRangeException.ThrowIfZero(client.IngressInterfaceIndex);
            RequireNativeAddress(client.Address);
        }
        this.clients = registrations.ToFrozenDictionary(client => (client.Address, client.IngressInterfaceIndex, client.NamespaceGeneration));
        DnsNameRule[] rules = names.Select(rule => rule with { Name = DnsWire.CanonicalName(rule.Name) }).ToArray();
        foreach (DnsNameRule rule in rules)
        {
            if (!registrations.Any(client => client.Subject == rule.Subject))
            {
                throw new ArgumentException("DNS rules require a registered owner.", nameof(names));
            }
            RequireNativeAddress(rule.IPv4);
            RequireNativeAddress(rule.IPv6);
            if (rule.IPv4.AddressFamily != AddressFamily.InterNetwork || rule.IPv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("The DNS fixture requires explicit IPv4 and IPv6 answer addresses.", nameof(names));
            }
        }
        this.names = rules.ToFrozenDictionary(rule => (rule.Subject, rule.Name));
    }

    public long Generation { get; }

    public SourceRegistration? Resolve(DnsClientContext context)
        => context.Port != 0 && context.Protocol is 6 or 17
            ? clients.GetValueOrDefault((context.Address, context.IngressInterfaceIndex, context.NamespaceGeneration)) : null;

    public DnsNameRule? Authorize(SourceRegistration client, DnsQuestion question)
        => clients.GetValueOrDefault((client.Address, client.IngressInterfaceIndex, client.NamespaceGeneration)) == client
            && question.Class == 1 && question.Type is 1 or 28
            ? names.GetValueOrDefault((client.Subject, question.Name)) : null;

    public static bool AllowsAnswer(DnsNameRule rule, DnsQuestion question, DnsAnswer answer)
        => question.Class == 1 && ((question.Type == 1 && answer.Address.Equals(rule.IPv4))
            || (question.Type == 28 && answer.Address.Equals(rule.IPv6)));

    private static void RequireNativeAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || address.IsIPv4MappedToIPv6 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
        {
            throw new ArgumentException("DNS fixture addresses must be explicit native addresses.", nameof(address));
        }
    }
}
