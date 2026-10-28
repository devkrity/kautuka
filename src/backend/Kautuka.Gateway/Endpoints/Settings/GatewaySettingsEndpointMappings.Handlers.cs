namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapGatewaySettingsEndpointsInternal(WebApplication app)
    {
        MapAdvancedSettingsEndpoints(app);
        MapSystemSettingsEndpoints(app);
        MapProviderPathPolicySettingsEndpoints(app);
        MapMemorySettingsEndpoints(app);
        MapProviderSettingsEndpoints(app);
        MapModelSettingsEndpoints(app);
        MapAgentSettingsEndpoints(app);
        MapPersonaSettingsEndpoints(app);
        MapUserSettingsEndpoints(app);
        MapOrganizationEndpoints(app);
    }
}
