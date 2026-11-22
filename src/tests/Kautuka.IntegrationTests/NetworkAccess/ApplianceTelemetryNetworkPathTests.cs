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
    public void EnvoyLoadsCollectorAuthorizationFromThePrivateFile()
    {
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig(includeApplianceTelemetry: true);

        int listenerStart = envoy.IndexOf("name: appliance_telemetry", StringComparison.Ordinal);
        string listener = envoy[listenerStart..envoy.IndexOf("  secrets:", listenerStart, StringComparison.Ordinal)];
        Assert.Contains("envoy.filters.http.credential_injector", listener, StringComparison.Ordinal);
        Assert.Contains("overwrite: true", listener, StringComparison.Ordinal);
        Assert.Contains("name: appliance_otlp_ingestion", listener, StringComparison.Ordinal);
        Assert.Contains("header_value_prefix: \"Basic \"", listener, StringComparison.Ordinal);
        Assert.Contains("filename: /etc/kautuka-envoy/otlp-ingestion", envoy, StringComparison.Ordinal);
        string secret = envoy[envoy.IndexOf("  secrets:", listenerStart, StringComparison.Ordinal)..envoy.IndexOf("  clusters:", listenerStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("inline_string:", secret, StringComparison.Ordinal);
        Assert.DoesNotContain("appliance_otlp_ingestion", BoundaryRulesetRenderer.RenderEnvoyConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedIngestionCredentialInvalidatesTheLoadedConfigurationHash()
    {
        Assert.NotEqual(
            EgressTopologyManager.ComputeReconcileConfigurationHash("rules", "envoy", "dns", "original"),
            EgressTopologyManager.ComputeReconcileConfigurationHash("rules", "envoy", "dns", "replacement"));
    }
}
