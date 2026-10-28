using System.Text;

namespace Kautuka.Telemetry.Telemetry;

public static class KautukaTelemetryDefaults
{
    /// <summary>Host port for OpenObserve when published on loopback (near WebHost 20229).</summary>
    public const int OpenObserveHostPort = 20215;

    public const string DefaultOpenObserveEndpoint = "http://127.0.0.1:20215/api/default";

    // Not the OpenObserve/ZincObserve public demo pair (root@example.com / Complexpass#123).
    // Per-install generation still needs a shared secret surface for every OTLP host; until then
    // sidecars.json must keep matching ZO_ROOT_USER_* values.
    // OpenObserve validates ZO_ROOT_USER_EMAIL against a TLD-required regex; bare @localhost is rejected
    // at bootstrap (panic: invalid email ID). Use a synthetic domain that still reads as local-only.
    public const string DefaultOpenObserveRootUserEmail = "kautuka-telemetry@kautuka.local";
    public const string DefaultOpenObserveRootUserPassword = "KautukaOo!telemetry-7f3a";

    public static readonly string DefaultOpenObserveAuthorizationHeader =
        "Authorization=Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{DefaultOpenObserveRootUserEmail}:{DefaultOpenObserveRootUserPassword}"));

    public const string SectionPrefix = "Kautuka:Telemetry";
    public const string TenantIdKey = SectionPrefix + ":TenantId";
    public const string OtlpSectionPrefix = SectionPrefix + ":Otlp";
    public const string LogsEndpointKey = OtlpSectionPrefix + ":LogsEndpoint";
    public const string TracesEndpointKey = OtlpSectionPrefix + ":TracesEndpoint";
    public const string MetricsEndpointKey = OtlpSectionPrefix + ":MetricsEndpoint";
    public const string ProtocolKey = OtlpSectionPrefix + ":Protocol";
    public const string HeadersKey = OtlpSectionPrefix + ":Headers";
    public const string DefaultLogsEndpoint = DefaultOpenObserveEndpoint + "/v1/logs";
    public const string DefaultTracesEndpoint = DefaultOpenObserveEndpoint + "/v1/traces";
    public const string DefaultMetricsEndpoint = DefaultOpenObserveEndpoint + "/v1/metrics";
    public const string DefaultOtlpProtocol = "http/protobuf";
    public static readonly string DefaultHeaders = DefaultOpenObserveAuthorizationHeader;
}
