using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeMemoryEndpoints(WebApplication app)
    {
        app.MapGet("/api/memories/agents/{agentId}", (string agentId, IGatewaySessionStore sessions, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Json(sessions.GetMemories("agent", agentId, user.Id), KautukaJsonContext.Default.GatewayMemoriesResponse);
        });

        app.MapDelete("/api/memories/{factId}", (string factId, IGatewaySessionStore sessions, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            bool factExists = false;
            foreach (GatewaySession session in sessions.ListRecentSessions(user.Id, 200))
            {
                GatewayMemoriesResponse candidate = sessions.GetMemories(session.TargetType, session.TargetId, user.Id);
                if (candidate.Facts.Any(item => string.Equals(item.Id, factId, StringComparison.Ordinal)))
                {
                    factExists = true;
                    break;
                }
            }

            if (!factExists)
            {
                return Results.Json(new GatewayErrorResponse("Memory fact not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            bool removed = sessions.DeleteMemoryFact(factId);
            return removed
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("Memory fact not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapMethods("/api/memories/{factId}", ["PATCH"], async (string factId, HttpContext context, IGatewaySessionStore sessions, IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            JsonElement body = await context.Request.ReadFromJsonAsync(KautukaJsonContext.Default.JsonElement, context.RequestAborted).ConfigureAwait(false);
            string title = body.GetStringProperty("title", "").Trim();
            string content = body.GetStringProperty("content", "").Trim();
            string category = body.GetStringProperty("category", "general").Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                return Results.Json(new GatewayErrorResponse("content is required"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            bool factExists = false;
            foreach (GatewaySession session in sessions.ListRecentSessions(user.Id, 200))
            {
                GatewayMemoriesResponse candidate = sessions.GetMemories(session.TargetType, session.TargetId, user.Id);
                if (candidate.Facts.Any(item => string.Equals(item.Id, factId, StringComparison.Ordinal)))
                {
                    factExists = true;
                    break;
                }
            }

            if (!factExists)
            {
                return Results.Json(new GatewayErrorResponse("Memory fact not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            bool updated = sessions.UpdateMemoryFact(factId, title, content, category);
            return updated
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("Memory fact not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });
    }
}
