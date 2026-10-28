using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeBehaviorEndpoints(WebApplication app)
    {
        app.MapGet("/api/runtime/continuations/{checkpointId}/effective-behavior", (
            string checkpointId,
            IGatewayContinuationStore continuationStore,
            IGatewayAuthGuard auth,
            HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            GatewayContinuationCheckpoint? checkpoint = continuationStore.GetById(checkpointId);
            if (checkpoint is null)
            {
                return Results.Json(new GatewayErrorResponse("Checkpoint not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.IsSelf(user, checkpoint.UserId))
            {
                return Results.Json(new GatewayErrorResponse("Checkpoint not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (string.IsNullOrWhiteSpace(checkpoint.EffectiveBehaviorJson))
            {
                return Results.Json(new GatewayErrorResponse("Effective behavior snapshot not available for this checkpoint"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            EffectiveRunBehavior? behavior = JsonSerializer.Deserialize(checkpoint.EffectiveBehaviorJson, KautukaJsonContext.Default.EffectiveRunBehavior);
            return behavior is null
                ? Results.Json(new GatewayErrorResponse("Effective behavior snapshot is invalid"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status500InternalServerError)
                : Results.Json(behavior, KautukaJsonContext.Default.EffectiveRunBehavior);
        });
    }
}
