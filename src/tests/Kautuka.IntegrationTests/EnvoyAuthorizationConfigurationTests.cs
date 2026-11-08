using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class EnvoyAuthorizationConfigurationTests
{
    [Fact]
    public void ExternalChainsAndUnknownDestinationsAreGatedWithoutChangingInternalRoutes()
    {
        const string generation = "generation-123";
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile(
            [new("10.10.0.2", "example.com", 443), new("10.10.0.2", "10.20.0.5", 8443, true)],
            "10.89.0.2", [new("10.10.0.2", "10.10.0.1", "10.11.0.2", "10.11.0.1")],
            privateSourceAddress: "10.89.0.3", authorizationGeneration: generation);
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        using JsonDocument cds = JsonDocument.Parse(config.ClusterDocument);
        JsonElement listener = lds.RootElement.GetProperty("resources")[0];
        Assert.False(listener.GetProperty("continue_on_listener_filters_timeout").GetBoolean());
        JsonElement[] chains = [.. listener.GetProperty("filter_chains").EnumerateArray()];
        foreach (JsonElement chain in chains.Where(c => c.GetProperty("name").GetString()!.StartsWith("upstream-", StringComparison.Ordinal)))
        {
            JsonElement filters = chain.GetProperty("filters");
            Assert.Equal("envoy.filters.network.ext_authz", filters[0].GetProperty("name").GetString());
            Assert.Equal("envoy.filters.network.tcp_proxy", filters[1].GetProperty("name").GetString());
            Assert.Equal("ON_DOWNSTREAM_DATA", filters[1].GetProperty("typed_config").GetProperty("upstream_connect_mode").GetString());
            Assert.Equal(8192, filters[1].GetProperty("typed_config").GetProperty("max_early_data_bytes").GetInt32());
            JsonElement auth = filters[0].GetProperty("typed_config");
            Assert.False(auth.GetProperty("failure_mode_allow").GetBoolean());
            Assert.Equal(generation, auth.GetProperty("grpc_service").GetProperty("initial_metadata")[0].GetProperty("value").GetString());
        }
        JsonElement package = Assert.Single(chains, c => c.GetProperty("name").GetString()!.StartsWith("package-", StringComparison.Ordinal));
        Assert.Equal("envoy.filters.network.tcp_proxy", package.GetProperty("filters")[0].GetProperty("name").GetString());
        JsonElement deny = listener.GetProperty("default_filter_chain").GetProperty("filters");
        Assert.Equal("envoy.filters.network.ext_authz", deny[0].GetProperty("name").GetString());
        JsonElement proxy = deny[1].GetProperty("typed_config");
        Assert.Equal("authorization-deny-empty", proxy.GetProperty("cluster").GetString());
        Assert.Equal("ON_DOWNSTREAM_DATA", proxy.GetProperty("upstream_connect_mode").GetString());
        Assert.Equal(8192, proxy.GetProperty("max_early_data_bytes").GetInt32());
        JsonElement[] clusters = [.. cds.RootElement.GetProperty("resources").EnumerateArray()];
        JsonElement empty = Assert.Single(clusters, c => c.GetProperty("name").GetString() == "authorization-deny-empty");
        Assert.Empty(empty.GetProperty("load_assignment").GetProperty("endpoints").EnumerateArray());
        JsonElement authCluster = Assert.Single(clusters, c => c.GetProperty("name").GetString() == "manager-authorization");
        Assert.Equal("/manager-authorization/check.sock", authCluster.GetProperty("load_assignment").GetProperty("endpoints")[0].GetProperty("lb_endpoints")[0].GetProperty("endpoint").GetProperty("address").GetProperty("pipe").GetProperty("path").GetString());
        JsonElement privateCluster = Assert.Single(clusters, c => c.GetProperty("name").GetString()!.StartsWith("upstream-", StringComparison.Ordinal) && c.GetProperty("type").GetString() == "STATIC");
        Assert.Equal("10.89.0.3", privateCluster.GetProperty("upstream_bind_config").GetProperty("source_address").GetProperty("address").GetString());
    }
}
