using System.Diagnostics.Metrics;

using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Kautuka.IntegrationTests;

public sealed class GatewayObservabilityMetricsTests
{
    [Fact]
    public async Task ConfigureGatewayMetricsExportsMicrosoftAiMeters()
    {
        var exportedMetrics = new List<string>();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KautukaTelemetryDefaults.TracesEndpointKey] = string.Empty,
            [KautukaTelemetryDefaults.MetricsEndpointKey] = string.Empty,
        });

        builder.AddKautukaTelemetry(
            KautukaActivitySources.ProcessKind.Gateway,
            KautukaActivitySources.Component.Gateway,
            configureMetrics: metrics =>
            {
                GatewayObservabilityExtensions.ConfigureGatewayMetrics(metrics);
                metrics.AddReader(new PeriodicExportingMetricReader(new MetricNameCaptureExporter(exportedMetrics)));
            });

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            using Meter chatMeter = new(GatewayObservabilityExtensions.MicrosoftExtensionsAiMeterName);
            using Meter agentMeter = new(GatewayObservabilityExtensions.MicrosoftAgentsAiMeterName);

            Counter<long> chatCounter = chatMeter.CreateCounter<long>("test.chat.requests");
            Counter<long> agentCounter = agentMeter.CreateCounter<long>("test.agent.requests");

            chatCounter.Add(1);
            agentCounter.Add(1);

            MeterProvider meterProvider = host.Services.GetRequiredService<MeterProvider>();
            meterProvider.ForceFlush();

            Assert.Contains("test.chat.requests", exportedMetrics);
            Assert.Contains("test.agent.requests", exportedMetrics);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private sealed class MetricNameCaptureExporter(List<string> exportedMetrics) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                exportedMetrics.Add(metric.Name);
            }

            return ExportResult.Success;
        }
    }
}
