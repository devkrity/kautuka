using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Endpoints.Settings;

internal sealed record NetworkAccessApprovalLimitsDto(int Global, int PerUser);

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapSystemSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/system/channels/address", IResult (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            return unauthorized ?? Results.Json(new JsonObject { ["address"] = ChannelPublicAddress.Read(settings) }, KautukaJsonContext.Default.JsonObject);
        });
        app.MapPut("/api/settings/system/channels/address", async Task<IResult> (HttpContext context, IGatewaySettingsStore settings, IGatewayAuthGuard auth, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null) return unauthorized;
            JsonObject? body = await context.Request.ReadFromJsonAsync(KautukaJsonContext.Default.JsonObject, ct).ConfigureAwait(false);
            if (body is null || (body["address"] is not null && (body["address"] is not JsonValue value || !value.TryGetValue<string>(out _)))) return Results.BadRequest();
            string? address = body["address"]?.GetValue<string>();
            if (!ChannelPublicAddress.TryNormalize(address, out string? normalized)) return Results.BadRequest();
            var saved = new JsonObject { ["address"] = normalized };
            settings.WriteSettingJson(ChannelPublicAddress.SettingKey, saved.ToJsonString());
            return Results.Json(saved, KautukaJsonContext.Default.JsonObject);
        });
        app.MapGet("/api/settings/system/network-access", async (IManagerGrpcService manager, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? user);
            if (unauthorized is not null || user is null) return unauthorized ?? Results.Unauthorized();
            try
            {
                var result = await manager.Client.GetApprovalLimitsAsync(new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true }, cancellationToken: ct);
                return Results.Json(new NetworkAccessApprovalLimitsDto(result.Global, result.PerUser), KautukaJsonContext.Default.NetworkAccessApprovalLimitsDto);
            }
            catch (Grpc.Core.RpcException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapPut("/api/settings/system/network-access", async (NetworkAccessApprovalLimitsDto body, IManagerGrpcService manager, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? user);
            if (unauthorized is not null || user is null) return unauthorized ?? Results.Unauthorized();
            if (body.Global < 1 || body.PerUser < 1 || body.PerUser > body.Global) return Results.BadRequest();
            try
            {
                var result = await manager.Client.SetApprovalLimitsAsync(new NetworkAccessSetApprovalLimitsRequest { Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true }, Global = body.Global, PerUser = body.PerUser }, cancellationToken: ct);
                return Results.Json(new NetworkAccessApprovalLimitsDto(result.Global, result.PerUser), KautukaJsonContext.Default.NetworkAccessApprovalLimitsDto);
            }
            catch (Grpc.Core.RpcException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
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
