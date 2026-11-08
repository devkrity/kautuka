using System.Globalization;
using System.Text;

using Kautuka.Hosting.Hosting;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>IPv4 appliance policy; special-purpose ranges are not ordinary public destinations.</summary>
internal static class NetworkDestinationPolicy
{
    internal const string PrivateSourceAddress = KautukaNetworkDestinationPolicy.PrivateSourceAddress;
    internal const string PlatformControlAddress = KautukaNetworkDestinationPolicy.PlatformControlAddress;
    internal const string PublicSpecialSet = KautukaNetworkDestinationPolicy.PublicSpecialSet;
    internal const string MetadataSet = KautukaNetworkDestinationPolicy.MetadataSet;
    internal const string NonPublicSet = KautukaNetworkDestinationPolicy.NonPublicSet;

    internal static bool IsMetadata(string destination) => KautukaNetworkDestinationPolicy.IsMetadata(destination);
    internal static bool IsNonPublic(string destination) => KautukaNetworkDestinationPolicy.IsNonPublic(destination);

    internal static string RenderBoundary(string subnets, string addresses, string? coreGateway,
        string? outsideSourceAddress, IReadOnlyList<string> dnsUpstreams, string? packageAddress, string? telemetryAddress)
    {
        foreach (string resolver in dnsUpstreams.Where(value => IsMetadata(value) || value == PlatformControlAddress))
        {
            var address = System.Net.IPAddress.Parse(resolver);
            if (subnets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(subnet => System.Net.IPNetwork.Parse(subnet).Contains(address)))
                throw new InvalidOperationException("A protected DNS upstream overlaps a managed private network.");
        }
        var text = new StringBuilder();
        text.AppendLine("""
            table ip kautuka_egress {
             chain capture_tcp { type nat hook prerouting priority dstnat; policy accept;
            """);
        if (subnets.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n ip saddr {{ {subnets} }} ip daddr {{ {addresses} }} tcp dport 53 return\n");
            text.Append(CultureInfo.InvariantCulture, $" ip saddr {{ {subnets} }} meta l4proto tcp redirect to :15001\n");
        }

        text.AppendLine("""
             }
             chain forward { type filter hook forward priority filter; policy drop; }
             chain input { type filter hook input priority filter; policy drop;
              iifname "lo" accept
              ct state established,related accept
            """);
        if (subnets.Length > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n ip saddr {{ {subnets} }} tcp dport 15001 accept\n");
            text.Append(CultureInfo.InvariantCulture, $" ip saddr {{ {subnets} }} ip daddr {{ {addresses} }} udp dport 53 accept\n");
            text.Append(CultureInfo.InvariantCulture, $" ip saddr {{ {subnets} }} ip daddr {{ {addresses} }} tcp dport 53 accept\n");
        }

        text.AppendLine("""
             }
             chain output { type filter hook output priority filter; policy accept;
            """);
        // Some cloud DNS services share an address with metadata/control APIs. Only the
        // dedicated resolver UID may use an explicitly configured address, on DNS ports.
        foreach (string resolver in dnsUpstreams.Where(value => IsMetadata(value) || value == PlatformControlAddress))
        {
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65534 ip daddr {resolver} udp dport 53 accept\n");
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65534 ip daddr {resolver} tcp dport 53 accept\n");
        }
        text.Append(CultureInfo.InvariantCulture, $" ip daddr {{ {NetworkDestinationPolicy.MetadataSet}, {NetworkDestinationPolicy.PrivateSourceAddress} }} counter reject\n");
        text.Append(CultureInfo.InvariantCulture, $" ip daddr {PlatformControlAddress} counter reject\n");
        text.AppendLine("""
              ct state established,related ct direction reply accept
              ip saddr 127.0.0.1 ip daddr 127.0.0.1 udp dport 53 accept
              ip saddr 127.0.0.1 ip daddr 127.0.0.1 tcp dport 53 accept
              meta skuid 65533 ip daddr 127.0.0.1 tcp dport 9901 accept
              ip daddr 127.0.0.0/8 counter reject
              ip daddr { 0.0.0.0/8, 224.0.0.0/4, 240.0.0.0/4 } counter reject
            """);
        if (telemetryAddress is not null)
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65532 ip saddr {coreGateway} ip daddr {telemetryAddress} tcp dport 5080 counter accept\n");
        if (packageAddress is not null)
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65532 ip saddr {coreGateway} ip daddr {packageAddress} tcp dport 20101 counter accept\n");
        if (subnets.Length > 0) text.Append(CultureInfo.InvariantCulture, $" ip daddr {{ {subnets} }} counter reject\n");
        foreach (string resolver in dnsUpstreams)
        {
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65534 ip daddr {resolver} udp dport 53 accept\n");
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65534 ip daddr {resolver} tcp dport 53 accept\n");
        }
        if (outsideSourceAddress is not null)
            text.Append(CultureInfo.InvariantCulture, $" meta skuid 65532 ip saddr {NetworkDestinationPolicy.PrivateSourceAddress} ip daddr {{ {NetworkDestinationPolicy.NonPublicSet} }} counter accept\n");
        text.Append(CultureInfo.InvariantCulture, $" ip daddr {{ {PublicSpecialSet} }} accept\n");
        text.Append(CultureInfo.InvariantCulture, $" ip daddr {{ {NetworkDestinationPolicy.NonPublicSet} }} counter reject\n");
        text.AppendLine(" }");
        if (outsideSourceAddress is not null)
        {
            text.AppendLine(" chain private_source_nat { type nat hook postrouting priority srcnat; policy accept;");
            if (subnets.Length > 0) text.Append(CultureInfo.InvariantCulture, $" ip daddr {{ {subnets} }} return\n");
            text.Append(CultureInfo.InvariantCulture, $" ip saddr {NetworkDestinationPolicy.PrivateSourceAddress} snat to {outsideSourceAddress}\n");
            text.AppendLine(" }");
        }
        text.AppendLine("""
            }
            table ip6 kautuka_egress {
             chain input { type filter hook input priority filter; policy drop; }
             chain forward { type filter hook forward priority filter; policy drop; }
             chain output { type filter hook output priority filter; policy drop; }
            }
            """);
        return text.ToString().ReplaceLineEndings("\n");
    }

}
