using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class EgressTelemetryConfigurationTests
{
    [Fact]
    public void HostTelemetryUsesOnlyThreeExactPostRoutesOnExistingSocket()
    {
        EnvoyEgressConfiguration config = Compile();
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        JsonElement host = Assert.Single(lds.RootElement.GetProperty("resources").EnumerateArray(), item => item.GetProperty("name").GetString() == "host-artifacts");
        Assert.Equal("/host-artifacts/connect.sock", host.GetProperty("address").GetProperty("pipe").GetProperty("path").GetString());
        JsonElement hcm = host.GetProperty("filter_chains")[0].GetProperty("filters")[0].GetProperty("typed_config");
        JsonElement telemetry = Assert.Single(hcm.GetProperty("route_config").GetProperty("virtual_hosts").EnumerateArray(), item => item.GetProperty("name").GetString() == "telemetry");
        Assert.Equal("kautuka-telemetry.internal", Assert.Single(telemetry.GetProperty("domains").EnumerateArray()).GetString());
        JsonElement[] routes = [.. telemetry.GetProperty("routes").EnumerateArray()];
        Assert.Equal(5, routes.Length);
        Assert.Equal(new[] { "/api/default/v1/logs", "/api/default/v1/metrics", "/api/default/v1/traces" }, routes.Take(3).Select(route => route.GetProperty("match").GetProperty("path").GetString()));
        Assert.All(routes.Take(3), route => Assert.Equal("POST", route.GetProperty("match").GetProperty("headers")[0].GetProperty("string_match").GetProperty("exact").GetString()));
        Assert.All(routes.Skip(3), route => Assert.Equal(403, route.GetProperty("direct_response").GetProperty("status").GetInt32()));
    }

    [Fact]
    public void TelemetryBackendIsCoreBoundAndOnlyRegisteredSourcesHaveTransparentRoute()
    {
        EnvoyEgressConfiguration config = Compile();
        using JsonDocument cds = JsonDocument.Parse(config.ClusterDocument);
        JsonElement backend = Assert.Single(cds.RootElement.GetProperty("resources").EnumerateArray());
        Assert.Equal("10.90.0.2", backend.GetProperty("upstream_bind_config").GetProperty("source_address").GetProperty("address").GetString());
        Assert.Equal("10.90.0.9", backend.GetProperty("load_assignment").GetProperty("endpoints")[0].GetProperty("lb_endpoints")[0].GetProperty("endpoint").GetProperty("address").GetProperty("socket_address").GetProperty("address").GetString());
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        JsonElement sources = lds.RootElement.GetProperty("resources")[0].GetProperty("filter_chain_matcher").GetProperty("matcher_tree").GetProperty("exact_match_map").GetProperty("map");
        Assert.Equal("10.90.0.10", Assert.Single(sources.EnumerateObject()).Name);
    }

    [Fact]
    public void DisabledTelemetryDoesNotOpenHostSocketOrCreateBackend()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile([], "10.89.0.2");
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        using JsonDocument cds = JsonDocument.Parse(config.ClusterDocument);
        Assert.Single(lds.RootElement.GetProperty("resources").EnumerateArray());
        Assert.Empty(cds.RootElement.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public void TelemetryRejectsOutsideBindAndLoopbackBackend()
    {
        Assert.Throws<ArgumentException>(() => EnvoyEgressConfiguration.Compile([], "10.89.0.2", telemetry: new("10.90.0.9", "10.89.0.2", [])));
        Assert.Throws<ArgumentException>(() => EnvoyEgressConfiguration.Compile([], "10.89.0.2", telemetry: new("127.0.0.1", "10.90.0.2", [])));
    }

    [Fact]
    public async Task DisabledTelemetryEndpointDoesNotStartPodman()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Unexpected native call"));
        string root = Path.Combine(Path.GetTempPath(), "kautuka-telemetry-disabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            Assert.False(runtime.TelemetryEnabled);
            Assert.Null(await runtime.GetCoreTelemetryEndpointAsync(TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AbsentOptionalCollectorKeepsDenyingHostListener()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile([], "10.89.0.2", telemetryEnabled: true);
        using JsonDocument lds = JsonDocument.Parse(config.ListenerDocument);
        JsonElement host = Assert.Single(lds.RootElement.GetProperty("resources").EnumerateArray(), item => item.GetProperty("name").GetString() == "host-artifacts");
        JsonElement hosts = host.GetProperty("filter_chains")[0].GetProperty("filters")[0].GetProperty("typed_config").GetProperty("route_config").GetProperty("virtual_hosts");
        Assert.Equal("deny", Assert.Single(hosts.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public void ReleasedImageWithoutEntrypointAcceptsNullButRejectsInjectedCommand()
    {
        using JsonDocument image = JsonDocument.Parse("{}");
        using JsonDocument empty = JsonDocument.Parse("{\"Entrypoint\":null}");
        using JsonDocument changed = JsonDocument.Parse("{\"Entrypoint\":[\"/bin/sh\"]}");
        Assert.True(WorkspaceSidecarSupervisor.TelemetryImageCommandMatches(empty.RootElement, image.RootElement, "Entrypoint"));
        Assert.False(WorkspaceSidecarSupervisor.TelemetryImageCommandMatches(changed.RootElement, image.RootElement, "Entrypoint"));
    }

    private static EnvoyEgressConfiguration Compile() => EnvoyEgressConfiguration.Compile([], "10.89.0.2",
        telemetry: new("10.90.0.9", "10.90.0.2", [new("10.90.0.10", "10.90.0.2")]));
}
