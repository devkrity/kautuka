using System.Diagnostics;

using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Kautuka.IntegrationTests;

public sealed class TelemetryTracingSourceTests
{
    [Fact]
    public async Task ConfigureKautukaTelemetryExportsGatewayAgentRunSourceActivities()
    {
        var exportedActivities = new List<Activity>();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KautukaTelemetryDefaults.TracesEndpointKey] = string.Empty,
            [KautukaTelemetryDefaults.MetricsEndpointKey] = string.Empty,
        });

        builder.AddKautukaTelemetry(
            KautukaActivitySources.ProcessKind.Gateway,
            KautukaActivitySources.Component.Gateway,
            enableTracing: true,
            configureTracing: tracing =>
                tracing.AddProcessor(new ActivityCaptureProcessor(exportedActivities)));

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
            using (Activity? activity = source.StartActivity("agent (test-agent) run [chat]"))
            {
                activity?.SetTag("kautuka.run.trigger", "chat");
                activity?.SetTag("kautuka.agent.id", "test-agent");
            }

            TracerProvider tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            tracerProvider.ForceFlush();

            Assert.Contains(exportedActivities, static activity =>
                string.Equals(activity.Source.Name, KautukaActivitySources.GatewayAgentRunSource, StringComparison.Ordinal)
                && string.Equals(activity.DisplayName, "agent (test-agent) run [chat]", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ConfigureKautukaTelemetryProjectsApprovedIdentityBaggageOntoChildSpans()
    {
        var exportedActivities = new List<Activity>();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KautukaTelemetryDefaults.TracesEndpointKey] = string.Empty,
            [KautukaTelemetryDefaults.MetricsEndpointKey] = string.Empty,
        });

        builder.AddKautukaTelemetry(
            KautukaActivitySources.ProcessKind.Gateway,
            KautukaActivitySources.Component.Gateway,
            enableTracing: true,
            configureTracing: tracing =>
                tracing.AddProcessor(new ActivityCaptureProcessor(exportedActivities)));

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            using var parentSource = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
            using var childSource = new ActivitySource("Experimental.Microsoft.Extensions.AI");
            using (Activity? parent = parentSource.StartActivity("agent run"))
            {
                parent?.AddBaggage(KautukaActivitySources.TenantIdAttribute, "tenant-1");
                parent?.AddBaggage(KautukaActivitySources.EndUserIdAttribute, "user-1");

                using Activity? child = childSource.StartActivity("model call");
            }

            TracerProvider tracerProvider = host.Services.GetRequiredService<TracerProvider>();
            tracerProvider.ForceFlush();

            Activity childActivity = Assert.Single(exportedActivities, static activity =>
                string.Equals(activity.DisplayName, "model call", StringComparison.Ordinal));
            Assert.Equal("tenant-1", childActivity.GetTagItem(KautukaActivitySources.TenantIdAttribute));
            Assert.Equal("user-1", childActivity.GetTagItem(KautukaActivitySources.EndUserIdAttribute));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private sealed class ActivityCaptureProcessor(List<Activity> exportedActivities) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            ArgumentNullException.ThrowIfNull(data);
            exportedActivities.Add(data);
        }
    }
}
