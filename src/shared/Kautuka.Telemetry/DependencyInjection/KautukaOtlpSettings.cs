using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;

using OpenTelemetry.Exporter;

namespace Kautuka.Telemetry.DependencyInjection;

public static class KautukaOtlpSettings
{
    public static string? ResolveLogsEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[KautukaTelemetryDefaults.LogsEndpointKey]
            ?? KautukaTelemetryDefaults.DefaultLogsEndpoint;
    }

    public static string? ResolveMetricsEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[KautukaTelemetryDefaults.MetricsEndpointKey]
            ?? KautukaTelemetryDefaults.DefaultMetricsEndpoint;
    }

    public static string? ResolveTracesEndpoint(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[KautukaTelemetryDefaults.TracesEndpointKey]
            ?? KautukaTelemetryDefaults.DefaultTracesEndpoint;
    }

    public static string ResolveProtocol(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[KautukaTelemetryDefaults.ProtocolKey]
            ?? KautukaTelemetryDefaults.DefaultOtlpProtocol;
    }

    public static string? ResolveHeaders(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration[KautukaTelemetryDefaults.HeadersKey]
            ?? KautukaTelemetryDefaults.DefaultHeaders;
    }

    public static OtlpExportProtocol ResolveExportProtocol(string protocol)
        => string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

    public static Uri? BuildSignalEndpoint(string? endpoint, OtlpExportProtocol exportProtocol, string signalPath)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        if (exportProtocol != OtlpExportProtocol.HttpProtobuf)
        {
            return new Uri(endpoint, UriKind.Absolute);
        }

        string normalizedEndpoint = endpoint.TrimEnd('/');
        if (normalizedEndpoint.EndsWith(signalPath, StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(normalizedEndpoint, UriKind.Absolute);
        }

        return new Uri(normalizedEndpoint + signalPath, UriKind.Absolute);
    }
}
