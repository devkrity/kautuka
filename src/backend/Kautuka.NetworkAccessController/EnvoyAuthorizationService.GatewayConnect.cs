using System.Globalization;

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
    private static readonly string[] scopeHeaders = [UserHeader, AgentHeader, ConnectorHeader, PurposeHeader, CorrelationHeader];

    private CheckResponse CheckGatewayConnect(AttributeContext attributes)
    {
        if (!attributes.ContextExtensions.TryGetValue(GatewayConnectRouteKey, out string? route)
            || route != GatewayConnectRoute
            || attributes.Source?.Principal != GatewayPrincipal)
        {
            return CreateDenyResponse("unauthenticated_gateway");
        }

        HttpRequest? http = attributes.Request?.Http;
        if (http?.Method != "CONNECT" || !TryParseConnectAuthority(http.Host, out string host, out int port))
        {
            return CreateDenyResponse("bad_connect_authority");
        }

        if (!TryReadScopeHeader(http, UserHeader, out string? user) || user is null
            || !TryReadScopeHeader(http, AgentHeader, out string? agent)
            || !TryReadScopeHeader(http, ConnectorHeader, out string? connector)
            || !TryReadScopeHeader(http, PurposeHeader, out string? purpose)
            || !TryReadScopeHeader(http, CorrelationHeader, out string? correlation)
            || (user == "system" && ((agent is not null && agent != "gateway") || connector is not null)))
        {
            return CreateDenyResponse("invalid_gateway_scope");
        }

        NetworkAccessDecisionResult decision = controller.EvaluateGatewayConnect(
            new OutboundCheckInput(user, agent ?? string.Empty, connector, host, port, purpose, correlation));
        if (!decision.Allowed)
        {
            return CreateDenyResponse(decision.Reason);
        }

        var response = new CheckResponse { Status = new Status { Code = 0 }, OkResponse = new OkHttpResponse() };
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
