namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    public static WebApplication MapGatewaySettingsEndpoints(this WebApplication app)
    {
        MapGatewaySettingsEndpointsInternal(app);
        return app;
    }
}
