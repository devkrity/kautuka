namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    public static WebApplication MapGatewayRuntimeEndpoints(this WebApplication app)
    {
        MapGatewayRuntimeSessionEndpoints(app);
        MapGatewayRuntimeAssetEndpoints(app);
        MapGatewayRuntimeAgentFileEndpoints(app);
        MapGatewayRuntimeMemoryEndpoints(app);
        MapGatewayRuntimeUsageEndpoints(app);
        MapGatewayRuntimeAutopilotEndpoints(app);
        MapGatewayRuntimeWorkflowEndpoints(app);
        MapGatewayRuntimeBehaviorEndpoints(app);
        MapGatewayRuntimeSystemEndpoints(app);

        return app;
    }
}
