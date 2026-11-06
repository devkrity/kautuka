using System.Globalization;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Probe;

/// <summary>Dual-stack DNS fixture: client conntrack labels never authorize upstream traffic.</summary>
internal sealed class ProbeDnsKernel : IAdmissionKernel
{
    private readonly uint socketUid;
    private readonly uint egress;

    public ProbeDnsKernel(string expectedNamespace, ushort queue, uint socketUid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(socketUid);
        ProbeKernel.VerifyNamespace(expectedNamespace);
        this.socketUid = socketUid;
        egress = uint.Parse(File.ReadAllText("/sys/class/net/g1/ifindex").Trim(), CultureInfo.InvariantCulture);
        string uid = socketUid.ToString(CultureInfo.InvariantCulture);
        string number = queue.ToString(CultureInfo.InvariantCulture);
        Apply($$"""
            destroy table inet ktng_dns
            table inet ktng_dns {
              set valid_output { type mark; flags timeout; }
              counter actor_denials { }
              counter query_denials { }
              counter forward_denials { }
              counter queued_output { }
              counter client_queries { }
              counter client_replies { }
              counter local_query_denials { }
              chain dns_prerouting {
                type nat hook prerouting priority dstnat; policy accept;
                iifname "g0" ip saddr 10.231.251.2 ip daddr 10.231.251.1 udp dport 53 redirect to :2053
                iifname "g0" ip6 saddr fd7a:6b74:251::2 ip6 daddr fd7a:6b74:251::1 udp dport 53 redirect to :2053
                iifname "g0" ip saddr 10.231.251.2 ip daddr 10.231.251.1 tcp dport 53 redirect to :2053
                iifname "g0" ip6 saddr fd7a:6b74:251::2 ip6 daddr fd7a:6b74:251::1 tcp dport 53 redirect to :2053
              }
              chain forward {
                type filter hook forward priority 0; policy drop;
                counter name forward_denials drop
              }
              chain ndp_output {
                oifname "g0" ip6 daddr { fd7a:6b74:251::2, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                oifname "g1" ip6 daddr { fd7a:6b74:252::2, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
              }
              chain actor {
                meta skuid {{uid}} return
                counter name actor_denials drop
              }
              chain client_reply {
                oifname "g0" ip saddr 10.231.251.1 ip daddr 10.231.251.2 ct direction reply ct original ip daddr 10.231.251.1 ct original proto-dst 53 ct label 0x1 tcp sport 2053 counter name client_replies accept
                oifname "g0" ip6 saddr fd7a:6b74:251::1 ip6 daddr fd7a:6b74:251::2 ct direction reply ct original ip6 daddr fd7a:6b74:251::1 ct original proto-dst 53 ct label 0x1 tcp sport 2053 counter name client_replies accept
                oifname "g0" ip saddr 10.231.251.1 ip daddr 10.231.251.2 ct direction reply ct original ip daddr 10.231.251.1 ct original proto-dst 53 ct label 0x1 udp sport 2053 counter name client_replies accept
                oifname "g0" ip6 saddr fd7a:6b74:251::1 ip6 daddr fd7a:6b74:251::2 ct direction reply ct original ip6 daddr fd7a:6b74:251::1 ct original proto-dst 53 ct label 0x1 udp sport 2053 counter name client_replies accept
              }
              chain upstream_boundary {
                meta nfproto != { ipv4, ipv6 } drop
                oifname "lo" counter name local_query_denials drop
                oifname != "g1" drop
                ct direction != original drop
                ip saddr != 10.231.252.1 drop
                ip daddr != 10.231.252.2 drop
                ip6 saddr != fd7a:6b74:252::1 drop
                ip6 daddr != fd7a:6b74:252::2 drop
                meta l4proto != { tcp, udp } drop
                tcp dport != 2053 drop
                udp dport != 2053 drop
              }
              chain admit {
                type filter hook output priority 0; policy drop;
                jump ndp_output
                jump actor
                ct state invalid drop
                jump client_reply
                jump upstream_boundary
                ct mark @valid_output accept
                ct mark != 0 drop
                meta mark set 0
                ct state new counter name queued_output queue num {{number}}
              }
              chain verify {
                type filter hook output priority 10; policy drop;
                jump ndp_output
                jump actor
                ct state invalid drop
                jump client_reply
                jump upstream_boundary
                ct mark @valid_output accept
                meta mark @valid_output ct mark set meta mark accept
              }
              chain input {
                type filter hook input priority 0; policy drop;
                iifname "g0" ip6 daddr { fd7a:6b74:251::1, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                iifname "g1" ip6 daddr { fd7a:6b74:252::1, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                ct state invalid drop
                iifname "g0" ip saddr 10.231.251.2 ip daddr 10.231.251.1 ct direction original ct original ip daddr 10.231.251.1 ct original proto-dst 53 tcp dport 2053 ct label set 0x1 counter name client_queries accept
                iifname "g0" ip6 saddr fd7a:6b74:251::2 ip6 daddr fd7a:6b74:251::1 ct direction original ct original ip6 daddr fd7a:6b74:251::1 ct original proto-dst 53 tcp dport 2053 ct label set 0x1 counter name client_queries accept
                iifname "g0" ip saddr 10.231.251.2 ip daddr 10.231.251.1 ct direction original ct original ip daddr 10.231.251.1 ct original proto-dst 53 udp dport 2053 ct label set 0x1 counter name client_queries accept
                iifname "g0" ip6 saddr fd7a:6b74:251::2 ip6 daddr fd7a:6b74:251::1 ct direction original ct original ip6 daddr fd7a:6b74:251::1 ct original proto-dst 53 udp dport 2053 ct label set 0x1 counter name client_queries accept
                iifname "g1" ip saddr 10.231.252.2 ip daddr 10.231.252.1 ct direction reply tcp sport 2053 ct mark @valid_output accept
                iifname "g1" ip6 saddr fd7a:6b74:252::2 ip6 daddr fd7a:6b74:252::1 ct direction reply tcp sport 2053 ct mark @valid_output accept
                iifname "g1" ip saddr 10.231.252.2 ip daddr 10.231.252.1 ct direction reply udp sport 2053 ct mark @valid_output accept
                iifname "g1" ip6 saddr fd7a:6b74:252::2 ip6 daddr fd7a:6b74:252::1 ct direction reply udp sport 2053 ct mark @valid_output accept
                counter name query_denials drop
              }
            }
            """);
    }

    public void Publish(AdmissionLease lease)
    {
        if (lease.Flow.Path != AdmissionPath.LocalOutput || lease.Flow.SocketUid != socketUid
            || lease.Flow.IngressInterfaceIndex != 0 || lease.Flow.EgressInterfaceIndex != egress
            || lease.Flow.NamespaceGeneration != 1 || lease.Flow.DestinationPort != 2053
            || lease.Flow.Destination.ToString() is not ("10.231.252.2" or "fd7a:6b74:252::2"))
        {
            throw new InvalidOperationException("DNS fixture received an incompatible upstream lease.");
        }
        long milliseconds = Math.Min(120000, (long)(lease.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds);
        if (milliseconds <= 0)
        {
            throw new IOException("Cannot publish an expired DNS upstream permission.");
        }
        Apply(FormattableString.Invariant($"add element inet ktng_dns valid_output {{ {lease.Handle} timeout {milliseconds}ms }}"));
    }

    public void Revoke(uint handle)
        => Apply(FormattableString.Invariant($"destroy element inet ktng_dns valid_output {{ {handle} }}"));

    private static void Apply(string rules) => ProbeKernel.Command("nft", ["-f", "-"], rules.ReplaceLineEndings("\n"));
}
