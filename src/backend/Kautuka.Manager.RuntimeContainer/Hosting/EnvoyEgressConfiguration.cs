using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record EgressTcpRule(string SourceAddress, string Destination, int Port);
internal sealed record EgressPackageRoute(string SourceAddress, string ProxyAddress, string BackendAddress, string ProxySourceAddress);


/// <summary>Stock Envoy file resources and observations of their accepted active configuration.</summary>
internal sealed record EnvoyEgressConfiguration(
    string Version, string ClusterDocument, string ListenerDocument, IReadOnlyList<string> ClusterNames)
{
    internal const string ListenerName = "transparent";
    internal const string ArtifactListenerName = "host-artifacts";
    private const string metadataNamespace = "kautuka.egress";

    internal static EnvoyEgressConfiguration Compile(IEnumerable<EgressTcpRule> rules, string? externalSourceAddress = null, IEnumerable<EgressPackageRoute>? packageRoutes = null, IEnumerable<string>? artifactHosts = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        EgressTcpRule[] normalized = [.. rules.Select(Normalize).Distinct()
            .OrderBy(static rule => rule.Destination, StringComparer.Ordinal).ThenBy(static rule => rule.Port)
            .ThenBy(static rule => rule.SourceAddress, StringComparer.Ordinal)];
        if (externalSourceAddress is not null) externalSourceAddress = NormalizeAddress(externalSourceAddress);
        EgressPackageRoute[] packages = [.. (packageRoutes ?? []).Select(static route => new EgressPackageRoute(
            NormalizeAddress(route.SourceAddress), NormalizeAddress(route.ProxyAddress), NormalizeAddress(route.BackendAddress), NormalizeAddress(route.ProxySourceAddress)))
            .Distinct().OrderBy(static route => route.SourceAddress, StringComparer.Ordinal).ThenBy(static route => route.ProxyAddress, StringComparer.Ordinal)];
        if (packages.Length > 0 && (externalSourceAddress is null || packages.Any(route => route.ProxySourceAddress == externalSourceAddress)
            || packages.GroupBy(static route => (route.SourceAddress, route.ProxyAddress)).Any(static group => group.Count() != 1)))
            throw new ArgumentException("Package routes need unambiguous topology and separate outside/core source addresses.", nameof(packageRoutes));
        string[] artifacts = [.. (artifactHosts ?? []).Select(host => Normalize(new EgressTcpRule("192.0.2.1", host, 443)).Destination)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (artifacts.Any(host => IPAddress.TryParse(host, out _))) throw new ArgumentException("Artifact hosts must be exact DNS names.", nameof(artifactHosts));
        if (artifacts.Length > 0 && externalSourceAddress is null) throw new ArgumentException("Artifact routes require the outside source address.", nameof(externalSourceAddress));
        var clusters = new JsonArray();
        var chains = new JsonArray();
        var clusterNames = new List<string>();
        var targets = normalized.Select(rule => (Destination: rule.Destination, Port: rule.Port, Name: "upstream-" + Hash(rule.Destination + ":" + rule.Port.ToString(CultureInfo.InvariantCulture)), Bind: externalSourceAddress))
            .Concat(artifacts.Select(host => (Destination: host, Port: 443, Name: "upstream-" + Hash(host + ":443"), Bind: externalSourceAddress)))
            .Distinct().Concat(packages.Select(route => (Destination: route.BackendAddress, Port: 20101, Name: PackageClusterName(route), Bind: (string?)route.ProxySourceAddress)).Distinct());
        foreach (var target in targets)
        {
            string destination = target.Destination;
            int port = target.Port;
            bool literal = IPAddress.TryParse(destination, out _);
            string name = target.Name;
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
            if (target.Bind is not null) cluster["upstream_bind_config"] = new JsonObject
            {
                ["source_address"] = new JsonObject { ["address"] = target.Bind, ["port_value"] = 0 },
            };
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
        listener["filter_chain_matcher"] = SourceMatcher(normalized, packages);
        listener["access_log"] = new JsonArray(AccessLog("listener"));
        JsonObject? artifactListener = artifacts.Length == 0 ? null : ArtifactListener(artifacts);
        string version = Hash(clusters.ToJsonString() + "\n" + listener.ToJsonString() + (artifactListener is null ? "" : "\n" + artifactListener.ToJsonString()));
        SetMarker(listener, version);
        var listeners = new JsonArray(listener);
        if (artifactListener is not null) { SetMarker(artifactListener, version); listeners.Add((JsonNode)artifactListener); }
        return new EnvoyEgressConfiguration(version, Envelope(version, clusters), Envelope(version, listeners), clusterNames);
    }

    internal bool ClustersAreActive(JsonElement dump)
    {
        foreach (JsonElement config in Configurations(dump, "ClustersConfigDump"))
        {
            // Envoy omits the enclosing version when no dynamic clusters exist.
            // Exact absence is the observable condition for an empty required set.
            if (ClusterNames.Count != 0 && Text(config, "version_info") != Version) continue;
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
        using JsonDocument expected = JsonDocument.Parse(ListenerDocument);
        string[] names = [.. expected.RootElement.GetProperty("resources").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
        foreach (JsonElement config in Configurations(dump, "ListenersConfigDump"))
        {
            if (Text(config, "version_info") != Version) continue;
            JsonElement[] listeners = Items(config, "dynamic_listeners");
            if (listeners.Length != names.Length) continue;
            bool matches = names.All(name => listeners.Any(item => Text(item, "name") == name
                && !(item.TryGetProperty("warming_state", out JsonElement warming) && warming.ValueKind == JsonValueKind.Object && warming.EnumerateObject().Any())
                && item.TryGetProperty("active_state", out JsonElement state) && state.TryGetProperty("listener", out JsonElement listener)
                && Text(listener, "name") == name && HasMarker(listener, Version)));
            if (matches) return true;
        }
        return false;
    }

    private static JsonObject ArtifactListener(string[] hosts)
    {
        var virtualHosts = new JsonArray();
        foreach (string host in hosts)
            virtualHosts.Add((JsonNode)new JsonObject
            {
                ["name"] = "artifact-" + Hash(host),
                ["domains"] = new JsonArray(host + ":443"),
                ["routes"] = new JsonArray(new JsonObject
                {
                    ["match"] = new JsonObject
                    {
                        ["connect_matcher"] = new JsonObject(),
                        ["headers"] = new JsonArray(
                        new JsonObject { ["name"] = ":method", ["string_match"] = new JsonObject { ["exact"] = "CONNECT" } })
                    },
                    ["route"] = new JsonObject
                    {
                        ["cluster"] = "upstream-" + Hash(host + ":443"),
                        ["timeout"] = "0s",
                        ["upgrade_configs"] = new JsonArray(new JsonObject { ["upgrade_type"] = "CONNECT", ["connect_config"] = new JsonObject() })
                    },
                }),
            });
        virtualHosts.Add((JsonNode)new JsonObject
        {
            ["name"] = "deny",
            ["domains"] = new JsonArray("*"),
            ["routes"] = new JsonArray(
            new JsonObject { ["match"] = new JsonObject { ["prefix"] = "/" }, ["direct_response"] = new JsonObject { ["status"] = 403 } },
            new JsonObject { ["match"] = new JsonObject { ["connect_matcher"] = new JsonObject() }, ["direct_response"] = new JsonObject { ["status"] = 403 } })
        });
        JsonObject log = AccessLog("host-artifact");
        log["typed_config"]!["log_format"]!["json_format"]!["authority"] = "%REQ(:AUTHORITY)%";
        log["typed_config"]!["log_format"]!["json_format"]!["status"] = "%RESPONSE_CODE%";
        return new JsonObject
        {
            ["@type"] = "type.googleapis.com/envoy.config.listener.v3.Listener",
            ["name"] = ArtifactListenerName,
            ["address"] = new JsonObject { ["pipe"] = new JsonObject { ["path"] = "/host-artifacts/connect.sock", ["mode"] = 432 } },
            ["filter_chains"] = new JsonArray(new JsonObject
            {
                ["filters"] = new JsonArray(new JsonObject
                {
                    ["name"] = "envoy.filters.network.http_connection_manager",
                    ["typed_config"] = new JsonObject
                    {
                        ["@type"] = "type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager",
                        ["stat_prefix"] = "host_artifacts",
                        ["codec_type"] = "HTTP1",
                        ["route_config"] = new JsonObject { ["name"] = "host_artifacts", ["virtual_hosts"] = virtualHosts },
                        ["http_filters"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "envoy.filters.http.router",
                            ["typed_config"] = new JsonObject
                            { ["@type"] = "type.googleapis.com/envoy.extensions.filters.http.router.v3.Router" }
                        }),
                        ["access_log"] = new JsonArray(log),
                    },
                })
            }),
        };
    }

    private static string PackageClusterName(EgressPackageRoute route) => "package-" + Hash(route.BackendAddress + ":20101@" + route.ProxySourceAddress);

    private static string NormalizeAddress(string value)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address) || !IsUnicastV4(address) || IPAddress.IsLoopback(address))
            throw new ArgumentException("Topology addresses must be assigned unicast IPv4 addresses.", nameof(value));
        return address.ToString();
    }

    private static JsonObject SourceMatcher(EgressTcpRule[] rules, EgressPackageRoute[] packages)
    {
        var sources = new JsonObject();
        foreach (string source in rules.Select(static rule => rule.SourceAddress).Concat(packages.Select(static route => route.SourceAddress)).Distinct().Order(StringComparer.Ordinal))
        {
            var ports = new JsonObject();
            foreach (int port in rules.Where(rule => rule.SourceAddress == source).Select(static rule => rule.Port)
                .Concat(packages.Any(route => route.SourceAddress == source) ? [20101] : Array.Empty<int>()).Distinct().Order())
            {
                var names = new JsonObject();
                var addresses = new JsonObject();
                foreach (EgressTcpRule rule in rules.Where(rule => rule.SourceAddress == source && rule.Port == port))
                {
                    string chain = "upstream-" + Hash(rule.Destination + ":" + rule.Port.ToString(CultureInfo.InvariantCulture));
                    (IPAddress.TryParse(rule.Destination, out _) ? addresses : names)[rule.Destination] = ChainAction(chain);
                }

                JsonObject fallback = addresses.Count == 0 ? ChainAction("deny")
                    : new JsonObject { ["matcher"] = ExactMatcher("destination_ip", "DestinationIPInput", addresses, ChainAction("deny")) };
                JsonObject ordinary = names.Count == 0 ? fallback
                    : new JsonObject { ["matcher"] = ExactMatcher("server_name", "ServerNameInput", names, fallback) };
                EgressPackageRoute[] routes = port == 20101 ? [.. packages.Where(route => route.SourceAddress == source)] : [];
                if (routes.Length > 0)
                {
                    var endpoints = new JsonObject();
                    foreach (EgressPackageRoute route in routes) endpoints[route.ProxyAddress] = ChainAction(PackageClusterName(route));
                    ordinary = new JsonObject { ["matcher"] = ExactMatcher("destination_ip", "DestinationIPInput", endpoints, ordinary) };
                }
                ports[port.ToString(CultureInfo.InvariantCulture)] = ordinary;
            }

            sources[source] = new JsonObject { ["matcher"] = ExactMatcher("destination_port", "DestinationPortInput", ports, ChainAction("deny")) };
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
            if (!IsUnicastV4(address) || IPAddress.IsLoopback(address)) throw new ArgumentException("Egress literal destination must be unicast IPv4.", nameof(rule));
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
