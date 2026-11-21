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
    public const string ApplianceGatewayIp = "10.247.0.4";
    public const string ApplianceWebHostIp = "10.247.0.5";
    public const string ObservabilityIp = "10.247.0.6";
    public const string ApplianceCredentialsHostIp = "10.247.0.7";
    public const string ApplianceChannelHostIp = "10.247.0.8";
    public const int TelemetryIngressPort = 15005;
    public const int ObservabilityPort = 5080;
    public const int WebHostPort = 20229;
    public const int GatewayConnectPort = 15004;
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

    public static readonly IReadOnlyList<string> RequiredApplianceComments =
    ["input_ipv6_drop", "input_drop", "ipv6_drop", "spoof_drop", "final_drop"];

    public static string? FindApplianceAddress(string? role) => role switch
    {
        "appliance-gateway" => ApplianceGatewayIp,
        "appliance-webhost" => ApplianceWebHostIp,
        "appliance-observability" => ObservabilityIp,
        _ => null,
    };

    /// <summary>
    /// Renders the idempotent, atomic workload ruleset for a specific allocated IPv4 address.
    /// Matches <c>kautuka_hook.py:render_ruleset</c> byte-for-byte.
    /// </summary>
    public static string Render(string allocatedIp, bool isPackageProxy = false, string? applianceRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);
        string normalizedIp = allocatedIp.Trim();
        if (!IPAddress.TryParse(normalizedIp, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"Invalid workload IPv4 address: '{allocatedIp}'.", nameof(allocatedIp));
        }

        if (applianceRole is not null)
        {
            ValidateApplianceIdentity(applianceRole, normalizedIp, isPackageProxy);
            return applianceRole switch
            {
                "appliance-gateway" => RenderApplianceGateway(),
                "appliance-webhost" => RenderApplianceWebHost(),
                "appliance-observability" => RenderObservability(),
                _ => throw new ArgumentException("Unknown appliance network role.", nameof(applianceRole)),
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine("table inet kautuka_workload");
        sb.AppendLine("delete table inet kautuka_workload");
        sb.AppendLine("table inet kautuka_workload {");
        sb.AppendLine("  chain input { type filter hook input priority 0; policy drop;");
        sb.AppendLine("    iifname \"lo\" accept");
        sb.AppendLine("    ct state established,related accept");
        if (isPackageProxy)
        {
            sb.AppendLine("    ip saddr 10.247.0.2 tcp dport 20101 accept");
        }
        sb.AppendLine("    counter drop comment \"input_drop\"");
        sb.AppendLine("  }");
        sb.AppendLine("  chain output { type filter hook output priority 0; policy drop;");
        sb.AppendLine("    oifname \"lo\" accept");
        if (isPackageProxy)
        {
            sb.AppendLine("    ct state established,related accept");
        }
        sb.AppendLine("    meta nfproto ipv6 counter drop comment \"ipv6_drop\"");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    oifname != \"lo\" ip saddr != {normalizedIp} counter drop comment \"spoof_drop\"");
        sb.AppendLine("    ip daddr 10.247.0.2 tcp dport 15001 accept");
        sb.AppendLine("    ip daddr 10.247.0.2 meta l4proto { udp, tcp } th dport 53 accept");
        sb.AppendLine("    ip daddr 10.247.0.0/16 counter drop comment \"wl_net_drop\"");
        sb.AppendLine("    ip daddr { 169.254.0.0/16, 168.63.129.16 } counter drop comment \"meta_drop\"");
        sb.AppendLine("    meta l4proto udp counter drop comment \"udp_drop\"");
        sb.AppendLine("    meta l4proto tcp accept");
        sb.AppendLine("    counter drop comment \"final_drop\"");
        sb.AppendLine("  }");
        sb.Append("}\n");

        return sb.ToString();
    }

    private static string RenderApplianceGateway() => $$"""
        table inet kautuka_workload
        delete table inet kautuka_workload
        table inet kautuka_workload {
          chain input { type filter hook input priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "input_ipv6_drop"
            iifname "lo" accept
            ct state established,related accept
            counter drop comment "input_drop"
          }
          chain output { type filter hook output priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "ipv6_drop"
            oifname "lo" accept
            oifname != "lo" ip saddr != {{ApplianceGatewayIp}} counter drop comment "spoof_drop"
            ip daddr {{EgressPodIp}} tcp dport {{GatewayConnectPort}} accept
            ip daddr {{EgressPodIp}} tcp dport {{TelemetryIngressPort}} accept
            ip daddr {{EgressPodIp}} meta l4proto { udp, tcp } th dport {{DnsPort}} accept
            counter drop comment "final_drop"
          }
        }

        """;

    private static string RenderApplianceWebHost() => $$"""
        table inet kautuka_workload
        delete table inet kautuka_workload
        table inet kautuka_workload {
          chain input { type filter hook input priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "input_ipv6_drop"
            iifname "lo" accept
            tcp dport {{WebHostPort}} accept
            counter drop comment "input_drop"
          }
          chain output { type filter hook output priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "ipv6_drop"
            oifname "lo" accept
            oifname != "lo" ip saddr != {{ApplianceWebHostIp}} counter drop comment "spoof_drop"
            ct state established accept
            counter drop comment "final_drop"
          }
        }

        """;
    private static string RenderObservability() => $$"""
        table inet kautuka_workload
        delete table inet kautuka_workload
        table inet kautuka_workload {
          chain input { type filter hook input priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "input_ipv6_drop"
            iifname "lo" accept
            ip saddr {{EgressPodIp}} ip daddr {{ObservabilityIp}} tcp dport {{ObservabilityPort}} accept
            counter drop comment "input_drop"
          }
          chain output { type filter hook output priority 0; policy drop;
            meta nfproto ipv6 counter drop comment "ipv6_drop"
            oifname "lo" accept
            oifname != "lo" ip saddr != {{ObservabilityIp}} counter drop comment "spoof_drop"
            ct state established accept
            counter drop comment "final_drop"
          }
        }

        """;

    public static string ComputeExpectedCanonicalRulesetHash(string allocatedIp, bool isPackageProxy = false, string? applianceRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);
        if (applianceRole is not null)
        {
            ValidateApplianceIdentity(applianceRole, allocatedIp, isPackageProxy);
            string golden = BoundaryRulesetRenderer.ResolveTemplate($"kautuka_{applianceRole.Replace('-', '_')}.nft.json.tmpl");
            return BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(golden, RequiredApplianceComments, allocatedIp);
        }
        string templateName = isPackageProxy ? "kautuka_package_proxy.nft.json.tmpl" : "kautuka_workload.nft.json.tmpl";
        string tmpl = BoundaryRulesetRenderer.ResolveTemplate(templateName);
        string goldenJson = tmpl.Replace("{{IP}}", allocatedIp.Trim(), StringComparison.Ordinal);
        return BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(goldenJson, RequiredWorkloadComments, allocatedIp.Trim());
    }

    private static void ValidateApplianceIdentity(string role, string address, bool isPackageProxy)
    {
        if (isPackageProxy || FindApplianceAddress(role) != address)
            throw new ArgumentException("Invalid appliance ruleset identity.", nameof(role));
    }
}
