using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

/// <summary>
/// Renders boundary nftables ruleset, static Envoy configuration, and CoreDNS configuration,
/// and computes canonical normalized hashes of nftables rulesets for tamper/drift verification (MF6).
/// </summary>
public static class BoundaryRulesetRenderer
{
    public const string BoundaryTableName = "kautuka_egress";

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
        string? decisionEndpointIp = null)
    {
        string decisionEndpointRule = !string.IsNullOrWhiteSpace(decisionEndpointIp)
            ? $"    ip daddr {decisionEndpointIp.Trim()} tcp dport 50051 counter accept comment \"decision_endpoint\""
            : string.Empty;

        string lanApprovedRule = includeLanGrants
            ? "    ip saddr 192.0.0.8 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 } ip daddr != 10.247.0.0/16 counter accept comment \"lan_approved\""
            : string.Empty;

        string privateRejectRule = NetworkAddressClassifier.RenderNftPrivateRejectRule();

        var sb = new StringBuilder();
        sb.AppendLine("table ip kautuka_egress");
        sb.AppendLine("delete table ip kautuka_egress");
        sb.AppendLine("table ip kautuka_egress {");
        sb.AppendLine("  chain prerouting {");
        sb.AppendLine("    type nat hook prerouting priority dstnat; policy accept;");
        sb.AppendLine("    ip saddr 10.247.0.0/16 ip daddr 10.247.0.2 tcp dport 53 counter return");
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
        sb.AppendLine("    counter drop");
        sb.AppendLine("  }");
        sb.AppendLine("  chain output {");
        sb.AppendLine("    type filter hook output priority 0; policy accept;");
        sb.AppendLine("    ct state established,related accept");
        sb.AppendLine("    oifname \"lo\" ip daddr 127.0.0.1 meta l4proto { tcp, udp } th dport 10053 accept");
        sb.AppendLine("    oifname \"lo\" ip daddr 127.0.0.1 tcp dport { 15002, 15003 } accept");
        if (!string.IsNullOrEmpty(decisionEndpointRule))
        {
            sb.AppendLine(decisionEndpointRule);
        }
        if (!string.IsNullOrEmpty(lanApprovedRule))
        {
            sb.AppendLine(lanApprovedRule);
        }
        sb.Append("    ").AppendLine(privateRejectRule);
        sb.AppendLine("    ip daddr 10.247.0.0/16 counter reject comment \"wl_net_reject\"");
        sb.AppendLine("    accept");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("table ip6 kautuka_egress");
        sb.AppendLine("delete table ip6 kautuka_egress");
        sb.AppendLine("table ip6 kautuka_egress {");
        sb.AppendLine("  chain output {");
        sb.AppendLine("    type filter hook output priority 0; policy drop;");
        sb.AppendLine("    counter drop comment \"ipv6_drop_pod\"");
        sb.AppendLine("  }");
        sb.Append("}\n");

        return sb.ToString();
    }

    /// <summary>
    /// Renders the static Envoy configuration for the egress pod from the template.
    /// </summary>
    public static string RenderEnvoyConfig(
        string adminSocketPath = "/run/kautuka-admin/admin.sock",
        bool isWindowsMtls = false,
        string? templatePath = null)
    {
        string template = ResolveTemplate(templatePath, "envoy.static.yaml.tmpl");

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

        string rendered = template
            .Replace("{{ADMIN_SOCKET_PATH}}", adminSocketPath, StringComparison.Ordinal)
            .Replace("{{MANAGER_AUTHORIZATION_CLUSTER}}", clusterSnippet, StringComparison.Ordinal);

        rendered = ReplaceIndentedPlaceholder(rendered, "{{RESOLVED_FILTER_PUBLIC}}", publicCidrs);
        rendered = ReplaceIndentedPlaceholder(rendered, "{{RESOLVED_FILTER_LAN}}", lanCidrs);

        return rendered;
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
        string? templatePath = null)
    {
        string template = ResolveTemplate(templatePath, "Corefile.tmpl");
        return template.Replace("{{DNS_IP}}", dnsIp, StringComparison.Ordinal);
    }

    internal static string ResolveTemplate(string? templatePath, string templateFileName)
    {
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            return File.ReadAllText(templatePath);
        }

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

    /// <summary>
    /// Computes a canonical SHA-256 hash from <c>nft -j list table ...</c> JSON output.
    /// Strips transient runtime state (<c>handle</c>, <c>metainfo</c>, and dynamic packet/byte counts),
    /// verifies that all required comment tags and the workload IP (when specified) are present,
    /// and hashes the canonicalized JSON tree.
    /// </summary>
    public static string ComputeCanonicalNftJsonHash(
        string nftJsonOutput,
        IReadOnlyList<string>? requiredComments = null,
        string? requiredWorkloadIp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nftJsonOutput);
        JsonNode root = JsonNode.Parse(nftJsonOutput)
            ?? throw new InvalidOperationException("Failed to parse nft JSON output.");

        var foundComments = new HashSet<string>(StringComparer.Ordinal);
        bool foundWorkloadIp = false;

        JsonArray nftablesArray;
        if (root is JsonObject obj && obj["nftables"] is JsonArray arr)
        {
            nftablesArray = arr;
        }
        else if (root is JsonArray directArr)
        {
            nftablesArray = directArr;
        }
        else
        {
            throw new InvalidOperationException("Unexpected nft JSON root element.");
        }

        var canonicalArray = new JsonArray();
        foreach (JsonNode? item in nftablesArray)
        {
            if (item is null)
            {
                continue;
            }

            // Omit metainfo object entirely (timestamp, version, etc.)
            if (item is JsonObject itemObj && itemObj.ContainsKey("metainfo"))
            {
                continue;
            }

            JsonNode canonicalNode = CanonicalizeNode(item, foundComments, ref foundWorkloadIp, requiredWorkloadIp);
            canonicalArray.Add(canonicalNode);
        }

        // Verify required comments:
        if (requiredComments is not null)
        {
            foreach (string required in requiredComments)
            {
                if (!foundComments.Contains(required))
                {
                    throw new InvalidOperationException($"Canonical nft verification failed: missing required comment tag '{required}'.");
                }
            }
        }

        // Verify required workload IP:
        if (!string.IsNullOrWhiteSpace(requiredWorkloadIp) && !foundWorkloadIp)
        {
            throw new InvalidOperationException($"Canonical nft verification failed: workload IP '{requiredWorkloadIp}' was not found in spoof_drop rule.");
        }

        var options = new JsonSerializerOptions { WriteIndented = false };
        string canonicalJson = canonicalArray.ToJsonString(options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static JsonNode CanonicalizeNode(
        JsonNode node,
        HashSet<string> foundComments,
        ref bool foundWorkloadIp,
        string? expectedIp)
    {
        if (node is JsonObject obj)
        {
            var canonicalObj = new JsonObject();
            // Sort keys deterministically:
            var sortedKeys = obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

            foreach (string key in sortedKeys)
            {
                // Strip dynamic/ephemeral handle:
                if (string.Equals(key, "handle", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonNode? child = obj[key];
                if (child is null)
                {
                    canonicalObj[key] = null;
                    continue;
                }

                // Check comments:
                if (string.Equals(key, "comment", StringComparison.Ordinal) && child is JsonValue commentVal)
                {
                    string? commentStr = commentVal.GetValue<string?>();
                    if (!string.IsNullOrWhiteSpace(commentStr))
                    {
                        foundComments.Add(commentStr.Trim());
                    }
                }

                // Canonicalize counter objects (zero packets and bytes):
                if (string.Equals(key, "counter", StringComparison.Ordinal))
                {
                    var zeroCounter = new JsonObject
                    {
                        ["packets"] = 0,
                        ["bytes"] = 0,
                    };
                    canonicalObj[key] = zeroCounter;
                    continue;
                }

                // Detect IP match in expressions:
                if (!string.IsNullOrWhiteSpace(expectedIp))
                {
                    string childText = child.ToJsonString();
                    if (childText.Contains(expectedIp, StringComparison.Ordinal))
                    {
                        foundWorkloadIp = true;
                    }
                }

                canonicalObj[key] = CanonicalizeNode(child, foundComments, ref foundWorkloadIp, expectedIp);
            }

            return canonicalObj;
        }

        if (node is JsonArray arr)
        {
            var canonicalArr = new JsonArray();
            foreach (JsonNode? item in arr)
            {
                if (item is not null)
                {
                    canonicalArr.Add(CanonicalizeNode(item, foundComments, ref foundWorkloadIp, expectedIp));
                }
            }

            return canonicalArr;
        }

        return node.DeepClone();
    }
}
