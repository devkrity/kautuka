using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class EnvoyEgressConfigurationTests
{
    [Fact]
    public async Task ExactRulesPreserveSourceBoundariesAndChooseApprovedUpstream()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile([
            new("10.247.91.10", "allowed.kautuka.test", 8443),
            new("10.247.92.10", "allowed.kautuka.test", 8443),
            new("10.247.91.10", "10.247.90.10", 9443),
        ]);
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        using JsonDocument cds = JsonDocument.Parse(config.ClusterDocument);
        JsonElement listener = lds.RootElement.GetProperty("resources")[0];
        JsonElement sources = listener.GetProperty("filter_chain_matcher").GetProperty("matcher_tree").GetProperty("exact_match_map").GetProperty("map");
        Assert.Equal(new[] { "10.247.91.10", "10.247.92.10" }, sources.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        JsonElement aPorts = Choices(sources.GetProperty("10.247.91.10").GetProperty("matcher"));
        JsonElement bPorts = Choices(sources.GetProperty("10.247.92.10").GetProperty("matcher"));
        JsonElement aName = Choices(aPorts.GetProperty("8443").GetProperty("matcher")).GetProperty("allowed.kautuka.test");
        JsonElement bName = Choices(bPorts.GetProperty("8443").GetProperty("matcher")).GetProperty("allowed.kautuka.test");
        Assert.Equal(aName.ToString(), bName.ToString());
        Assert.False(bPorts.TryGetProperty("9443", out _));
        JsonElement literal = Choices(aPorts.GetProperty("9443").GetProperty("matcher")).GetProperty("10.247.90.10");
        Assert.NotEqual(aName.ToString(), literal.ToString());
        Assert.All(listener.GetProperty("filter_chains").EnumerateArray(), chain => Assert.False(chain.TryGetProperty("filter_chain_match", out _)));
        Assert.Equal("envoy.filters.network.direct_response", listener.GetProperty("default_filter_chain").GetProperty("filters")[0].GetProperty("name").GetString());
        JsonElement dns = Assert.Single(cds.RootElement.GetProperty("resources").EnumerateArray(), cluster => cluster.GetProperty("type").GetString() == "STRICT_DNS");
        Assert.Equal("V4_ONLY", dns.GetProperty("dns_lookup_family").GetString());
        Assert.Equal("allowed.kautuka.test", dns.GetProperty("load_assignment").GetProperty("endpoints")[0].GetProperty("lb_endpoints")[0]
            .GetProperty("endpoint").GetProperty("address").GetProperty("socket_address").GetProperty("address").GetString());
        Assert.DoesNotContain("original_dst_lb_config", config.ClusterDocument, StringComparison.Ordinal);

        // Explicit local native-proof export, never enabled by the ordinary test suite.
        string? export = Environment.GetEnvironmentVariable("KAUTUKA_TEST_EGRESS_DOCUMENTS");
        if (!string.IsNullOrWhiteSpace(export))
        {
            Directory.CreateDirectory(export);
            await File.WriteAllTextAsync(Path.Combine(export, "cds.json"), config.ClusterDocument, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(export, "lds.json"), config.ListenerDocument, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(export, "version.txt"), config.Version, TestContext.Current.CancellationToken);
            EnvoyEgressConfiguration mixed = EnvoyEgressConfiguration.Compile([
                new("10.247.91.10", "allowed.kautuka.test", 8443), new("10.247.92.10", "allowed.kautuka.test", 8443),
                new("10.247.91.10", "10.247.90.10", 9443), new("10.247.91.10", "10.247.90.10", 8443),
            ]);
            await File.WriteAllTextAsync(Path.Combine(export, "mixed-cds.json"), mixed.ClusterDocument, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(export, "mixed-lds.json"), mixed.ListenerDocument, TestContext.Current.CancellationToken);
            string? report = Environment.GetEnvironmentVariable("KAUTUKA_TEST_EGRESS_REPORT");
            if (!string.IsNullOrWhiteSpace(report))
            {
                using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
                Assert.True(observed.RootElement.GetProperty("passed").GetBoolean());
                JsonElement accepted = observed.RootElement.GetProperty("dynamicConfigDump");
                Assert.True(config.ClustersAreActive(accepted));
                Assert.True(config.ListenerIsActive(accepted));
                Assert.True(config.ListenerIsActive(observed.RootElement.GetProperty("rejectedConfigDump")));
                JsonElement mixedAccepted = observed.RootElement.GetProperty("mixedPolicyDump");
                Assert.True(mixed.ClustersAreActive(mixedAccepted));
                Assert.True(mixed.ListenerIsActive(mixedAccepted));
                Assert.False(config.ListenerIsActive(mixedAccepted));
            }
        }
    }

    [Fact]
    public void EquivalentRulesHaveStableVersionAndEmptyPolicyIsDenyOnly()
    {
        EgressTcpRule a = new("10.247.91.10", "EXAMPLE.COM.", 443);
        EgressTcpRule b = new("10.247.92.10", "example.com", 443);
        EnvoyEgressConfiguration first = EnvoyEgressConfiguration.Compile([a, b, a]);
        EnvoyEgressConfiguration second = EnvoyEgressConfiguration.Compile([b, a]);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.ListenerDocument, second.ListenerDocument);
        Assert.Equal(first.ClusterDocument, second.ClusterDocument);
        Assert.NotEqual(first.Version, EnvoyEgressConfiguration.Compile([a]).Version);
        EnvoyEgressConfiguration denied = EnvoyEgressConfiguration.Compile([]);
        using JsonDocument listener = JsonDocument.Parse(denied.ListenerDocument);
        JsonElement deny = Assert.Single(listener.RootElement.GetProperty("resources")[0].GetProperty("filter_chains").EnumerateArray());
        Assert.Equal("deny", deny.GetProperty("name").GetString());
        Assert.Equal("envoy.filters.network.direct_response", deny.GetProperty("filters")[0].GetProperty("name").GetString());
        Assert.Empty(denied.ClusterNames);
    }

    [Theory]
    [InlineData("0.0.0.0", "example.com", 443)]
    [InlineData("127.0.0.1", "example.com", 443)]
    [InlineData("::1", "example.com", 443)]
    [InlineData("224.0.0.1", "example.com", 443)]
    [InlineData("10.0.0.1", "*.example.com", 443)]
    [InlineData("10.0.0.1", "https://example.com", 443)]
    [InlineData("10.0.0.1", "example.com/path", 443)]
    [InlineData("10.0.0.1", "example.com\nother", 443)]
    [InlineData("10.0.0.1", "::1", 443)]
    [InlineData("10.0.0.1", "255.255.255.255", 443)]
    [InlineData("10.0.0.1", "example.com", 0)]
    [InlineData("10.0.0.1", "example.com", 65536)]
    public void UnrepresentableRulesAreRejected(string source, string destination, int port)
        => Assert.ThrowsAny<ArgumentException>(() => EnvoyEgressConfiguration.Compile([new(source, destination, port)]));

    [Fact]
    public void ListenerAdmissionUsesSameDumpAcceptedVersionActiveMarkerAndNoWarming()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile([new("10.0.0.1", "example.com", 443)]);
        JsonObject dump = Dump(config);
        JsonNode listenerDump = dump["configs"]![1]!;
        // This stale field was observed on real in-place Envoy updates.
        listenerDump["dynamic_listeners"]![0]!["active_state"]!["version_info"] = "previous";
        Assert.True(ListenerActive(config, dump));
        listenerDump["version_info"] = "previous";
        Assert.False(ListenerActive(config, dump));
        listenerDump["version_info"] = config.Version;
        JsonNode active = listenerDump["dynamic_listeners"]![0]!["active_state"]!;
        active["listener"]!["metadata"]!["filter_metadata"]!["kautuka.egress"]!["policy_version"] = "previous";
        Assert.False(ListenerActive(config, dump));
        active["listener"]!["metadata"]!["filter_metadata"]!["kautuka.egress"]!["policy_version"] = config.Version;
        listenerDump["dynamic_listeners"]![0]!["warming_state"] = new JsonObject { ["version_info"] = config.Version };
        Assert.False(ListenerActive(config, dump));
        listenerDump["dynamic_listeners"]![0]!.AsObject().Remove("active_state");
        Assert.False(ListenerActive(config, dump));
    }

    [Fact]
    public void ClusterAdmissionRequiresAcceptedCompleteActiveSetWithoutWarming()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile([new("10.0.0.1", "example.com", 443)]);
        JsonObject dump = Dump(config);
        Assert.True(ClustersActive(config, dump));
        JsonNode clusterDump = dump["configs"]![0]!;
        clusterDump["dynamic_warming_clusters"] = new JsonArray(new JsonObject { ["version_info"] = config.Version });
        Assert.False(ClustersActive(config, dump));
        clusterDump.AsObject().Remove("dynamic_warming_clusters");
        clusterDump["dynamic_active_clusters"]![0]!["cluster"]!["metadata"]!["filter_metadata"]!["kautuka.egress"]!["policy_version"] = "wrong";
        Assert.False(ClustersActive(config, dump));
        clusterDump["dynamic_active_clusters"] = new JsonArray();
        Assert.False(ClustersActive(config, dump));
        clusterDump["version_info"] = "previous";
        Assert.False(ClustersActive(config, dump));
    }

    private static JsonElement Choices(JsonElement matcher)
        => matcher.GetProperty("matcher_tree").GetProperty("exact_match_map").GetProperty("map");

    private static JsonObject Dump(EnvoyEgressConfiguration config) => new()
    {
        ["configs"] = new JsonArray(
            new JsonObject
            {
                ["@type"] = "type.googleapis.com/envoy.admin.v3.ClustersConfigDump",
                ["version_info"] = config.Version,
                ["dynamic_active_clusters"] = new JsonArray([.. JsonNode.Parse(config.ClusterDocument)!["resources"]!.AsArray()
                    .Select(cluster => (JsonNode)new JsonObject { ["cluster"] = cluster!.DeepClone() })]),
            },
            new JsonObject
            {
                ["@type"] = "type.googleapis.com/envoy.admin.v3.ListenersConfigDump",
                ["version_info"] = config.Version,
                ["dynamic_listeners"] = new JsonArray(new JsonObject
                {
                    ["name"] = EnvoyEgressConfiguration.ListenerName,
                    ["active_state"] = new JsonObject { ["listener"] = JsonNode.Parse(config.ListenerDocument)!["resources"]![0]!.DeepClone() },
                }),
            }),
    };

    private static bool ListenerActive(EnvoyEgressConfiguration config, JsonObject dump)
    {
        using JsonDocument document = JsonDocument.Parse(dump.ToJsonString());
        return config.ListenerIsActive(document.RootElement);
    }

    private static bool ClustersActive(EnvoyEgressConfiguration config, JsonObject dump)
    {
        using JsonDocument document = JsonDocument.Parse(dump.ToJsonString());
        return config.ClustersAreActive(document.RootElement);
    }
}
