using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapModelSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/models", (IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out _))
            {
                return Results.Unauthorized();
            }

            return Results.Json(
                new GatewayModelsResponse([..
                    catalog.ListModels().Select(model => new GatewayModelListItem(
                        model.Id,
                        model.ProviderId,
                        model.ModelName,
                        model.Settings,
                        model.Capabilities,
                        model.CreatedAt))]),
                KautukaJsonContext.Default.GatewayModelsResponse);
        });

        app.MapPost("/api/settings/models/discover", async (JsonElement body, IModelProbeService modelProbeService, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(
                new GatewayDiscoveredModelsResponse([..
                    (await modelProbeService.DiscoverModelsAsync(body.GetStringProperty("providerId", "")).ConfigureAwait(false))
                        .Select(model => new GatewayDiscoveredModelItem(
                            model.Id,
                            model.DisplayName,
                            model.Description,
                            model.SupportsChat,
                            model.SupportsEmbeddings,
                            model.CapabilityTags))]),
                KautukaJsonContext.Default.GatewayDiscoveredModelsResponse);
        })
        .Produces<GatewayDiscoveredModelsResponse>(StatusCodes.Status200OK);

        app.MapPost("/api/settings/models/probe-candidate", async (JsonElement body, IModelProbeService probeService, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayModelCapabilities capabilities = await probeService.ProbeModelCandidateAsync(
                body.GetStringProperty("providerId", string.Empty),
                body.GetStringProperty("modelName", string.Empty)).ConfigureAwait(false);
            string? warningMessage = capabilities.AgentConversations is false
                ? "This model may not work for agent conversations"
                : null;

            return Results.Json(
                new GatewayModelCandidateProbeResponse(capabilities, warningMessage),
                KautukaJsonContext.Default.GatewayModelCandidateProbeResponse);
        })
        .Produces<GatewayModelCandidateProbeResponse>(StatusCodes.Status200OK);

        app.MapPost("/api/settings/models/save", (JsonElement body, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayModel model = catalog.SaveModel(body);
            return Results.Json(
                new GatewayModelResponse(new GatewayModelListItem(
                    model.Id,
                    model.ProviderId,
                    model.ModelName,
                    model.Settings,
                    model.Capabilities,
                    model.CreatedAt)),
                KautukaJsonContext.Default.GatewayModelResponse);
        });

        app.MapPost("/api/settings/models/{modelId}/probe", async (string modelId, IModelProbeService probeService, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayModelCapabilities? capabilities = await probeService.ProbeModelAsync(modelId).ConfigureAwait(false);
            return capabilities is null
                ? Results.Json(new GatewayErrorResponse("Model not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(
                    new GatewayModelCapabilitiesResponse(capabilities),
                    KautukaJsonContext.Default.GatewayModelCapabilitiesResponse);
        });

        app.MapDelete("/api/settings/models/{modelId}", (string modelId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return catalog.DeleteModel(modelId)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("Model not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });
    }
}
