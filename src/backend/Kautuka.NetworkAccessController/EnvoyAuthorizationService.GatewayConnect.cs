using System.Globalization;

using Google.Protobuf.WellKnownTypes;

using Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3;

namespace Kautuka.NetworkAccessController;

public sealed partial class EnvoyAuthorizationService
{
    private const string GatewayConnectRouteKey = "kautuka.authorization";
    private const string GatewayConnectRoute = "gateway-connect";
    private const string GatewayPrincipal = "spiffe://kautuka.internal/gateway";
    private const string UserHeader = "x-kautuka-user";
    private const string AgentHeader = "x-kautuka-agent";
    private const string ConnectorHeader = "x-kautuka-connector";
    private const string PurposeHeader = "x-kautuka-purpose";
    private const string CorrelationHeader = "x-kautuka-correlation";
    private const string PublicOnlyHeader = "x-kautuka-public-only";
    private const string EgressClassKey = "egress_class";
    private static readonly string[] scopeHeaders = [UserHeader, AgentHeader, ConnectorHeader, PurposeHeader, CorrelationHeader, PublicOnlyHeader];

    private CheckResponse CheckGatewayConnect(AttributeContext attributes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!attributes.ContextExtensions.TryGetValue(GatewayConnectRouteKey, out string? route)
            || route != GatewayConnectRoute
            || attributes.Source?.Principal != GatewayPrincipal)
        {
            return CreateDenyResponse("unauthenticated_gateway");
        }

        HttpRequest? http = attributes.Request?.Http;
        bool validAuthority = TryParseConnectAuthority(http?.Host ?? string.Empty, out string host, out int port);
        if (!validAuthority || http?.Method != "CONNECT")
        {
            return CreateDenyResponse("bad_connect_authority");
        }

        if (!TryReadScopeHeader(http, UserHeader, out string? user) || user is null
            || !TryReadScopeHeader(http, AgentHeader, out string? agent)
            || !TryReadScopeHeader(http, ConnectorHeader, out string? connector)
            || !TryReadScopeHeader(http, PurposeHeader, out string? purpose)
            || !TryReadScopeHeader(http, CorrelationHeader, out string? correlation)
            || !TryReadScopeHeader(http, PublicOnlyHeader, out string? publicOnlyValue)
            || publicOnlyValue is not (null or "true")
            || (user == "system" && ((agent is not null && agent != "gateway") || connector is not null)))
        {
            return CreateDenyResponse("invalid_gateway_scope");
        }

        bool publicOnly = publicOnlyValue is not null;
        var input = new OutboundCheckInput(user, agent ?? string.Empty, connector, host, port, purpose, correlation);
        NetworkAccessDecisionResult decision = controller.EvaluateGatewayConnect(input, publicOnly);
        if (!decision.Allowed)
        {
            return CreateDenyResponse(decision.Reason);
        }

        var metadata = new Struct();
        metadata.Fields[EgressClassKey] = Value.ForString(decision.AllowLan && !publicOnly ? "lan" : "public");
        var response = new CheckResponse
        {
            Status = new Status { Code = 0 },
            OkResponse = new OkHttpResponse(),
            DynamicMetadata = metadata,
        };
        response.OkResponse.HeadersToRemove.AddRange(scopeHeaders);
        return response;
    }

    private static bool TryReadScopeHeader(HttpRequest http, string name, out string? value)
    {
        if (!http.Headers.TryGetValue(name, out value))
        {
            return true;
        }

        // Envoy combines duplicate headers with commas. Reject ambiguous values.
        return value.Length is > 0 and <= 256 && value.All(static ch => ch is >= '!' and <= '~' and not ',');
    }

    private static bool TryParseConnectAuthority(string authority, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        int colon = authority.LastIndexOf(':');
        if (colon is < 1 or > 253 || authority.Length - colon is < 2 or > 6
            || !int.TryParse(authority.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        string name = authority[..colon];
        return (NetworkDestinationNormalizer.TryNormalizeIpv4(name, out host, out _)
                || NetworkDestinationNormalizer.TryNormalizeHostname(name, out host, out _))
            && string.Equals(authority, string.Create(CultureInfo.InvariantCulture, $"{host}:{port}"), StringComparison.Ordinal);
    }
}
