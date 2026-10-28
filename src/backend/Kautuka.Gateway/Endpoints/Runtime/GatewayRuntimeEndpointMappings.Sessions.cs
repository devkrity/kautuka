using System.Text.Json;

using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeSessionEndpoints(WebApplication app)
    {
        app.MapPost("/api/sessions", async (JsonElement body, ISessionLifecycleService sessionLifecycleService, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            string targetType = body.GetStringProperty("targetType", "agent");
            string targetId = body.GetStringProperty("targetId", "");
            if (string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
            {
                if (!catalog.TryGetAgent(targetId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
                {
                    return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            string? source = body.GetStringProperty("source", "").Trim() is var parsedSource && string.IsNullOrWhiteSpace(parsedSource) ? null : parsedSource;
            GatewaySession session = await sessionLifecycleService.CreateSessionAsync(targetType, targetId, user.Id, cancellationToken, source).ConfigureAwait(false);
            return Results.Json(session, KautukaJsonContext.Default.GatewaySession);
        });

        app.MapGet("/api/sessions/recent", (int? limit, IGatewaySessionStore sessions, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            int boundedLimit = Math.Clamp(limit ?? 20, 1, 200);
            GatewaySession[] visibleSessions = [.. sessions.ListRecentSessions(user.Id, boundedLimit)
                .Where(session => !string.Equals(session.TargetType, "agent", StringComparison.Ordinal)
                    || (catalog.TryGetAgent(session.TargetId, out GatewayAgent? agent)
                        && GatewayOwnershipAccess.CanAccessAgent(user, agent)))];
            return Results.Json(new GatewaySessionsResponse(visibleSessions), KautukaJsonContext.Default.GatewaySessionsResponse);
        });

        app.MapGet("/api/sessions/agents/{agentId}", (string agentId, IGatewaySessionStore sessions, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Json(
                new GatewaySessionsResponse([.. sessions.ListSessions("agent", agentId, user.Id)]),
                KautukaJsonContext.Default.GatewaySessionsResponse);
        });

        app.MapGet("/api/sessions/{sessionId}", (string sessionId, IGatewaySessionStore sessions, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            return GatewayOwnershipAccess.CanAccessSession(user, session)
                ? Results.Json(session, KautukaJsonContext.Default.GatewaySession)
                : Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        });

        app.MapDelete("/api/sessions/{sessionId}", (string sessionId, IGatewaySessionStore sessions, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            bool removed = sessions.DeleteSession(sessionId);
            return removed
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPost("/api/sessions/{sessionId}/compact", async (string sessionId, IGatewaySessionStore sessions, ISessionCompactionService compactionService, IGatewayAuthGuard auth, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            GatewaySession? compacted = await compactionService.CompactAsync(sessionId, "manual", cancellationToken).ConfigureAwait(false);
            return compacted is null
                ? Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(compacted, KautukaJsonContext.Default.GatewaySession);
        });
    }
}
