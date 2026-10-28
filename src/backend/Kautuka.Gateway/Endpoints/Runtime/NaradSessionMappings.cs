using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradSessionMappings
{
    internal const string DefaultAgentAlias = "default";
    internal const string DefaultOrganizationId = "kautuka-default";

    internal static GatewayAgent? ResolveAgent(
        IGatewayCatalogStore catalog,
        GatewayUser user,
        string? requestedAgent)
    {
        IReadOnlyList<GatewayAgent> visibleAgents = [.. catalog.ListAgents()
            .Where(agent => agent.Enabled && GatewayOwnershipAccess.CanAccessAgent(user, agent))
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(agent => agent.Id, StringComparer.Ordinal)];

        if (!string.IsNullOrWhiteSpace(requestedAgent))
        {
            if (string.Equals(requestedAgent, DefaultAgentAlias, StringComparison.OrdinalIgnoreCase))
            {
                return visibleAgents.Count > 0 ? visibleAgents[0] : null;
            }

            GatewayAgent? matchedById = visibleAgents.FirstOrDefault(agent =>
                string.Equals(agent.Id, requestedAgent, StringComparison.Ordinal));
            if (matchedById is not null)
            {
                return matchedById;
            }

            return visibleAgents.FirstOrDefault(agent =>
                string.Equals(agent.Name, requestedAgent, StringComparison.OrdinalIgnoreCase));
        }

        return visibleAgents.Count > 0 ? visibleAgents[0] : null;
    }

    internal static JsonObject ToSessionRecord(GatewaySession session)
    {
        string? lastRunAt = session.Messages.Count > 0 ? session.Messages[^1].Timestamp : null;
        return new JsonObject
        {
            ["id"] = session.Id,
            ["agentId"] = session.TargetId,
            ["name"] = string.IsNullOrWhiteSpace(session.Title) ? null : session.Title,
            ["archived"] = !string.IsNullOrWhiteSpace(session.ArchivedAt),
            ["createdAt"] = session.CreatedAt,
            ["updatedAt"] = session.UpdatedAt,
            ["organizationId"] = DefaultOrganizationId,
            ["createdById"] = session.OwnerUserId,
            ["lastRunAt"] = lastRunAt,
        };
    }

    internal static NaradSessionListResponse ListSessions(
        string agentId,
        GatewayUser user,
        IGatewaySessionStore sessions,
        int limit,
        string? cursor,
        bool includeArchived)
    {
        int boundedLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
        IReadOnlyList<GatewaySession> matchingSessions = sessions.ListSessions("agent", agentId, user.Id, includeArchived);
        IEnumerable<GatewaySession> ordered = matchingSessions
            .OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal)
            .ThenByDescending(session => session.Id, StringComparer.Ordinal);

        if (TryParseCursor(cursor, out string cursorUpdatedAt, out string cursorId))
        {
            ordered = ordered.Where(session =>
                string.Compare(session.UpdatedAt, cursorUpdatedAt, StringComparison.Ordinal) < 0
                || (string.Equals(session.UpdatedAt, cursorUpdatedAt, StringComparison.Ordinal)
                    && string.Compare(session.Id, cursorId, StringComparison.Ordinal) < 0));
        }

        List<GatewaySession> pageSessions = ordered.Take(boundedLimit + 1).ToList();
        bool hasMore = pageSessions.Count > boundedLimit;
        if (hasMore)
        {
            pageSessions.RemoveAt(pageSessions.Count - 1);
        }

        JsonObject[] records = pageSessions.Select(ToSessionRecord).ToArray();
        string? nextCursor = hasMore && pageSessions.Count > 0
            ? EncodeCursor(pageSessions[^1].UpdatedAt, pageSessions[^1].Id)
            : null;

        return new NaradSessionListResponse(records, nextCursor);
    }

    internal static IResult? ValidateSessionAccess(
        string sessionId,
        GatewayAgent agent,
        GatewayUser user,
        IGatewaySessionStore sessions,
        out GatewaySession? session,
        bool adminFallback = false)
    {
        session = null;
        if (!sessions.TryGetSession(sessionId, out GatewaySession? resolvedSession) || resolvedSession is null)
        {
            return Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        }

        if (!string.Equals(resolvedSession.TargetType, "agent", StringComparison.Ordinal)
            || !string.Equals(resolvedSession.TargetId, agent.Id, StringComparison.Ordinal)
            || !GatewayOwnershipAccess.CanAccessSession(user, resolvedSession, adminFallback))
        {
            return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        }

        session = resolvedSession;
        return null;
    }

    internal static bool TrySetStreamAccessFailure(
        HttpContext context,
        string sessionId,
        GatewayAgent agent,
        GatewayUser user,
        IGatewaySessionStore sessions,
        out GatewaySession? session,
        bool adminFallback = false)
    {
        session = null;
        if (!sessions.TryGetSession(sessionId, out GatewaySession? resolvedSession) || resolvedSession is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }

        if (!string.Equals(resolvedSession.TargetType, "agent", StringComparison.Ordinal)
            || !string.Equals(resolvedSession.TargetId, agent.Id, StringComparison.Ordinal)
            || !GatewayOwnershipAccess.CanAccessSession(user, resolvedSession, adminFallback))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return true;
        }

        session = resolvedSession;
        return false;
    }

    internal static IResult? ValidateCapabilitiesSessionAccess(
        string sessionId,
        GatewayAgent agent,
        GatewayUser user,
        IGatewaySessionStore sessions)
    {
        if (!sessions.TryGetSession(sessionId, out GatewaySession? resolvedSession) || resolvedSession is null)
        {
            return Results.Json(new GatewayErrorResponse("Session not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        }

        if (!string.Equals(resolvedSession.TargetType, "agent", StringComparison.Ordinal)
            || !string.Equals(resolvedSession.TargetId, agent.Id, StringComparison.Ordinal)
            || !GatewayOwnershipAccess.CanAccessSession(user, resolvedSession))
        {
            return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    internal static bool TryResolveAgentId(JsonElement body, string? queryAgentId, out string? agentId)
    {
        if (!string.IsNullOrWhiteSpace(queryAgentId))
        {
            agentId = queryAgentId;
            return true;
        }

        if (body.ValueKind is JsonValueKind.Object
            && body.TryGetProperty("agentId", out JsonElement agentIdElement)
            && agentIdElement.ValueKind is JsonValueKind.String)
        {
            agentId = agentIdElement.GetString();
            return !string.IsNullOrWhiteSpace(agentId);
        }

        agentId = null;
        return false;
    }

    private static string EncodeCursor(string updatedAt, string id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{updatedAt}|{id}"));

    private static bool TryParseCursor(string? cursor, out string updatedAt, out string id)
    {
        updatedAt = string.Empty;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            int separatorIndex = decoded.LastIndexOf('|');
            if (separatorIndex <= 0 || separatorIndex >= decoded.Length - 1)
            {
                return false;
            }

            updatedAt = decoded[..separatorIndex];
            id = decoded[(separatorIndex + 1)..];
            return !string.IsNullOrWhiteSpace(updatedAt) && !string.IsNullOrWhiteSpace(id);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record NaradSessionListResponse(IReadOnlyList<JsonObject> Sessions, string? NextCursor);

internal sealed record NaradIntentResponse(
    string Status,
    string? Detail = null,
    long? CurrentRevision = null,
    long? CurrentSessionSequence = null);

internal static class NaradIntentResponses
{
    public static IResult Accepted(long? currentRevision = null, long? currentSessionSequence = null)
        => Results.Json(
            new NaradIntentResponse(
                "accepted",
                CurrentRevision: currentRevision,
                CurrentSessionSequence: currentSessionSequence),
            KautukaJsonContext.Default.NaradIntentResponse);

    public static IResult Rejected(string detail, int statusCode = StatusCodes.Status400BadRequest)
        => Results.Json(new NaradIntentResponse("rejected", detail), KautukaJsonContext.Default.NaradIntentResponse, statusCode: statusCode);

    public static IResult Conflict(string detail)
        => Results.Json(new NaradIntentResponse("conflict", detail), KautukaJsonContext.Default.NaradIntentResponse, statusCode: StatusCodes.Status409Conflict);
}
