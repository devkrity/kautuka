using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapMemorySettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/memory", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(settings.GetMemorySettingsPayload(), KautukaJsonContext.Default.GatewayMemorySettingsResponse);
        });

        app.MapPut("/api/settings/memory", (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            settings.SaveMemorySettings(body);
            return Results.Json(settings.GetMemorySettingsPayload(), KautukaJsonContext.Default.GatewayMemorySettingsResponse);
        });
    }
}
