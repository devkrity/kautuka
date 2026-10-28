using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapSystemSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/system", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(settings.GetPayload(), KautukaJsonContext.Default.GatewaySystemSettingsResponse);
        });

        app.MapGet("/api/settings/runtime-limits", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out _))
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(settings.GetPayload().System.RuntimeLimits, KautukaJsonContext.Default.GatewayRuntimeLimitsSettingsPayload);
        });

        app.MapPut("/api/settings/system", async (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            await settings.SaveAsync(body, ct).ConfigureAwait(false);
            return Results.Json(settings.GetPayload(), KautukaJsonContext.Default.GatewaySystemSettingsResponse);
        });

    }
}
