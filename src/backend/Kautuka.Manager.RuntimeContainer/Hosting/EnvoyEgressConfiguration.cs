using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record EgressTcpRule(string SourceAddress, string Destination, int Port);

/// <summary>Stock Envoy file resources and observations of their accepted active configuration.</summary>
internal sealed record EnvoyEgressConfiguration(
    string Version, string ClusterDocument, string ListenerDocument, IReadOnlyList<string> ClusterNames)
{
    internal const string ListenerName = "transparent";
    private const string metadataNamespace = "kautuka.egress";

    internal static EnvoyEgressConfiguration Compile(IEnumerable<EgressTcpRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        EgressTcpRule[] normalized = [.. rules.Select(Normalize).Distinct()
            .OrderBy(static rule => rule.Destination, StringComparer.Ordinal).ThenBy(static rule => rule.Port)
            .ThenBy(static rule => rule.SourceAddress, StringComparer.Ordinal)];
        var clusters = new JsonArray();
        var chains = new JsonArray();
        var clusterNames = new List<string>();
        foreach (var group in normalized.GroupBy(static rule => (rule.Destination, rule.Port)))
        {
            string destination = group.Key.Destination;
            int port = group.Key.Port;
            bool literal = IPAddress.TryParse(destination, out _);
            string name = "upstream-" + Hash(destination + ":" + port.ToString(CultureInfo.InvariantCulture));
            clusterNames.Add(name);
            var cluster = new JsonObject
            {
                ["@type"] = "type.googleapis.com/envoy.config.cluster.v3.Cluster",
                ["name"] = name,
                ["type"] = literal ? "STATIC" : "STRICT_DNS",
                ["connect_timeout"] = "5s",
                ["load_assignment"] = new JsonObject
                {
                    ["cluster_name"] = name,
                    ["endpoints"] = new JsonArray(new JsonObject
                    {
                        ["lb_endpoints"] = new JsonArray(new JsonObject
                        {
                            ["endpoint"] = new JsonObject
                            {
                                ["address"] = new JsonObject
                                {
                                    ["socket_address"] = new JsonObject { ["address"] = destination, ["port_value"] = port },
                                }
                            },
                        }),
                    }),
                },
            };
            if (!literal) cluster["dns_lookup_family"] = "V4_ONLY";
            SetMarker(cluster, name);
            clusters.Add((JsonNode)cluster);

            chains.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["filters"] = new JsonArray(new JsonObject
                {
                    ["name"] = "envoy.filters.network.tcp_proxy",
                    ["typed_config"] = new JsonObject
                    {
                        ["@type"] = "type.googleapis.com/envoy.extensions.filters.network.tcp_proxy.v3.TcpProxy",
                        ["stat_prefix"] = "egress",
                        ["cluster"] = name,
                        ["access_log"] = new JsonArray(AccessLog("proxy")),
                    },
                }),
            });
        }

        var listener = JsonNode.Parse("""
            {
              "@type": "type.googleapis.com/envoy.config.listener.v3.Listener",
              "name": "transparent",
              "address": { "socket_address": { "address": "0.0.0.0", "port_value": 15001 } },
              "use_original_dst": true,
              "listener_filters_timeout": "2s",
              "continue_on_listener_filters_timeout": true,
              "listener_filters": [
                { "name": "envoy.filters.listener.original_dst", "typed_config": { "@type": "type.googleapis.com/envoy.extensions.filters.listener.original_dst.v3.OriginalDst" } },
                { "name": "envoy.filters.listener.tls_inspector", "typed_config": { "@type": "type.googleapis.com/envoy.extensions.filters.listener.tls_inspector.v3.TlsInspector" } }
              ],
              "default_filter_chain": { "filters": [
                { "name": "envoy.filters.network.direct_response", "typed_config": { "@type": "type.googleapis.com/envoy.extensions.filters.network.direct_response.v3.Config", "response": { "inline_string": "" } } }
              ] }
            }
            """)!.AsObject();
        JsonObject deny = listener["default_filter_chain"]!.DeepClone().AsObject();
        deny["name"] = "deny";
        chains.Add((JsonNode)deny);
        listener["filter_chains"] = chains;
        listener["filter_chain_matcher"] = SourceMatcher(normalized);
        listener["access_log"] = new JsonArray(AccessLog("listener"));
        string version = Hash(clusters.ToJsonString() + "\n" + listener.ToJsonString());
        SetMarker(listener, version);
        return new EnvoyEgressConfiguration(version, Envelope(version, clusters), Envelope(version, new JsonArray(listener)), clusterNames);
    }

    internal bool ClustersAreActive(JsonElement dump)
    {
        foreach (JsonElement config in Configurations(dump, "ClustersConfigDump"))
        {
            if (Text(config, "version_info") != Version) continue;
            JsonElement[] active = Items(config, "dynamic_active_clusters");
            if (active.Length != ClusterNames.Count) return false;
            foreach (string name in ClusterNames)
            {
                if (!active.Any(item => item.TryGetProperty("cluster", out JsonElement cluster)
                    && Text(cluster, "name") == name && HasMarker(cluster, name))) return false;
            }

            return Items(config, "dynamic_warming_clusters").Length == 0;
        }

        return false;
    }

    internal bool ListenerIsActive(JsonElement dump)
    {
        foreach (JsonElement config in Configurations(dump, "ListenersConfigDump"))
        {
            if (Text(config, "version_info") != Version) continue;
            foreach (JsonElement item in Items(config, "dynamic_listeners"))
            {
                if (Text(item, "name") != ListenerName) continue;
                if (item.TryGetProperty("warming_state", out JsonElement warming)
                    && warming.ValueKind == JsonValueKind.Object && warming.EnumerateObject().Any()) return false;
                // Envoy in-place updates can leave active_state.version_info stale. Inspect the active marker instead.
                return item.TryGetProperty("active_state", out JsonElement state)
                    && state.TryGetProperty("listener", out JsonElement listener)
                    && Text(listener, "name") == ListenerName && HasMarker(listener, Version);
            }
        }

        return false;
    }

    private static JsonObject SourceMatcher(EgressTcpRule[] rules)
    {
        var sources = new JsonObject();
        foreach (var source in rules.GroupBy(static rule => rule.SourceAddress))
        {
            var ports = new JsonObject();
            foreach (var port in source.GroupBy(static rule => rule.Port))
            {
                var names = new JsonObject();
                var addresses = new JsonObject();
                foreach (EgressTcpRule rule in port)
                {
                    string chain = "upstream-" + Hash(rule.Destination + ":" + rule.Port.ToString(CultureInfo.InvariantCulture));
                    (IPAddress.TryParse(rule.Destination, out _) ? addresses : names)[rule.Destination] = ChainAction(chain);
                }

                JsonObject fallback = addresses.Count == 0 ? ChainAction("deny")
                    : new JsonObject { ["matcher"] = ExactMatcher("destination_ip", "DestinationIPInput", addresses, ChainAction("deny")) };
                ports[port.Key.ToString(CultureInfo.InvariantCulture)] = names.Count == 0 ? fallback
                    : new JsonObject { ["matcher"] = ExactMatcher("server_name", "ServerNameInput", names, fallback) };
            }

            sources[source.Key] = new JsonObject { ["matcher"] = ExactMatcher("destination_port", "DestinationPortInput", ports, ChainAction("deny")) };
        }

        return sources.Count == 0 ? new JsonObject { ["on_no_match"] = ChainAction("deny") }
            : ExactMatcher("source_ip", "SourceIPInput", sources, ChainAction("deny"));
    }

    private static JsonObject ExactMatcher(string name, string type, JsonObject choices, JsonObject fallback) => new()
    {
        ["matcher_tree"] = new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["name"] = "envoy.matching.inputs." + name,
                ["typed_config"] = new JsonObject
                {
                    ["@type"] = "type.googleapis.com/envoy.extensions.matching.common_inputs.network.v3." + type,
                }
            },
            ["exact_match_map"] = new JsonObject { ["map"] = choices },
        },
        ["on_no_match"] = fallback,
    };

    private static JsonObject ChainAction(string name) => new()
    {
        ["action"] = new JsonObject
        {
            ["name"] = name,
            ["typed_config"] = new JsonObject
            {
                ["@type"] = "type.googleapis.com/google.protobuf.StringValue",
                ["value"] = name,
            }
        },
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "DNS names use lowercase ASCII for the wire policy.")]
    private static EgressTcpRule Normalize(EgressTcpRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!IPAddress.TryParse(rule.SourceAddress, out IPAddress? source) || !IsUnicastV4(source) || IPAddress.IsLoopback(source))
        {
            throw new ArgumentException("Egress source must be an assigned unicast IPv4 address.", nameof(rule));
        }

        if (rule.Port is < 1 or > 65535) throw new ArgumentException("Egress TCP port must be between 1 and 65535.", nameof(rule));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Destination);
        string destination;
        if (IPAddress.TryParse(rule.Destination, out IPAddress? address))
        {
            if (!IsUnicastV4(address)) throw new ArgumentException("Egress literal destination must be unicast IPv4.", nameof(rule));
            destination = address.ToString();
        }
        else
        {
            destination = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(rule.Destination.TrimEnd('.')).ToLowerInvariant();
            if (destination.Length > 253 || Uri.CheckHostName(destination) != UriHostNameType.Dns || destination.Contains('*', StringComparison.Ordinal))
            {
                throw new ArgumentException("Egress hostname must be an exact DNS name.", nameof(rule));
            }
        }

        return rule with { SourceAddress = source.ToString(), Destination = destination };
    }

    private static bool IsUnicastV4(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is > 0 and < 224;

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Envelope(string version, JsonArray resources)
        => new JsonObject { ["version_info"] = version, ["resources"] = resources }.ToJsonString();

    private static void SetMarker(JsonObject resource, string value)
        => resource["metadata"] = new JsonObject
        {
            ["filter_metadata"] = new JsonObject
            {
                [metadataNamespace] = new JsonObject { ["policy_version"] = value },
            }
        };

    private static bool HasMarker(JsonElement resource, string value)
        => resource.TryGetProperty("metadata", out JsonElement metadata)
            && metadata.TryGetProperty("filter_metadata", out JsonElement filters)
            && filters.TryGetProperty(metadataNamespace, out JsonElement policy) && Text(policy, "policy_version") == value;

    private static IEnumerable<JsonElement> Configurations(JsonElement dump, string suffix)
        => Items(dump, "configs").Where(config => Text(config, "@type")?.EndsWith("." + suffix, StringComparison.Ordinal) == true);

    private static JsonElement[] Items(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()] : [];

    private static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static JsonObject AccessLog(string phase)
    {
        JsonObject log = JsonNode.Parse("""
        {
          "name": "envoy.access_loggers.stdout",
          "typed_config": {
            "@type": "type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog",
            "log_format": { "json_format": {
              "timestamp": "%START_TIME%", "source": "%DOWNSTREAM_REMOTE_ADDRESS_WITHOUT_PORT%",
              "source_address": "%DOWNSTREAM_REMOTE_ADDRESS%", "destination": "%DOWNSTREAM_LOCAL_ADDRESS%",
              "sni": "%REQUESTED_SERVER_NAME%", "upstream_cluster": "%UPSTREAM_CLUSTER%", "details": "%CONNECTION_TERMINATION_DETAILS%",
              "bytes_received": "%BYTES_RECEIVED%", "bytes_sent": "%BYTES_SENT%", "duration_ms": "%DURATION%"
            } }
          }
        }
        """)!.AsObject();
        log["typed_config"]!["log_format"]!["json_format"]!["phase"] = phase;
        return log;
    }
}
