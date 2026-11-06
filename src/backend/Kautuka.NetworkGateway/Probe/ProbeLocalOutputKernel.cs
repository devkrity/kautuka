using System.Globalization;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Probe;

/// <summary>Fixture-only upstream transport for a trusted-infrastructure namespace.</summary>
internal sealed class ProbeLocalOutputKernel : IAdmissionKernel
{
    private readonly uint socketUid;
    private readonly uint egress;

    public ProbeLocalOutputKernel(string expectedNamespace, ushort queue, uint socketUid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(socketUid);
        ProbeKernel.VerifyNamespace(expectedNamespace);
        this.socketUid = socketUid;
        egress = uint.Parse(File.ReadAllText("/sys/class/net/g1/ifindex").Trim(), CultureInfo.InvariantCulture);
        string uid = socketUid.ToString(CultureInfo.InvariantCulture);
        string number = queue.ToString(CultureInfo.InvariantCulture);
        Apply($$"""
            destroy table inet ktng_output
            table inet ktng_output {
              set valid_output { type mark; flags timeout; }
              counter actor_denials { }
              counter queued_output { }
              chain forward { type filter hook forward priority 0; policy drop; }
              chain actor {
                meta skuid {{uid}} return
                counter name actor_denials drop
              }
              chain boundary {
                meta nfproto != { ipv4, ipv6 } drop
                oifname != "g1" drop
                jump actor
                ct direction != original drop
                ip saddr != 10.231.252.1 drop
                ip daddr != 10.231.252.2 drop
                ip6 saddr != fd7a:6b74:252::1 drop
                ip6 daddr != fd7a:6b74:252::2 drop
                meta l4proto != { tcp, udp } drop
                tcp dport != 20199 drop
                udp dport != 20199 drop
              }
              chain admit {
                type filter hook output priority 0; policy drop;
                oifname "g1" ip6 daddr { fd7a:6b74:252::2, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                jump boundary
                ct state invalid drop
                ct mark @valid_output accept
                ct mark != 0 drop
                meta mark set 0
                ct state new counter name queued_output queue num {{number}}
              }
              chain verify {
                type filter hook output priority 10; policy drop;
                oifname "g1" ip6 daddr { fd7a:6b74:252::2, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                jump boundary
                ct mark @valid_output accept
                meta mark @valid_output ct mark set meta mark accept
              }
              chain input {
                type filter hook input priority 0; policy drop;
                iifname "g1" ip6 daddr { fd7a:6b74:252::1, ff02::/16 } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
                iifname != "g1" drop
                ct direction != reply drop
                ct state invalid drop
                ip saddr != 10.231.252.2 drop
                ip daddr != 10.231.252.1 drop
                ip6 saddr != fd7a:6b74:252::2 drop
                ip6 daddr != fd7a:6b74:252::1 drop
                meta l4proto != { tcp, udp } drop
                tcp sport != 20199 drop
                udp sport != 20199 drop
                ct mark @valid_output accept
              }
            }
            """);
    }

    public void Publish(AdmissionLease lease)
    {
        if (lease.Flow.Path != AdmissionPath.LocalOutput || lease.Flow.SocketUid != socketUid
            || lease.Flow.IngressInterfaceIndex != 0 || lease.Flow.EgressInterfaceIndex != egress
            || lease.Flow.NamespaceGeneration != 1)
        {
            throw new InvalidOperationException("Local-output fixture received an incompatible lease identity.");
        }
        long milliseconds = Math.Min(120000, (long)(lease.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds);
        if (milliseconds <= 0)
        {
            throw new IOException("Cannot publish an expired local-output permission.");
        }
        Apply(FormattableString.Invariant($"add element inet ktng_output valid_output {{ {lease.Handle} timeout {milliseconds}ms }}"));
    }

    public void Revoke(uint handle)
        => Apply(FormattableString.Invariant($"destroy element inet ktng_output valid_output {{ {handle} }}"));

    private static void Apply(string rules) => ProbeKernel.Command("nft", ["-f", "-"], rules.ReplaceLineEndings("\n"));
}
