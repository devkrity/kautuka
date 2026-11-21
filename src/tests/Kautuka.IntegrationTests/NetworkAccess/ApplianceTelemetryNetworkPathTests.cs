using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ApplianceTelemetryNetworkPathTests
{
    [Fact]
    public void ApplianceTelemetryIsAnOptInIngestionOnlyRoute()
    {
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig(includeApplianceTelemetry: true);
        string boundary = BoundaryRulesetRenderer.RenderBoundaryNft(includeApplianceTelemetry: true);

        Assert.Contains("name: appliance_telemetry", envoy, StringComparison.Ordinal);
        Assert.Contains("regex: '^/api/default/v1/(logs|metrics|traces)$'", envoy, StringComparison.Ordinal);
        Assert.Contains("exact: \"POST\"", envoy, StringComparison.Ordinal);
        Assert.Contains("status: 404", envoy, StringComparison.Ordinal);
        Assert.Contains("cluster: appliance_observability", envoy, StringComparison.Ordinal);
        Assert.Contains("ip saddr { 10.247.0.4, 10.247.0.5, 10.247.0.7, 10.247.0.8, 10.247.0.9 } ip daddr 10.247.0.2 tcp dport 15005 counter return", boundary, StringComparison.Ordinal);
        Assert.Contains("ip saddr { 10.247.0.4, 10.247.0.5, 10.247.0.7, 10.247.0.8, 10.247.0.9 } ip daddr 10.247.0.2 tcp dport 15005 counter accept", boundary, StringComparison.Ordinal);
        Assert.Contains("ip daddr 10.247.0.6 tcp dport 5080 counter accept", boundary, StringComparison.Ordinal);

        Assert.DoesNotContain("appliance_telemetry", BoundaryRulesetRenderer.RenderEnvoyConfig(), StringComparison.Ordinal);
        Assert.DoesNotContain("telemetry_", BoundaryRulesetRenderer.RenderBoundaryNft(), StringComparison.Ordinal);
    }

    [Fact]
    public void EnvoySetsTheCollectorAuthorizationOnlyOnTheIngestionRoute()
    {
        const string authorization = "Zml4dHVyZQ==";
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig(includeApplianceTelemetry: true, telemetryAuthorization: authorization);

        string route = envoy[envoy.IndexOf("cluster: appliance_observability", StringComparison.Ordinal)..envoy.IndexOf("status: 404", StringComparison.Ordinal)];
        Assert.Contains("key: authorization", route, StringComparison.Ordinal);
        Assert.Contains($"value: \"Basic {authorization}\"", route, StringComparison.Ordinal);
        Assert.Contains("append_action: OVERWRITE_IF_EXISTS_OR_ADD", route, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => BoundaryRulesetRenderer.RenderEnvoyConfig(
            includeApplianceTelemetry: true, telemetryAuthorization: "bad\nvalue"));
    }
}
