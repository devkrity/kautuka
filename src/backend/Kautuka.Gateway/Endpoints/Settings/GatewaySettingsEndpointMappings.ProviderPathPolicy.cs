using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapProviderPathPolicySettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/provider-path-policy", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(settings.GetProviderPathPolicyPayload(), KautukaJsonContext.Default.GatewayProviderPathPolicySettingsResponse);
        });

        app.MapPut("/api/settings/provider-path-policy", (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            settings.SaveProviderPathPolicySettings(body);
            return Results.Json(settings.GetProviderPathPolicyPayload(), KautukaJsonContext.Default.GatewayProviderPathPolicySettingsResponse);
        });
    }
}
