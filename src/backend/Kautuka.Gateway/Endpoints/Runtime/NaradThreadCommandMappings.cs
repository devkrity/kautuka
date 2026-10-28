using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradThreadCommandMappings
{
    public static WebApplication MapNaradThreadCommandEndpoints(this WebApplication app)
    {
        app.MapPost("/api/narad/v1/commands/resolve-interrupt", async (
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IThreadCommandService commands,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Error("Request body must be a JSON object.", StatusCodes.Status400BadRequest);
            }

            if (body.GetRawText().Length > NaradRequestParsing.MaxBodyLength)
            {
                return Error("Request body exceeds maximum size.", StatusCodes.Status400BadRequest);
            }

            if (!body.TryGetProperty("interruptId", out JsonElement interruptIdEl)
                || interruptIdEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(interruptIdEl.GetString()))
            {
                return Error("Field 'interruptId' is required.", StatusCodes.Status400BadRequest);
            }

            if (!body.TryGetProperty("decision", out JsonElement decisionEl)
                || decisionEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(decisionEl.GetString()))
            {
                return Error("Field 'decision' is required.", StatusCodes.Status400BadRequest);
            }

            if (!ApprovalActionCatalog.TryMapDecision(decisionEl.GetString(), out _, out _, out string decision))
            {
                return Error("Field 'decision' is not a known interrupt action.", StatusCodes.Status400BadRequest);
            }

            string? reason = body.TryGetProperty("reason", out JsonElement reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString()
                    : null;
            bool adminFallback = ReadAdminFallback(body, user);
            long? expectedRevision = ReadExpectedRevision(body);

            ThreadCommandResult result = await commands.ResolveInterruptAsync(
                new ResolveThreadInterruptCommand(
                    interruptIdEl.GetString()!,
                    decision,
                    reason,
                    user.Id,
                    user.DisplayName ?? user.Username,
                    expectedRevision,
                    AdminFallback: adminFallback),
                ct).ConfigureAwait(false);

            return ToResult(result);
        });

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/resolve-all-interrupts", async (
            string agentId,
            string sessionId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IThreadCommandService commands,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                return Results.NotFound();
            }

            IResult? accessError = NaradSessionMappings.ValidateSessionAccess(
                sessionId,
                agent,
                user,
                sessions,
                out _,
                ReadAdminFallback(body, user));
            if (accessError is not null)
            {
                return accessError;
            }

            bool approved = !body.TryGetProperty("approved", out JsonElement approvedEl)
                || approvedEl.ValueKind != JsonValueKind.False;
            bool trustThisRun = body.TryGetProperty("trustThisRun", out JsonElement trustEl)
                && trustEl.ValueKind == JsonValueKind.True;
            bool stopRun = body.TryGetProperty("stopRun", out JsonElement stopEl)
                && stopEl.ValueKind == JsonValueKind.True;
            string? runId = body.TryGetProperty("runId", out JsonElement runIdEl)
                && runIdEl.ValueKind == JsonValueKind.String
                    ? runIdEl.GetString()
                    : null;
            string? reason = body.TryGetProperty("reason", out JsonElement reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString()
                    : null;
            bool adminFallback = ReadAdminFallback(body, user);
            long? expectedRevision = ReadExpectedRevision(body);

            ThreadCommandResult result = await commands.ResolveAllInterruptsAsync(
                new ResolveAllThreadInterruptsCommand(
                    agent.Id,
                    sessionId,
                    runId,
                    approved,
                    trustThisRun,
                    stopRun,
                    reason,
                    user.Id,
                    user.DisplayName ?? user.Username,
                    expectedRevision,
                    AdminFallback: adminFallback),
                ct).ConfigureAwait(false);

            return ToResult(result);
        });

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/stop-active-root", async (
            string agentId,
            string sessionId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IThreadCommandService commands,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                return Results.NotFound();
            }

            IResult? accessError = NaradSessionMappings.ValidateSessionAccess(
                sessionId,
                agent,
                user,
                sessions,
                out _,
                ReadAdminFallback(body, user));
            if (accessError is not null)
            {
                return accessError;
            }

            string? runId = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("runId", out JsonElement runIdEl)
                && runIdEl.ValueKind == JsonValueKind.String
                    ? runIdEl.GetString()
                    : null;
            string? reason = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("reason", out JsonElement reasonEl)
                && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString()
                    : null;
            bool adminFallback = ReadAdminFallback(
                body.ValueKind == JsonValueKind.Object ? body : default,
                user);
            long? expectedRevision = body.ValueKind == JsonValueKind.Object
                ? ReadExpectedRevision(body)
                : null;

            ThreadCommandResult result = await commands.StopActiveRootAsync(
                new StopActiveRootCommand(
                    agent.Id,
                    sessionId,
                    runId,
                    reason,
                    user.Id,
                    user.DisplayName ?? user.Username,
                    expectedRevision,
                    AdminFallback: adminFallback),
                ct).ConfigureAwait(false);

            return ToResult(result);
        });

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/resume-waiting-root", async (
            string agentId,
            string sessionId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IThreadCommandService commands,
            CancellationToken ct) =>
            await MapCapabilityCommandAsync(
                agentId,
                sessionId,
                body,
                context,
                auth,
                catalog,
                sessions,
                (agent, user, revision, adminFallback) => commands.ResumeWaitingRootAsync(
                    new ResumeWaitingRootCommand(
                        agent.Id,
                        sessionId,
                        ReadOptionalRunId(body),
                        user.Id,
                        revision,
                        adminFallback),
                    ct)).ConfigureAwait(false));

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/retry-target", async (
            string agentId,
            string sessionId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IThreadCommandService commands,
            CancellationToken ct) =>
        {
            IResult? guard = await AuthorizeThreadAsync(agentId, sessionId, body, context, auth, catalog, sessions).ConfigureAwait(false);
            if (guard is not null)
            {
                return guard;
            }

            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            bool adminFallback = ReadAdminFallback(body, user);
            string? targetType = body.TryGetProperty("targetType", out JsonElement targetTypeEl)
                && targetTypeEl.ValueKind == JsonValueKind.String
                    ? targetTypeEl.GetString()
                    : null;
            string? targetId = body.TryGetProperty("targetId", out JsonElement targetIdEl)
                && targetIdEl.ValueKind == JsonValueKind.String
                    ? targetIdEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
            {
                return Error("Fields 'targetType' and 'targetId' are required.", StatusCodes.Status400BadRequest);
            }

            ThreadCommandResult result = await commands.RetryTargetAsync(
                new RetryExecutionTargetCommand(
                    agent!.Id,
                    sessionId,
                    targetType,
                    targetId,
                    user.Id,
                    ReadExpectedRevision(body),
                    adminFallback),
                ct).ConfigureAwait(false);
            return ToResult(result);
        });

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/commands/cancel-graph-node", async (
            string agentId,
            string sessionId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IThreadCommandService commands,
            CancellationToken ct) =>
        {
            IResult? guard = await AuthorizeThreadAsync(agentId, sessionId, body, context, auth, catalog, sessions).ConfigureAwait(false);
            if (guard is not null)
            {
                return guard;
            }

            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            bool adminFallback = ReadAdminFallback(body, user);
            string? nodeId = body.TryGetProperty("nodeId", out JsonElement nodeIdEl)
                && nodeIdEl.ValueKind == JsonValueKind.String
                    ? nodeIdEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return Error("Field 'nodeId' is required.", StatusCodes.Status400BadRequest);
            }

            ThreadCommandResult result = await commands.CancelGraphNodeAsync(
                new CancelGraphNodeCommand(agent!.Id, sessionId, nodeId, user.Id, ReadExpectedRevision(body), adminFallback),
                ct).ConfigureAwait(false);
            return ToResult(result);
        });

        return app;
    }

    private static async Task<IResult> MapCapabilityCommandAsync(
        string agentId,
        string sessionId,
        JsonElement body,
        HttpContext context,
        IGatewayAuthGuard auth,
        IGatewayCatalogStore catalog,
        IGatewaySessionStore sessions,
        Func<GatewayAgent, GatewayUser, long?, bool, ValueTask<ThreadCommandResult>> execute)
    {
        IResult? guard = await AuthorizeThreadAsync(agentId, sessionId, body, context, auth, catalog, sessions).ConfigureAwait(false);
        if (guard is not null)
        {
            return guard;
        }

        if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
        {
            return Results.Unauthorized();
        }

        GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
        bool adminFallback = ReadAdminFallback(body, user);
        ThreadCommandResult result = await execute(agent!, user, ReadExpectedRevision(body), adminFallback).ConfigureAwait(false);
        return ToResult(result);
    }

    private static Task<IResult?> AuthorizeThreadAsync(
        string agentId,
        string sessionId,
        JsonElement body,
        HttpContext context,
        IGatewayAuthGuard auth,
        IGatewayCatalogStore catalog,
        IGatewaySessionStore sessions)
    {
        if (body.ValueKind == JsonValueKind.Object && body.GetRawText().Length > NaradRequestParsing.MaxBodyLength)
        {
            return Task.FromResult<IResult?>(Error("Request body exceeds maximum size.", StatusCodes.Status400BadRequest));
        }

        if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
        {
            return Task.FromResult<IResult?>(Results.Unauthorized());
        }

        GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
        if (agent is null)
        {
            return Task.FromResult<IResult?>(Results.NotFound());
        }

        IResult? accessError = NaradSessionMappings.ValidateSessionAccess(
            sessionId,
            agent,
            user,
            sessions,
            out _,
            ReadAdminFallback(body, user));
        return Task.FromResult(accessError);
    }

    private static bool ReadAdminFallback(JsonElement body, GatewayUser user)
        => body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("adminFallback", out JsonElement adminFallbackEl)
            && adminFallbackEl.ValueKind == JsonValueKind.True
            && GatewayOwnershipAccess.IsAdmin(user);

    private static long? ReadExpectedRevision(JsonElement body)
        => body.TryGetProperty("expectedRevision", out JsonElement revisionEl)
            && revisionEl.TryGetInt64(out long revision)
                ? revision
                : null;

    private static string? ReadOptionalRunId(JsonElement body)
        => body.TryGetProperty("runId", out JsonElement runIdEl) && runIdEl.ValueKind == JsonValueKind.String
            ? runIdEl.GetString()
            : null;

    private static IResult Error(string message, int statusCode)
        => Results.Json(
            new GatewayThreadCommandResponse("rejected", Detail: message),
            KautukaJsonContext.Default.GatewayThreadCommandResponse,
            statusCode: statusCode);

    private static IResult ToResult(ThreadCommandResult result)
    {
        string status = result.Status switch
        {
            ThreadCommandStatus.Accepted => "accepted",
            ThreadCommandStatus.AlreadyApplied => "already_applied",
            ThreadCommandStatus.Expired => "expired",
            ThreadCommandStatus.Conflict => "conflict",
            ThreadCommandStatus.UnsupportedCapability => "unsupported",
            ThreadCommandStatus.Forbidden => "forbidden",
            ThreadCommandStatus.Rejected => "rejected",
            _ => "not_found",
        };

        int statusCode = result.Status switch
        {
            ThreadCommandStatus.Accepted => StatusCodes.Status202Accepted,
            ThreadCommandStatus.AlreadyApplied => StatusCodes.Status200OK,
            ThreadCommandStatus.Expired => StatusCodes.Status409Conflict,
            ThreadCommandStatus.Conflict => StatusCodes.Status409Conflict,
            ThreadCommandStatus.UnsupportedCapability => StatusCodes.Status409Conflict,
            ThreadCommandStatus.Forbidden => StatusCodes.Status403Forbidden,
            ThreadCommandStatus.Rejected => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status404NotFound,
        };

        return Results.Json(
            new GatewayThreadCommandResponse(
                status,
                result.Decision,
                result.RequestId,
                result.ResolvedCount,
                result.Detail),
            KautukaJsonContext.Default.GatewayThreadCommandResponse,
            statusCode: statusCode);
    }
}

internal sealed record GatewayThreadCommandResponse(
    string Status,
    string? Decision = null,
    string? RequestId = null,
    int? ResolvedCount = null,
    string? Detail = null);
