using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapOrganizationEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/organization", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out _))
            {
                return Results.Unauthorized();
            }

            return Results.Json(settings.GetOrganizationPayload(), KautukaJsonContext.Default.GatewayOrganizationSettingsResponse);
        });

        app.MapPut("/api/settings/organization", (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            settings.SaveOrganization(body);
            return Results.Json(settings.GetOrganizationPayload(), KautukaJsonContext.Default.GatewayOrganizationSettingsResponse);
        });
    }
}
