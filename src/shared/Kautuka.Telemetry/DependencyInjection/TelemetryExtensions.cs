using System.Diagnostics;

using Kautuka.Telemetry.Telemetry;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kautuka.Telemetry.DependencyInjection;

public static class KautukaTelemetryExtensions
{
    private const string healthEndpointPath = "/health";
    private const string alivenessEndpointPath = "/alive";
    private static readonly string[] tracingSourceNames =
    [
        "Experimental.Microsoft.Extensions.AI",
        "Experimental.Microsoft.Agents.AI",
        KautukaActivitySources.GatewayAgentRunSource,
    ];
    private static readonly object telemetryProcessKindKey = new();
    private static readonly object telemetryComponentKey = new();

    public static TBuilder AddKautukaTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureKautukaTelemetry();
        builder.AddDefaultHealthChecks();
        return builder;
    }

    public static TBuilder AddKautukaTelemetry<TBuilder>(
        this TBuilder builder,
        string processKind,
        string component,
        Action<MeterProviderBuilder>? configureMetrics = null,
        bool enableTracing = false,
        Action<TracerProviderBuilder>? configureTracing = null) where TBuilder : IHostApplicationBuilder
    {
        builder.Properties[telemetryProcessKindKey] = processKind;
        builder.Properties[telemetryComponentKey] = component;
        builder.ConfigureKautukaTelemetry(configureMetrics, enableTracing, configureTracing);
        builder.AddDefaultHealthChecks();

        return builder;
    }

    public static TBuilder ConfigureKautukaTelemetry<TBuilder>(
        this TBuilder builder,
        Action<MeterProviderBuilder>? configureMetrics = null,
        bool enableTracing = false,
        Action<TracerProviderBuilder>? configureTracing = null) where TBuilder : IHostApplicationBuilder
    {
        string protocol = KautukaOtlpSettings.ResolveProtocol(builder.Configuration);
        OtlpExportProtocol exportProtocol = KautukaOtlpSettings.ResolveExportProtocol(protocol);
        Uri? tracesEndpoint = KautukaOtlpSettings.BuildSignalEndpoint(
            KautukaOtlpSettings.ResolveTracesEndpoint(builder.Configuration),
            exportProtocol,
            "/v1/traces");
        Uri? metricsEndpoint = KautukaOtlpSettings.BuildSignalEndpoint(
            KautukaOtlpSettings.ResolveMetricsEndpoint(builder.Configuration),
            exportProtocol,
            "/v1/metrics");
        string? headers = KautukaOtlpSettings.ResolveHeaders(builder.Configuration);
        string? serviceName = builder.Configuration[$"{KautukaTelemetryDefaults.SectionPrefix}:ServiceName"];

        builder.Services.TryAddSingleton(new TelemetryDiagnosticsState(
            enableTracing,
            protocol,
            exportProtocol,
            tracesEndpoint,
            metricsEndpoint,
            headers,
            tracingSourceNames));
        builder.Services.AddSingleton<IHostedService>(sp =>
            new TelemetryDiagnosticsHostedService(
                sp.GetRequiredService<ILogger<TelemetryDiagnosticsHostedService>>(),
                sp.GetRequiredService<TelemetryDiagnosticsState>()));

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    resource.AddService(serviceName);
                }

                resource.AddAttributes(
                [
                    new KeyValuePair<string, object>(KautukaActivitySources.ProcessKindAttribute, builder.GetTelemetryProcessKind()),
                    new KeyValuePair<string, object>(KautukaActivitySources.ComponentAttribute, builder.GetTelemetryComponent()),
                ]);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddRuntimeInstrumentation();

                if (metricsEndpoint is not null)
                {
                    metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = metricsEndpoint;
                        options.Protocol = exportProtocol;
                        options.Headers = string.IsNullOrWhiteSpace(headers) ? null : headers;
                    });
                }

                configureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                if (!enableTracing)
                {
                    return;
                }

                foreach (string sourceName in tracingSourceNames)
                {
                    tracing.AddSource(sourceName);
                }

                tracing.AddProcessor(new KautukaIdentityBaggageProcessor());

                if (tracesEndpoint is not null)
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = tracesEndpoint;
                        options.Protocol = exportProtocol;
                        options.Headers = string.IsNullOrWhiteSpace(headers) ? null : headers;
                    });
                }

                configureTracing?.Invoke(tracing);
            });

        return builder;
    }

    private static string GetTelemetryProcessKind<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        => builder.Properties.TryGetValue(telemetryProcessKindKey, out object? value) && value is string processKind
            ? processKind
            : string.Empty;

    private static string GetTelemetryComponent<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
        => builder.Properties.TryGetValue(telemetryComponentKey, out object? value) && value is string component
            ? component
            : string.Empty;

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(healthEndpointPath);
        app.MapHealthChecks(alivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        });

        return app;
    }

    private sealed record TelemetryDiagnosticsState(
        bool EnableTracing,
        string Protocol,
        OtlpExportProtocol ExportProtocol,
        Uri? TracesEndpoint,
        Uri? MetricsEndpoint,
        string? Headers,
        IReadOnlyList<string> TracingSourceNames);

    private sealed class TelemetryDiagnosticsHostedService(
        ILogger<TelemetryDiagnosticsHostedService> logger,
        TelemetryDiagnosticsState state) : IHostedService, IDisposable
    {
        private static readonly Action<ILogger, bool, string, string, string, bool, string, Exception?> logTelemetryDiagnostics =
            LoggerMessage.Define<bool, string, string, string, bool, string>(
                LogLevel.Information,
                new EventId(700, nameof(TelemetryDiagnosticsHostedService)),
                "Telemetry diagnostics: TracingEnabled={TracingEnabled}, Protocol={Protocol}, TracesEndpoint={TracesEndpoint}, MetricsEndpoint={MetricsEndpoint}, HeadersConfigured={HeadersConfigured}, TraceSources={TraceSources}");

        private static readonly Action<ILogger, string, string, string, Exception?> logActivityStarted =
            LoggerMessage.Define<string, string, string>(
                LogLevel.Information,
                new EventId(701, nameof(TelemetryDiagnosticsHostedService)),
                "Telemetry diagnostics activity started. Source={Source}, Name={Name}, TraceId={TraceId}");

        private static readonly Action<ILogger, string, string, string, Exception?> logActivityStopped =
            LoggerMessage.Define<string, string, string>(
                LogLevel.Information,
                new EventId(702, nameof(TelemetryDiagnosticsHostedService)),
                "Telemetry diagnostics activity stopped. Source={Source}, Name={Name}, TraceId={TraceId}");

        private ActivityListener? activityListener;
        private int startedLogCount;
        private int stoppedLogCount;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!logger.IsEnabled(LogLevel.Information))
            {
                return Task.CompletedTask;
            }

            logTelemetryDiagnostics(
                logger,
                state.EnableTracing,
                $"{state.Protocol}/{state.ExportProtocol}",
                state.TracesEndpoint?.AbsoluteUri ?? "<null>",
                state.MetricsEndpoint?.AbsoluteUri ?? "<null>",
                !string.IsNullOrWhiteSpace(state.Headers),
                string.Join(",", state.TracingSourceNames),
                null);

            activityListener = new ActivityListener
            {
                ShouldListenTo = source => state.TracingSourceNames.Contains(source.Name, StringComparer.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity =>
                {
                    if (Interlocked.Increment(ref startedLogCount) <= 8)
                    {
                        logActivityStarted(
                            logger,
                            activity.Source.Name,
                            activity.OperationName,
                            activity.TraceId.ToString(),
                            null);
                    }
                },
                ActivityStopped = activity =>
                {
                    if (Interlocked.Increment(ref stoppedLogCount) <= 8)
                    {
                        logActivityStopped(
                            logger,
                            activity.Source.Name,
                            activity.OperationName,
                            activity.TraceId.ToString(),
                            null);
                    }
                }
            };

            ActivitySource.AddActivityListener(activityListener);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            activityListener?.Dispose();
            activityListener = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            activityListener?.Dispose();
            activityListener = null;
        }
    }
}
