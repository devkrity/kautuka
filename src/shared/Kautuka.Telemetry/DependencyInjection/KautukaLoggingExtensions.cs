using Kautuka.Telemetry.Telemetry;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace Kautuka.Telemetry.DependencyInjection;

public static class KautukaHostLoggingExtensions
{
    public static KautukaLogLevelSwitch AddKautukaLogging(this WebApplicationBuilder builder, LogLevel defaultMinimumLevel, string? serviceNameOverride = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? serviceName = builder.Configuration[$"{KautukaTelemetryDefaults.SectionPrefix}:ServiceName"]
            ?? serviceNameOverride;

        string? otlpEndpoint = KautukaOtlpSettings.ResolveLogsEndpoint(builder.Configuration);
        string protocol = KautukaOtlpSettings.ResolveProtocol(builder.Configuration);
        string? headers = KautukaOtlpSettings.ResolveHeaders(builder.Configuration);

        return AddKautukaLoggingCore(builder.Logging, builder.Services, defaultMinimumLevel, serviceName, otlpEndpoint, protocol, headers);
    }

    public static KautukaLogLevelSwitch AddKautukaLogging(this ILoggingBuilder builder, LogLevel defaultMinimumLevel, string? serviceName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string? otlpEndpoint = KautukaTelemetryDefaults.DefaultLogsEndpoint;
        string protocol = KautukaTelemetryDefaults.DefaultOtlpProtocol;
        string? headers = KautukaTelemetryDefaults.DefaultHeaders;
        string? resolvedServiceName = serviceName;

        return AddKautukaLoggingCore(builder, builder.Services, defaultMinimumLevel, resolvedServiceName, otlpEndpoint, protocol, headers);
    }

    private static KautukaLogLevelSwitch AddKautukaLoggingCore(
        ILoggingBuilder builder,
        IServiceCollection services,
        LogLevel defaultMinimumLevel,
        string? serviceName,
        string? otlpEndpoint,
        string protocol,
        string? headers)
    {
        KautukaLogLevelSwitch logLevelSwitch = new(defaultMinimumLevel);
        services.AddSingleton(logLevelSwitch);

        builder.ClearProviders();
        builder.AddConsole();
        builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.AddFilter("Grpc", LogLevel.Warning);
        builder.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Warning);
        builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        builder.AddFilter((_, level) => level >= logLevelSwitch.MinimumLevel);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            OtlpExportProtocol exportProtocol = KautukaOtlpSettings.ResolveExportProtocol(protocol);
            Uri? logsEndpoint = KautukaOtlpSettings.BuildSignalEndpoint(otlpEndpoint, exportProtocol, "/v1/logs");

            if (logsEndpoint is null)
            {
                return logLevelSwitch;
            }

            builder.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                if (!string.IsNullOrWhiteSpace(serviceName))
                {
                    options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
                }
                options.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = logsEndpoint;
                    exporter.Protocol = exportProtocol;
                    exporter.Headers = string.IsNullOrWhiteSpace(headers) ? null : headers;
                });
            });
        }

        return logLevelSwitch;
    }
}
