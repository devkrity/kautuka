using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class SessionQueueEndpoints
{
    public static WebApplication MapSessionQueueEndpoints(this WebApplication app)
    {
        app.MapGet("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/queue", (
            string agentId, string sessionId, IGatewayAuthGuard auth, IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions, [FromServices] SessionTurnQueueStore queue, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            IResult? denied = Validate(agentId, sessionId, user, catalog, sessions);
            if (denied is not null) return denied;
            IReadOnlyList<QueuedSessionTurn> waiting = queue.ListSession(user.Id, sessionId);
            return Results.Json(new JsonObject
            {
                ["messages"] = new JsonArray([.. waiting.Select(turn =>
                {
                    JsonObject payload = JsonNode.Parse(turn.PayloadJson)!.AsObject();
                    string content = payload["message"]?.GetValue<string>() ?? string.Empty;
                    return (JsonNode)new JsonObject { ["id"] = turn.Id, ["order"] = turn.Order,
                        ["content"] = content.Length > 300 ? content[..300] + "…" : content };
                })]),
                ["throughOrder"] = waiting.Count == 0 ? 0 : waiting[^1].Order,
            }, KautukaJsonContext.Default.JsonObject);
        });
        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/queue/remove", async (
            string agentId, string sessionId, JsonElement body, IGatewayAuthGuard auth, IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions, [FromServices] SessionTurnQueueStore queue,
            [FromServices] IEnumerable<ISessionTurnProjection> projections, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            IResult? denied = Validate(agentId, sessionId, user, catalog, sessions);
            if (denied is not null) return denied;
            string? id = body.TryGetStringProperty("messageId");
            long through = body.TryGetProperty("throughOrder", out JsonElement value) && value.TryGetInt64(out long order) ? order : 0;
            if (string.IsNullOrWhiteSpace(id) && through <= 0) return Results.BadRequest();
            IReadOnlyList<QueuedSessionTurn> waiting = queue.ListSession(user.Id, sessionId);
            QueuedSessionTurn? before = waiting.Count == 0 ? null : waiting[0];
            int removed = queue.RemoveSessionWaiting(user.Id, sessionId, id, id is null ? through : long.MaxValue);
            if (before is not null)
                foreach (ISessionTurnProjection projection in projections) await projection.QueueChangedAsync(before, ct).ConfigureAwait(false);
            return Results.Json(new JsonObject { ["removed"] = removed }, KautukaJsonContext.Default.JsonObject);
        });
        return app;
    }

    private static IResult? Validate(string agentId, string sessionId, GatewayUser user, IGatewayCatalogStore catalog, IGatewaySessionStore sessions)
    {
        GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
        return agent is null ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : NaradSessionMappings.ValidateSessionAccess(sessionId, agent, user, sessions, out _);
    }
}
