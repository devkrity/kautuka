using System.Text;
using System.Globalization;

using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

/// <summary>
/// Renders boundary nftables ruleset, static Envoy configuration, and CoreDNS configuration,
/// and computes canonical normalized hashes of nftables rulesets for tamper/drift verification (MF6).
/// </summary>
public static class BoundaryRulesetRenderer
{
    public const string BoundaryTableName = "kautuka_egress";
    internal const int GatewayConnectPort = WorkloadRulesetRenderer.GatewayConnectPort;

    public static readonly IReadOnlyList<string> RequiredBoundaryComments =
    [
        "private_reject",
        "wl_net_reject",
        "ipv6_drop_pod",
    ];

    private const string LinuxManagerAuthorizationClusterYaml = """
          load_assignment:
            cluster_name: manager-authorization
            endpoints:
              - lb_endpoints:
                  - endpoint:
                      address:
                        pipe:
                          path: /run/kautuka-nac/envoy.sock
    """;

    private const string WindowsManagerAuthorizationClusterYaml = """
          type: STRICT_DNS
          dns_lookup_family: V4_ONLY
          load_assignment:
            cluster_name: manager-authorization
            endpoints:
              - lb_endpoints:
                  - endpoint:
                      address:
                        socket_address:
                          address: kautuka-decision.internal
                          port_value: 50051
          transport_socket:
            name: envoy.transport_sockets.tls
            typed_config:
              "@type": type.googleapis.com/envoy.extensions.transport_sockets.tls.v3.UpstreamTlsContext
              sni: kautuka-decision.internal
              common_tls_context:
                alpn_protocols: [ "h2" ]
                tls_certificates:
                  - certificate_chain:
                      filename: /etc/envoy/certs/client.crt
                    private_key:
                      filename: /etc/envoy/certs/client.key
                validation_context:
                  trusted_ca:
                    filename: /etc/envoy/certs/ca.crt
                  match_typed_subject_alt_names:
                    - san_type: DNS
                      matcher:
                        exact: kautuka-decision.internal
    """;

