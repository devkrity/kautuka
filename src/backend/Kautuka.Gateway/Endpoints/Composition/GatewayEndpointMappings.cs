using Kautuka.Gateway.Endpoints.Auth;
using Kautuka.Gateway.Endpoints.Catalog;
using Kautuka.Gateway.Endpoints.Connectors;
using Kautuka.Gateway.Endpoints.Credentials;
using Kautuka.Gateway.Endpoints.Internal;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Endpoints.Settings;
using Kautuka.Gateway.Endpoints.Skills;

namespace Kautuka.Gateway.Endpoints.Composition;

internal static class GatewayEndpointMappings
{
    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapGatewayManagementEndpoints();
        app.MapGatewayAuthEndpoints();
        app.MapGatewayCatalogEndpoints();
        app.MapGatewayConnectorEndpoints();
        app.MapGatewaySkillEndpoints();
        app.MapGatewayCredentialEndpoints();
        app.MapGatewayRuntimeEndpoints();
        app.MapNaradEndpoints();
        app.MapGatewaySettingsEndpoints();
        app.MapGatewayApprovalEndpoints();

        return app;
    }
}
