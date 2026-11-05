using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapSystemSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/system/authentication-broker", GetAuthenticationBrokerSettings);
        app.MapPut("/api/settings/system/authentication-broker", SaveAuthenticationBrokerSettings);
        app.MapGet("/api/settings/system", async (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(await settings.GetEffectiveSettingsAsync(ct).ConfigureAwait(false), KautukaJsonContext.Default.GatewaySystemSettingsResponse);
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
            return Results.Json(await settings.GetEffectiveSettingsAsync(ct).ConfigureAwait(false), KautukaJsonContext.Default.GatewaySystemSettingsResponse);
        });

    }

    private static async Task<IResult> GetAuthenticationBrokerSettings(BrokerSettingsCoordinator broker, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct)
    {
        IResult? unauthorized = auth.RequireAdmin(context, out _);
        return unauthorized ?? Results.Json(await broker.ReadAsync(ct).ConfigureAwait(false), KautukaJsonContext.Default.AuthenticationBrokerSettingsResponse);
    }

    private static async Task<IResult> SaveAuthenticationBrokerSettings(AuthenticationBrokerSettings? body, BrokerSettingsCoordinator broker, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct)
    {
        IResult? unauthorized = auth.RequireAdmin(context, out _);
        if (unauthorized is not null) return unauthorized;
        if (body is null) return Results.BadRequest();
        try { return Results.Json(await broker.SaveAsync(body, ct).ConfigureAwait(false), KautukaJsonContext.Default.AuthenticationBrokerSettingsResponse); }
        catch (ArgumentException e) { return Results.Json(new GatewayErrorResponse(e.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: 400); }
        catch (InvalidOperationException e) { return Results.Json(new GatewayErrorResponse(e.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: 409); }
        catch (Exception e) when (e is Grpc.Core.RpcException or IOException or System.Security.Cryptography.CryptographicException or Microsoft.Data.Sqlite.SqliteException)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