    /// <summary>
    /// Renders the egress pod boundary nftables ruleset (<c>table ip kautuka_egress</c> and <c>table ip6 kautuka_egress</c>).
    /// </summary>
    public static string RenderBoundaryNft(
        bool includeLanGrants = false,
        string? decisionEndpointIp = null,
        IEnumerable<string>? upstreamDnsIps = null,
        bool includeGatewayConnect = false,
        bool includeApplianceTelemetry = false)
    {
        string decisionEndpointRule = !string.IsNullOrWhiteSpace(decisionEndpointIp)
            ? $"    ip daddr {decisionEndpointIp.Trim()} tcp dport 50051 counter accept comment \"decision_endpoint\""
            : string.Empty;

        string dnsUpstreamRule = BuildUpstreamDnsNftRule(upstreamDnsIps);

        string lanApprovedRule = includeLanGrants
            ? $"    ip saddr {EgressTopologyManager.OutsideLanSourceIp} ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 }} ip daddr != 10.247.0.0/16 counter accept comment \"lan_approved\""
            : string.Empty;

        string privateRejectRule = NetworkAddressClassifier.RenderNftPrivateRejectRule();
        string gatewayConnectMatch = FormattableString.Invariant(
            $"ip saddr {WorkloadRulesetRenderer.ApplianceGatewayIp} ip daddr {EgressTopologyManager.EgressPodIp} tcp dport {GatewayConnectPort}");
        string telemetryMatch = FormattableString.Invariant(
            $"ip saddr {{ {WorkloadRulesetRenderer.ApplianceGatewayIp}, {WorkloadRulesetRenderer.ApplianceWebHostIp}, {WorkloadRulesetRenderer.ApplianceCredentialsHostIp}, {WorkloadRulesetRenderer.ApplianceChannelHostIp}, {WorkloadRulesetRenderer.ApplianceUpdateServiceIp} }} ip daddr {EgressTopologyManager.EgressPodIp} tcp dport {WorkloadRulesetRenderer.TelemetryIngressPort}");

        var sb = new StringBuilder();
        sb.AppendLine("table ip kautuka_egress");
        sb.AppendLine("delete table ip kautuka_egress");
        sb.AppendLine("table ip kautuka_egress {");
        sb.AppendLine("  chain prerouting {");
        sb.AppendLine("    type nat hook prerouting priority dstnat; policy accept;");
        sb.AppendLine("    ip saddr 10.247.0.0/16 ip daddr 10.247.0.2 tcp dport 53 counter return");
        if (includeGatewayConnect)
            sb.Append("    ").Append(gatewayConnectMatch).AppendLine(" counter return comment \"gateway_connect_no_redirect\"");
        if (includeApplianceTelemetry)
            sb.Append("    ").Append(telemetryMatch).AppendLine(" counter return comment \"telemetry_no_redirect\"");
        sb.AppendLine("    ip saddr 10.247.0.0/16 meta l4proto tcp counter redirect to :15001");
        sb.AppendLine("  }");
        sb.AppendLine("  chain forward {");
        sb.AppendLine("    type filter hook forward priority 0; policy drop;");
        sb.AppendLine("    counter drop");
        sb.AppendLine("  }");
        sb.AppendLine("  chain input {");
        sb.AppendLine("    type filter hook input priority 0; policy drop;");
        sb.AppendLine("    iifname \"lo\" accept");
        sb.AppendLine("    ct state established,related accept");
        sb.AppendLine("    ip saddr 10.247.0.0/16 tcp dport 15001 accept");
        sb.AppendLine("    ip saddr 10.247.0.0/16 ip daddr 10.247.0.2 meta l4proto { tcp, udp } th dport 53 accept");
        if (includeGatewayConnect)
            sb.Append("    ").Append(gatewayConnectMatch).AppendLine(" counter accept comment \"gateway_connect_ingress\"");
        if (includeApplianceTelemetry)
            sb.Append("    ").Append(telemetryMatch).AppendLine(" counter accept comment \"telemetry_ingress\"");
        sb.AppendLine("    counter drop");
        sb.AppendLine("  }");
        sb.AppendLine("  chain output {");
        sb.AppendLine("    type filter hook output priority 0; policy accept;");
        sb.AppendLine("    ct state established,related accept");
        sb.AppendLine("    oifname \"lo\" ip daddr 127.0.0.1 meta l4proto { tcp, udp } th dport 10053 accept");
        sb.AppendLine("    oifname \"lo\" ip daddr 127.0.0.1 tcp dport { 15002, 15003 } accept");
        if (!string.IsNullOrEmpty(dnsUpstreamRule))
        {
            sb.AppendLine(dnsUpstreamRule);
        }
        if (!string.IsNullOrEmpty(decisionEndpointRule))
        {
            sb.AppendLine(decisionEndpointRule);
        }
        if (!string.IsNullOrEmpty(lanApprovedRule))
        {
            sb.AppendLine(lanApprovedRule);
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"    ip daddr {WorkloadRulesetRenderer.PackageProxyIp} tcp dport {WorkloadRulesetRenderer.PackageProxyPort} counter accept comment \"package_proxy_cluster\"");
        if (includeApplianceTelemetry)
            sb.AppendLine(CultureInfo.InvariantCulture, $"    ip daddr {WorkloadRulesetRenderer.ObservabilityIp} tcp dport {WorkloadRulesetRenderer.ObservabilityPort} counter accept comment \"telemetry_cluster\"");
        sb.Append("    ").AppendLine(privateRejectRule);
        sb.AppendLine("    ip daddr 10.247.0.0/16 counter reject comment \"wl_net_reject\"");
        sb.AppendLine("    accept");
        sb.AppendLine("  }");
        if (includeLanGrants)
        {
            sb.AppendLine("  chain postrouting {");
            sb.AppendLine("    type nat hook postrouting priority srcnat; policy accept;");
            sb.AppendLine($"    ip saddr {EgressTopologyManager.OutsideLanSourceIp} ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 }} ip daddr != 10.247.0.0/16 counter snat to {EgressTopologyManager.OutsidePodIp} comment \"lan_source_masquerade\"");
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        sb.AppendLine("table ip6 kautuka_egress");
        sb.AppendLine("delete table ip6 kautuka_egress");
        sb.AppendLine("table ip6 kautuka_egress {");
        sb.AppendLine("  chain output {");
        sb.AppendLine("    type filter hook output priority 0; policy drop;");
        sb.AppendLine("    counter drop comment \"ipv6_drop_pod\"");
        sb.AppendLine("  }");
        sb.Append("}\n");

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string BuildUpstreamDnsNftRule(IEnumerable<string>? upstreamDnsIps)
    {
        if (upstreamDnsIps is null)
        {
            return string.Empty;
        }

        var validated = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in upstreamDnsIps)
        {
            if (string.IsNullOrWhiteSpace(raw)
                || !System.Net.IPAddress.TryParse(raw.Trim(), out System.Net.IPAddress? parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            byte[] b = parsed.GetAddressBytes();
            if (b[0] == 127 || (b[0] == 10 && b[1] == 247) || (b[0] == 169 && b[1] == 254))
            {
                continue;
            }

            string normalized = parsed.ToString();
            if (seen.Add(normalized))
            {
                validated.Add(normalized);
            }
        }

        if (validated.Count == 0)
        {
            return string.Empty;
        }

        string daddrExpr = validated.Count == 1
            ? validated[0]
            : "{ " + string.Join(", ", validated) + " }";

        return $"    ip daddr {daddrExpr} meta l4proto {{ tcp, udp }} th dport 53 counter accept comment \"dns_upstream\"";
    }

    /// <summary>
    /// Renders the static Envoy configuration for the egress pod from the template.
    /// </summary>
    public static string RenderEnvoyConfig(
        string adminSocketPath = "/run/kautuka-admin/admin.sock",
        bool isWindowsMtls = false,
        bool includeGatewayConnect = false,
        bool includeApplianceTelemetry = false,
        string? telemetryAuthorization = null,
        TimeSpan approvalHold = default)
    {
        string template = ResolveTemplate("envoy.static.yaml.tmpl");
        string gatewayConnect = includeGatewayConnect ? RenderGatewayConnectListener() : string.Empty;

        string clusterSnippet = isWindowsMtls
            ? WindowsManagerAuthorizationClusterYaml
            : LinuxManagerAuthorizationClusterYaml;

        IReadOnlyList<string> publicCidrs = [.. NetworkAddressClassifier.NonPublicV4CanonicalCidrs];
        IReadOnlyList<string> lanCidrs =
        [
            .. NetworkAddressClassifier.NonPublicV4CanonicalCidrs.Where(static c =>
                !c.Equals("10.0.0.0/8", StringComparison.Ordinal)
                && !c.Equals("172.16.0.0/12", StringComparison.Ordinal)
                && !c.Equals("192.168.0.0/16", StringComparison.Ordinal)),
            NetworkAddressClassifier.WorkloadSubnetCidr
        ];

        TimeSpan authorizationDeadline = (includeGatewayConnect ? approvalHold : TimeSpan.Zero) + TimeSpan.FromSeconds(2);
        TimeSpan connectIdleDeadline = authorizationDeadline > TimeSpan.FromMinutes(5)
            ? authorizationDeadline + TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(5);
        string rendered = template
            .Replace("{{GATEWAY_CONNECT_LISTENER}}", gatewayConnect, StringComparison.Ordinal)
            .Replace("{{AUTHORIZATION_DEADLINE}}", FormattableString.Invariant($"{authorizationDeadline.TotalSeconds:0.###}s"), StringComparison.Ordinal)
            .Replace("{{GATEWAY_STREAM_IDLE_DEADLINE}}", FormattableString.Invariant($"{connectIdleDeadline.TotalSeconds:0.###}s"), StringComparison.Ordinal)
            .Replace("{{TELEMETRY_LISTENER}}", includeApplianceTelemetry ? RenderTelemetryListener(telemetryAuthorization) : string.Empty, StringComparison.Ordinal)
            .Replace("{{TELEMETRY_CLUSTER}}", includeApplianceTelemetry ? RenderTelemetryCluster() : string.Empty, StringComparison.Ordinal)
            .Replace("{{ADMIN_SOCKET_PATH}}", adminSocketPath, StringComparison.Ordinal)
            .Replace("{{LAN_SOURCE_IP}}", EgressTopologyManager.OutsideLanSourceIp, StringComparison.Ordinal)
            .Replace("{{MANAGER_AUTHORIZATION_CLUSTER}}", clusterSnippet, StringComparison.Ordinal);

        rendered = ReplaceIndentedPlaceholder(rendered, "{{RESOLVED_FILTER_PUBLIC}}", publicCidrs);
        rendered = ReplaceIndentedPlaceholder(rendered, "{{RESOLVED_FILTER_LAN}}", lanCidrs);

        return rendered;
    }

    private static string RenderTelemetryListener(string? authorization)
    {
        if (authorization is not null && (authorization.Length is 0 or > 256
            || !authorization.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')))
            throw new ArgumentException("Invalid appliance telemetry authorization format.", nameof(authorization));

        string header = authorization is null ? string.Empty : FormattableString.Invariant($"""
                                  request_headers_to_add:
                                    - header:
                                        key: authorization
                                        value: "Basic {authorization}"
                                      append_action: OVERWRITE_IF_EXISTS_OR_ADD

        """);
        return FormattableString.Invariant($"""
        - name: appliance_telemetry
          address:
            socket_address:
              address: {EgressTopologyManager.EgressPodIp}
              port_value: {WorkloadRulesetRenderer.TelemetryIngressPort}
          filter_chains:
            - filters:
                - name: envoy.filters.network.http_connection_manager
                  typed_config:
                    "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
                    stat_prefix: appliance_telemetry
                    normalize_path: true
                    path_with_escaped_slashes_action: REJECT_REQUEST
                    route_config:
                      name: telemetry_ingestion
                      virtual_hosts:
                        - name: observability
                          domains: ["*"]
                          routes:
                            - match:
                                safe_regex:
                                  regex: '^/api/default/v1/(logs|metrics|traces)$'
                                headers:
                                  - name: ":method"
                                    string_match:
                                      exact: "POST"
                              route:
                                cluster: appliance_observability
    {header}                        - match:
                                prefix: "/"
                              direct_response:
                                status: 404
                    http_filters:
                      - name: envoy.filters.http.router
                        typed_config:
                          "@type": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router

    """);
    }

    private static string RenderTelemetryCluster() => FormattableString.Invariant($"""
        - name: appliance_observability
          type: STATIC
          connect_timeout: 1s
          load_assignment:
            cluster_name: appliance_observability
            endpoints:
              - lb_endpoints:
                  - endpoint:
                      address:
                        socket_address:
                          address: {WorkloadRulesetRenderer.ObservabilityIp}
                          port_value: {WorkloadRulesetRenderer.ObservabilityPort}

    """);

    private static string RenderGatewayConnectListener()
    {
        string filter = ResolveTemplate("envoy.gateway-connect-dfp.yaml.tmpl").TrimEnd('\r', '\n') + "\n";
        string publicFilter = filter
            .Replace("{{CLASS}}", "public", StringComparison.Ordinal)
            .Replace("{{CACHE}}", "dfp_cache", StringComparison.Ordinal)
            .Replace("{{FILTER}}", "{{RESOLVED_FILTER_PUBLIC}}", StringComparison.Ordinal);
        string lanFilter = filter
            .Replace("{{CLASS}}", "lan", StringComparison.Ordinal)
            .Replace("{{CACHE}}", "dfp_cache_lan", StringComparison.Ordinal)
            .Replace("{{FILTER}}", "{{RESOLVED_FILTER_LAN}}", StringComparison.Ordinal);
        return ResolveTemplate("envoy.gateway-connect.yaml.tmpl")
            .Replace("{{GATEWAY_CONNECT_IP}}", EgressTopologyManager.EgressPodIp, StringComparison.Ordinal)
            .Replace("{{GATEWAY_CONNECT_PORT}}", GatewayConnectPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{GATEWAY_CONNECT_DFP_FILTERS}}", publicFilter + lanFilter, StringComparison.Ordinal)
            .TrimEnd('\r', '\n') + "\n";
    }

    public static string RenderEnvoyAddressRangesYaml(IEnumerable<string> cidrs, string indent)
    {
        ArgumentNullException.ThrowIfNull(cidrs);
        var sb = new StringBuilder();
        bool first = true;
        foreach (string cidr in cidrs)
        {
            string[] parts = cidr.Split('/');
            string prefix = parts[0];
            string len = parts.Length > 1 ? parts[1] : "32";
            if (!first)
            {
                sb.AppendLine();
            }

            first = false;
            sb.Append(indent).Append("- address_prefix: ").AppendLine(prefix);
            sb.Append(indent).Append("  prefix_len: ").Append(len);
        }

        return sb.ToString();
    }

    private static string ReplaceIndentedPlaceholder(string template, string placeholder, IEnumerable<string> cidrs)
    {
        while (true)
        {
            int index = template.IndexOf(placeholder, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            int lineStart = template.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int indentLen = index - lineStart;
            string indent = template.Substring(lineStart, indentLen);

            string rendered = RenderEnvoyAddressRangesYaml(cidrs, indent);
            template = string.Concat(template.AsSpan(0, lineStart), rendered, template.AsSpan(index + placeholder.Length));
        }

        return template;
    }

    /// <summary>
    /// Renders the CoreDNS configuration from the template.
    /// </summary>
    public static string RenderCorefile(
        string dnsIp = "10.247.0.2",
        IEnumerable<string>? upstreamDnsIps = null)
    {
        string template = ResolveTemplate("Corefile.tmpl");
        var upstreams = new List<string>();
        if (upstreamDnsIps is not null)
        {
            foreach (string candidate in upstreamDnsIps)
            {
                if (System.Net.IPAddress.TryParse(candidate, out System.Net.IPAddress? address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !upstreams.Contains(address.ToString(), StringComparer.Ordinal))
                {
                    upstreams.Add(address.ToString());
                }
            }
        }

        if (upstreams.Count == 0)
        {
            upstreams.Add(EgressTopologyManager.OutsideGatewayIp);
        }

        string forwarders = string.Join(" ", upstreams);
        return template
            .Replace("{{DNS_IP}}", dnsIp, StringComparison.Ordinal)
            .Replace("{{UPSTREAM_DNS}}", forwarders, StringComparison.Ordinal);
    }

    internal static string ResolveTemplate(string templateFileName)
    {
        var assembly = typeof(BoundaryRulesetRenderer).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Hosting.NetworkAccess.{templateFileName}";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        throw new FileNotFoundException($"Embedded template resource '{resourceName}' not found in assembly '{assembly.FullName}'.");
    }

    /// <summary>Computes the normalized hash used to verify a live nftables ruleset.</summary>
    public static string ComputeCanonicalNftJsonHash(
        string nftJsonOutput,
        IReadOnlyList<string>? requiredComments = null,
        string? requiredWorkloadIp = null)
        => NftRulesetCanonicalizer.ComputeHash(nftJsonOutput, requiredComments, requiredWorkloadIp);
}
