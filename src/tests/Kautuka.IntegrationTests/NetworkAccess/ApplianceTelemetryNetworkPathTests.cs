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
        Assert.Contains("ip saddr { 10.247.0.4, 10.247.0.5, 10.247.0.7, 10.247.0.8 } ip daddr 10.247.0.2 tcp dport 15005 counter return", boundary, StringComparison.Ordinal);
        Assert.Contains("ip saddr { 10.247.0.4, 10.247.0.5, 10.247.0.7, 10.247.0.8 } ip daddr 10.247.0.2 tcp dport 15005 counter accept", boundary, StringComparison.Ordinal);
        Assert.Contains("ip daddr 10.247.0.6 tcp dport 5080 counter accept", boundary, StringComparison.Ordinal);

        Assert.DoesNotContain("appliance_telemetry", BoundaryRulesetRenderer.RenderEnvoyConfig(), StringComparison.Ordinal);
        Assert.DoesNotContain("telemetry_", BoundaryRulesetRenderer.RenderBoundaryNft(), StringComparison.Ordinal);
    }
}
