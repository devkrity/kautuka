using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class GatewayApprovalEndpointMappings
{
    public static WebApplication MapGatewayApprovalEndpoints(this WebApplication app)
    {
        // GET /api/approvals/pending
        // Returns pending approval requests visible to the caller.
        // Query: ?scope=all (admin only) to see all pending across all users.
        app.MapGet("/api/approvals/pending", (
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            bool adminAll = false;
            if (context.Request.Query.TryGetValue("scope", out var scope)
                && string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                adminAll = string.Equals(user?.Role, "admin", StringComparison.OrdinalIgnoreCase);
            }

            IReadOnlyList<GatewayToolApprovalRequest> requests = approvalStore.ListPending(user?.Id, adminAll);
            return Results.Json(requests.Select(request => ToApprovalResponse(request, catalog, users, invocationLinks, continuations, workflows)).ToArray(), KautukaJsonContext.Default.ApprovalResponseArray);
        });

        // GET /api/approvals/changes
        // Invalidation-only SSE for pending Approvals / nav badge. Not a transcript stream.
        // Query: ?scope=all (admin only) subscribes to every approval boundary.
        app.MapGet("/api/approvals/changes", async (
            HttpContext context,
            IGatewayAuthGuard auth,
            IRuntimeEventStreamHub streamHub,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            bool adminAll = context.Request.Query.TryGetValue("scope", out var scope)
                && string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
                && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            await context.Response.StartAsync(ct).ConfigureAwait(false);

            using ApprovalInvalidationSubscription subscription = adminAll
                ? streamHub.SubscribeForAdminApprovals()
                : streamHub.SubscribeForUserApprovals(user.Id);

            await context.Response.WriteAsync(": connected\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            await foreach (ApprovalInvalidation ping in subscription.Reader
                .ReadAllAsync(ct)
                .ConfigureAwait(false))
            {
                var payload = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "STATE_DELTA",
                    ["delta"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["op"] = "add",
                            ["path"] = "/kautukaApprovalInvalidation",
                            ["value"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["eventId"] = ping.EventId,
                                ["eventType"] = ping.EventType,
                                ["sessionId"] = ping.SessionId,
                                ["rootRunId"] = ping.RootRunId,
                            },
                        }),
                };
                await context.Response.WriteAsync(
                    $"id: {ping.EventId}\ndata: {payload.ToJsonString()}\n\n",
                    ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        });

        // GET /api/approvals/session/{sessionId}
        // Returns all approval requests (pending and resolved) for a session.
        app.MapGet("/api/approvals/session/{sessionId}", (
            string sessionId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewaySessionStore sessions,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            bool adminFallback = context.Request.Query.TryGetValue("adminFallback", out var adminFallbackValue)
                && bool.TryParse(adminFallbackValue, out bool parsedAdminFallback)
                && parsedAdminFallback
                && GatewayOwnershipAccess.IsAdmin(user);

            IReadOnlyList<GatewayToolApprovalRequest> requests = [.. approvalStore
                .ListBySession(sessionId)
                .Concat(approvalStore.ListByOuterSession(sessionId))
                .Where(request => CanAccessApproval(user, request, sessions, adminFallback))
                .GroupBy(request => request.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(request => request.RequestedAt, StringComparer.Ordinal)];
            return Results.Json(requests.Select(request => ToApprovalResponse(request, catalog, users, invocationLinks, continuations, workflows)).ToArray(), KautukaJsonContext.Default.ApprovalResponseArray);
        });

        // GET /api/approvals/history
        // Returns resolved approvals (approved or denied) ordered by decided_at descending.
        // Query: ?scope=all (admin only), ?limit=50 (max 100), ?offset=0
        app.MapGet("/api/approvals/history", (
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            bool adminAll = false;
            if (context.Request.Query.TryGetValue("scope", out var histScope)
                && string.Equals(histScope, "all", StringComparison.OrdinalIgnoreCase))
            {
                adminAll = string.Equals(user?.Role, "admin", StringComparison.OrdinalIgnoreCase);
            }

            int limit = 50;
            int offset = 0;
            if (context.Request.Query.TryGetValue("limit", out var limitStr)
                && int.TryParse(limitStr, out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 100);
            }
            if (context.Request.Query.TryGetValue("offset", out var offsetStr)
                && int.TryParse(offsetStr, out int parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
            }

            IReadOnlyList<GatewayToolApprovalRequest> requests = approvalStore.ListHistory(user?.Id, adminAll, limit, offset);
            return Results.Json(requests.Select(request => ToApprovalResponse(request, catalog, users, invocationLinks, continuations, workflows)).ToArray(), KautukaJsonContext.Default.ApprovalResponseArray);
        });

        // Resource-owned HITL aggregates. Do not use :regex on CreateSlimBuilder — regex is not registered.
        app.MapGet("/api/agents/{agentId}/stats", (
            string agentId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth) =>
            HandleHitlStats(context, approvalStore, catalog, auth, agentId, mcpServerId: null, skillId: null));

        app.MapGet("/api/agents/{agentId}/stats/denied", (
            string agentId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
            HandleHitlDeniedStats(context, approvalStore, catalog, users, invocationLinks, continuations, workflows, auth, agentId, mcpServerId: null, skillId: null));

        app.MapGet("/api/connectors/{serverId}/stats", (
            string serverId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth) =>
            HandleHitlStats(context, approvalStore, catalog, auth, agentId: null, mcpServerId: serverId, skillId: null));

        app.MapGet("/api/connectors/{serverId}/stats/denied", (
            string serverId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
            HandleHitlDeniedStats(context, approvalStore, catalog, users, invocationLinks, continuations, workflows, auth, agentId: null, mcpServerId: serverId, skillId: null));

        app.MapGet("/api/skills/{skillId}/stats", (
            string skillId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth) =>
            HandleHitlStats(context, approvalStore, catalog, auth, agentId: null, mcpServerId: null, skillId));

        app.MapGet("/api/skills/{skillId}/stats/denied", (
            string skillId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
            HandleHitlDeniedStats(context, approvalStore, catalog, users, invocationLinks, continuations, workflows, auth, agentId: null, mcpServerId: null, skillId));

        // GET /api/approvals/{requestId}
        // Returns a specific approval request by ID (for polling).
        app.MapGet("/api/approvals/{requestId}", (
            string requestId,
            HttpContext context,
            IGatewayApprovalStore approvalStore,
            IGatewaySessionStore sessions,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
            IAgentToolInvocationLinkStore invocationLinks,
            IGatewayContinuationStore continuations,
            IGatewayWorkflowStore workflows,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            bool adminFallback = context.Request.Query.TryGetValue("adminFallback", out var adminFallbackValue)
                && bool.TryParse(adminFallbackValue, out bool parsedAdminFallback)
                && parsedAdminFallback
                && GatewayOwnershipAccess.IsAdmin(user);

            GatewayToolApprovalRequest? request = approvalStore.GetById(requestId);
            return request is null || !CanAccessApproval(user, request, sessions, adminFallback)
                ? Results.Json(new GatewayErrorResponse("Approval request not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(ToApprovalResponse(request, catalog, users, invocationLinks, continuations, workflows), KautukaJsonContext.Default.ApprovalResponse);
        });

        return app;
    }

    private static ApprovalResponse ToApprovalResponse(
        GatewayToolApprovalRequest request,
        IGatewayCatalogStore catalog,
        IGatewayUserStore users,
        IAgentToolInvocationLinkStore invocationLinks,
        IGatewayContinuationStore continuations,
        IGatewayWorkflowStore workflows)
    {
        string? agentName = ResolveAgentName(catalog, request.AgentId);
        string? outerAgentName = ResolveAgentName(catalog, request.OuterAgentId);
        string? decidedByDisplayName = ResolveUserDisplayName(users, request.DecidedBy);
        AgentToolInvocationLink? invocationLink = invocationLinks.GetByApprovalRequestId(request.Id);
        GatewayContinuationCheckpoint? checkpoint = string.IsNullOrWhiteSpace(request.CheckpointId)
            ? null
            : continuations.GetById(request.CheckpointId);
        ApprovalAction[] actions = ApprovalResponse.ComputeActions(request.ToolType);
        int? remainingChangeRequests = null;
        if (string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
            || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal))
        {
            GatewayWorkflowInstance? instance = workflows.GetInstance(request.InstanceId ?? string.Empty);
            int finishedAttempts = instance?.Steps.Count(step =>
                string.Equals(step.NodeId, request.NodeId, StringComparison.Ordinal)
                && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal)) ?? 0;
            if (!WorkflowRetryPolicy.CanRequestChanges(finishedAttempts))
            {
                actions = actions.Where(action => action.Value != ApprovalActionCatalog.RequestChanges).ToArray();
            }
            remainingChangeRequests = WorkflowRetryPolicy.RemainingChangeRequests(finishedAttempts);
        }
        return ApprovalResponse.From(
            request,
            agentName,
            outerAgentName,
            decidedByDisplayName,
            request.ResolutionSource,
            invocationLink,
            checkpoint,
            actions,
            remainingChangeRequests);
    }

    private static bool CanAccessApproval(
        GatewayUser user,
        GatewayToolApprovalRequest request,
        IGatewaySessionStore sessions,
        bool adminFallback)
    {
        if (GatewayOwnershipAccess.CanAccessApproval(user, request, adminFallback))
            return true;

        foreach (string? sessionId in new[] { request.SessionId, request.OuterSessionId })
        {
            if (!string.IsNullOrWhiteSpace(sessionId)
                && sessions.TryGetSession(sessionId, out GatewaySession? session)
                && GatewayOwnershipAccess.CanAccessSession(user, session, adminFallback))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveUserDisplayName(IGatewayUserStore users, string? userId)
    {
        string normalizedUserId = userId?.StartsWith("bypass:", StringComparison.Ordinal) == true
            ? userId["bypass:".Length..]
            : userId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalizedUserId)
            && users.TryGetUserById(normalizedUserId, out GatewayUser? user)
            && user is not null
                ? user.DisplayName ?? user.Username
                : null;
    }

    private static string? ResolveAgentName(IGatewayCatalogStore catalog, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        return catalog.TryGetAgent(agentId, out GatewayAgent? agent) && agent is not null
            ? agent.Name
            : null;
    }

    private static IResult HandleHitlStats(
        HttpContext context,
        IGatewayApprovalStore approvalStore,
        IGatewayCatalogStore catalog,
        IGatewayAuthGuard auth,
        string? agentId,
        string? mcpServerId,
        string? skillId)
    {
        if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            return Results.Unauthorized();

        if (!TryParseHitlStatsQuery(context, user, catalog, agentId, mcpServerId, skillId, out HitlApprovalStatsQuery? query, out int windowDays, out IResult? error)
            || query is null)
        {
            return error ?? Results.BadRequest();
        }

        HitlApprovalStatItem[] items =
        [
            .. approvalStore.ListHitlStats(query).Select(row => ToStatItem(row, query.McpServerId)),
        ];
        return Results.Json(
            new HitlApprovalStatsResponse(windowDays, query.Since ?? string.Empty, items),
            KautukaJsonContext.Default.HitlApprovalStatsResponse);
    }

    private static IResult HandleHitlDeniedStats(
        HttpContext context,
        IGatewayApprovalStore approvalStore,
        IGatewayCatalogStore catalog,
        IGatewayUserStore users,
        IAgentToolInvocationLinkStore invocationLinks,
        IGatewayContinuationStore continuations,
        IGatewayWorkflowStore workflows,
        IGatewayAuthGuard auth,
        string? agentId,
        string? mcpServerId,
        string? skillId)
    {
        if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            return Results.Unauthorized();

        if (!TryParseHitlStatsQuery(context, user, catalog, agentId, mcpServerId, skillId, out HitlApprovalStatsQuery? query, out _, out IResult? error)
            || query is null)
        {
            return error ?? Results.BadRequest();
        }

        string? toolName = ReadQueryString(context, "toolName");
        query = query with { ToolName = string.IsNullOrWhiteSpace(toolName) ? null : toolName };

        if (context.Request.Query.TryGetValue("ungrouped", out var ungroupedValue)
            && string.Equals(ungroupedValue, "1", StringComparison.Ordinal))
        {
            query = query with { UngroupedRevisionOnly = true, AgentDefinitionRevision = null };
        }
        else if (context.Request.Query.TryGetValue("agentDefinitionRevision", out var revisionValue)
            && long.TryParse(revisionValue, out long parsedRevision))
        {
            query = query with { AgentDefinitionRevision = parsedRevision, UngroupedRevisionOnly = false };
        }

        int limit = 20;
        if (context.Request.Query.TryGetValue("limit", out var limitStr)
            && int.TryParse(limitStr, out int parsedLimit))
        {
            limit = Math.Clamp(parsedLimit, 1, 50);
        }

        IReadOnlyList<GatewayToolApprovalRequest> requests = approvalStore.ListDeniedHistory(query, limit);
        return Results.Json(
            requests.Select(request => ToApprovalResponse(request, catalog, users, invocationLinks, continuations, workflows)).ToArray(),
            KautukaJsonContext.Default.ApprovalResponseArray);
    }

    private static bool TryParseHitlStatsQuery(
        HttpContext context,
        GatewayUser user,
        IGatewayCatalogStore catalog,
        string? pathAgentId,
        string? pathMcpServerId,
        string? pathSkillId,
        out HitlApprovalStatsQuery? query,
        out int windowDays,
        out IResult? error)
    {
        query = null;
        error = null;
        windowDays = 30;
        bool allTime = false;
        if (context.Request.Query.TryGetValue("days", out var daysValue))
        {
            string daysText = daysValue.ToString().Trim();
            if (string.Equals(daysText, "all", StringComparison.OrdinalIgnoreCase)
                || daysText == "0")
            {
                allTime = true;
                windowDays = 0;
            }
            else if (int.TryParse(daysText, out int parsedDays))
            {
                windowDays = Math.Clamp(parsedDays, 1, 365);
            }
        }

        bool adminAll = false;
        if (context.Request.Query.TryGetValue("scope", out var scope)
            && string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            adminAll = string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);
        }

        string? agentId = CoalesceId(pathAgentId, ReadQueryString(context, "agentId"));
        string? mcpServerId = CoalesceId(pathMcpServerId, ReadQueryString(context, "mcpServerId"));
        string? skillId = CoalesceId(pathSkillId, ReadQueryString(context, "skillId"));
        IReadOnlyList<string>? connectorToolNames = null;
        if (string.IsNullOrWhiteSpace(agentId)
            && string.IsNullOrWhiteSpace(mcpServerId)
            && string.IsNullOrWhiteSpace(skillId))
        {
            error = Results.Json(
                new GatewayApprovalErrorResponse("One of agentId, mcpServerId, or skillId is required."),
                KautukaJsonContext.Default.GatewayApprovalErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent)
                || agent is null
                || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                error = Results.Json(
                    new GatewayApprovalErrorResponse("Agent not found"),
                    KautukaJsonContext.Default.GatewayApprovalErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(mcpServerId))
        {
            if (!catalog.TryGetMcpServer(mcpServerId, out GatewayMcpServer? server)
                || server is null
                || !GatewayOwnershipAccess.CanAccessConnector(user, server))
            {
                error = Results.Json(
                    new GatewayApprovalErrorResponse("Connector not found"),
                    KautukaJsonContext.Default.GatewayApprovalErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
                return false;
            }

            connectorToolNames = server.ToolNames is { Count: > 0 }
                ? [.. server.ToolNames.Where(static name => !string.IsNullOrWhiteSpace(name))]
                : null;
        }

        if (!string.IsNullOrWhiteSpace(skillId))
        {
            if (!catalog.TryGetSkill(skillId, out GatewaySkill? skill)
                || skill is null
                || !GatewayOwnershipAccess.CanAccessSkill(user, skill))
            {
                error = Results.Json(
                    new GatewayApprovalErrorResponse("Skill not found"),
                    KautukaJsonContext.Default.GatewayApprovalErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
                return false;
            }
        }

        string? since = allTime
            ? null
            : DateTimeOffset.UtcNow.AddDays(-windowDays).ToString("O");
        query = new HitlApprovalStatsQuery(
            UserId: user.Id,
            AdminAll: adminAll,
            Since: since,
            AgentId: agentId,
            McpServerId: mcpServerId,
            SkillId: skillId,
            ConnectorToolNames: connectorToolNames);
        return true;
    }

    private static string? CoalesceId(string? pathValue, string? queryValue)
        => string.IsNullOrWhiteSpace(pathValue) ? queryValue : pathValue;

    private static string? ReadQueryString(HttpContext context, string key)
        => context.Request.Query.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value.ToString()
                : null;

    private static HitlApprovalStatItem ToStatItem(HitlApprovalStatRow row, string? scopeMcpServerId = null)
        => new(
            row.ToolName ?? string.Empty,
            row.ToolType ?? string.Empty,
            checked((int)row.Approved),
            checked((int)row.Denied),
            checked((int)row.AgentCount),
            row.McpServerId ?? scopeMcpServerId,
            row.SkillId,
            row.LastDeniedAt,
            row.AgentDefinitionRevision);
}
