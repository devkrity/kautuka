using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapAdvancedSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/advanced", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(
                settings.GetAdvancedSettingsPayload(),
                KautukaJsonContext.Default.GatewayAdvancedSettingsResponse);
        });

        app.MapPut("/api/settings/advanced", async (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            try
            {
                await settings.SaveAdvancedSettingsAsync(body, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new GatewayErrorResponse(ex.Message));
            }

            return Results.Json(
                settings.GetAdvancedSettingsPayload(),
                KautukaJsonContext.Default.GatewayAdvancedSettingsResponse);
        });
    }
}
