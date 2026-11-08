namespace Kautuka.Telemetry.Telemetry;

public static class KautukaTelemetryDefaults
{
    public const string ManagedHostAuthority = "kautuka-telemetry.internal";
    public const string ManagedHostEndpoint = "http://" + ManagedHostAuthority + "/api/default";
    /// <summary>Host port for OpenObserve when published on loopback (near WebHost 20229).</summary>
    public const int OpenObserveHostPort = 20215;

    public const string DefaultOpenObserveEndpoint = "http://127.0.0.1:20215/api/default";

    public const string SectionPrefix = "Kautuka:Telemetry";
    public const string TenantIdKey = SectionPrefix + ":TenantId";
    public const string OtlpSectionPrefix = SectionPrefix + ":Otlp";
    public const string LogsEndpointKey = OtlpSectionPrefix + ":LogsEndpoint";
    public const string TracesEndpointKey = OtlpSectionPrefix + ":TracesEndpoint";
    public const string MetricsEndpointKey = OtlpSectionPrefix + ":MetricsEndpoint";
    public const string ProtocolKey = OtlpSectionPrefix + ":Protocol";
    public const string HeadersKey = OtlpSectionPrefix + ":Headers";
    public const string UnixSocketPathKey = OtlpSectionPrefix + ":UnixSocketPath";
    public const string AuthorizationHeaderFileKey = OtlpSectionPrefix + ":AuthorizationHeaderFile";
    public const string DefaultLogsEndpoint = DefaultOpenObserveEndpoint + "/v1/logs";
    public const string DefaultTracesEndpoint = DefaultOpenObserveEndpoint + "/v1/traces";
    public const string DefaultMetricsEndpoint = DefaultOpenObserveEndpoint + "/v1/metrics";
    public const string DefaultOtlpProtocol = "http/protobuf";
    public static readonly string? DefaultHeaders;
}
