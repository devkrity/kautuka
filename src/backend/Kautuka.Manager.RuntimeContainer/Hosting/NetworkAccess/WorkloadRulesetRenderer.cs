using System.Globalization;
using System.Net;
using System.Text;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

/// <summary>
/// Independent renderer of the workload nftables ruleset (<c>table inet kautuka_workload</c>).
/// Produces the exact ruleset applied by <c>kautuka_hook.py</c> in the workload network namespace (MF7).
/// </summary>
public static class WorkloadRulesetRenderer
{
    public const string WorkloadTableName = "kautuka_workload";
    public const string EgressPodIp = "10.247.0.2";
    public const string PackageProxyIp = "10.247.0.3";
    public const int PackageProxyPort = 20101;
    public const int EnvoyPort = 15001;
    public const int DnsPort = 53;

    public static readonly IReadOnlyList<string> RequiredWorkloadComments =
    [
        "input_drop",
        "ipv6_drop",
        "spoof_drop",
        "wl_net_drop",
        "meta_drop",
        "udp_drop",
        "final_drop",
    ];

    /// <summary>
    /// Renders the idempotent, atomic workload ruleset for a specific allocated IPv4 address.
    /// Matches <c>kautuka_hook.py:render_ruleset</c> byte-for-byte.
    /// </summary>
    public static string Render(string allocatedIp, bool includePackageProxy = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);
        string normalizedIp = allocatedIp.Trim();
        if (!IPAddress.TryParse(normalizedIp, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid workload IPv4 address: '{allocatedIp}'.", nameof(allocatedIp));
        }

        string pkgRule = includePackageProxy
            ? "    ip daddr 10.247.0.3 tcp dport 20101 accept\n"
            : string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("table inet kautuka_workload");
        sb.AppendLine("delete table inet kautuka_workload");
        sb.AppendLine("table inet kautuka_workload {");
        sb.AppendLine("  chain input { type filter hook input priority 0; policy drop;");
        sb.AppendLine("    iifname \"lo\" accept");
        sb.AppendLine("    ct state established,related accept");
        sb.AppendLine("    counter drop comment \"input_drop\"");
        sb.AppendLine("  }");
        sb.AppendLine("  chain output { type filter hook output priority 0; policy drop;");
        sb.AppendLine("    oifname \"lo\" accept");
        sb.AppendLine("    meta nfproto ipv6 counter drop comment \"ipv6_drop\"");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    oifname != \"lo\" ip saddr != {normalizedIp} counter drop comment \"spoof_drop\"");
        sb.AppendLine("    ip daddr 10.247.0.2 tcp dport 15001 accept");
        sb.AppendLine("    ip daddr 10.247.0.2 meta l4proto { udp, tcp } th dport 53 accept");
        if (includePackageProxy)
        {
            sb.Append(pkgRule);
        }
        sb.AppendLine("    ip daddr 10.247.0.0/16 counter drop comment \"wl_net_drop\"");
        sb.AppendLine("    ip daddr { 169.254.0.0/16, 168.63.129.16 } counter drop comment \"meta_drop\"");
        sb.AppendLine("    meta l4proto udp counter drop comment \"udp_drop\"");
        sb.AppendLine("    meta l4proto tcp accept");
        sb.AppendLine("    counter drop comment \"final_drop\"");
        sb.AppendLine("  }");
        sb.Append("}\n");

        return sb.ToString();
    }
}
