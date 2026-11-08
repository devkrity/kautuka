using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;

namespace Kautuka.Telemetry.DependencyInjection;

/// <summary>The shared host exporter contract for Manager and UpdateService.</summary>
public static class ManagedHostTelemetryConfiguration
{
    public static string AuthorizationFilePath(string sharedStateRoot)
        => Path.Combine(Path.GetFullPath(sharedStateRoot), "telemetry", "exporter", "ingestion.authorization");

    public static void Apply(ConfigurationManager configuration, string sharedStateRoot, string managerStateRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration[KautukaTelemetryDefaults.LogsEndpointKey] = KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/logs";
        configuration[KautukaTelemetryDefaults.MetricsEndpointKey] = KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/metrics";
        configuration[KautukaTelemetryDefaults.TracesEndpointKey] = KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/traces";
        configuration[KautukaTelemetryDefaults.ProtocolKey] = "http/protobuf";
        configuration[KautukaTelemetryDefaults.HeadersKey] = string.Empty;
        configuration[KautukaTelemetryDefaults.UnixSocketPathKey] = Path.Combine(Path.GetFullPath(managerStateRoot), "egress", "host", "connect.sock");
        configuration[KautukaTelemetryDefaults.AuthorizationHeaderFileKey] = AuthorizationFilePath(sharedStateRoot);
    }
}
