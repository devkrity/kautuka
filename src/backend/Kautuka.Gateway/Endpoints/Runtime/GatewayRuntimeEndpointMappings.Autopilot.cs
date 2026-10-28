using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeAutopilotEndpoints(WebApplication app)
    {
        app.MapGet("/api/autopilot/schedules", (
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            IReadOnlyList<GatewayAutopilotSchedule> schedules = autopilotStore.ListSchedulesByOwner(user.Id);
            return Results.Json([.. schedules], KautukaJsonContext.Default.GatewayAutopilotScheduleArray);
        });

        app.MapGet("/api/autopilot/schedules/{scheduleId}", (
            string scheduleId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAutopilotSchedule? schedule = autopilotStore.GetSchedule(scheduleId);
            if (schedule is null)
            {
                return Results.Json(new GatewayErrorResponse("Schedule not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.IsSelf(user, schedule.OwnerUserId))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Json(schedule, KautukaJsonContext.Default.GatewayAutopilotSchedule);
        });

        app.MapPost("/api/autopilot/schedules", (
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewayAutopilotStore autopilotStore,
            IGatewayWorkflowStore workflowStore,
            IAgentFilePathAuthorizationService fileAuthorization) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

#pragma warning disable CA1308 // Lowercase is canonical for targetType and mode comparison
            string targetType = body.GetStringProperty("targetType", "").Trim().ToLowerInvariant();
#pragma warning restore CA1308
            string targetId = body.GetStringProperty("targetId", "").Trim();
            if (!IsSupportedTargetType(targetType) || string.IsNullOrWhiteSpace(targetId))
            {
                return Results.Json(new GatewayErrorResponse("targetType and targetId are required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!HasTargetAccess(user, catalog, workflowStore, targetType, targetId))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden target."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string cron = body.GetStringProperty("cronExpression", "").Trim();
#pragma warning disable CA1308 // Lowercase is canonical for mode comparison
            string mode = body.GetStringProperty("mode", AutopilotScheduleModes.On).Trim().ToLowerInvariant();
#pragma warning restore CA1308
            string schedulePromptContext = body.GetStringProperty("schedulePromptContext", "").Trim();
            string[] workspaceFilePaths = NormalizeWorkspaceFilePaths(body.GetStringArrayProperty("workspaceFilePaths"));
            bool notifyUiInbox = body.GetBoolProperty("notifyUiInbox", true);
            bool notifyChannels = body.GetBoolProperty("notifyChannels", false);
            string expiryPolicy = body.GetStringProperty("preRunApprovalExpiryPolicy", AutopilotPreRunApprovalExpiryPolicies.NextOccurrence);
            int? timeoutSeconds = GetOptionalIntProperty(body, "preRunApprovalTimeoutSeconds");
            string overlapPolicy = body.GetStringProperty("overlapPolicy", AutopilotOverlapPolicies.SkipAndNotify);

            if (!ValidateWorkflowTarget(workflowStore, catalog, fileAuthorization, user, targetType, targetId, schedulePromptContext, workspaceFilePaths, out string? targetError))
            {
                return Results.Json(new GatewayErrorResponse(targetError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(cron))
            {
                return Results.Json(new GatewayErrorResponse("cronExpression is required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            TimeZoneInfo timeZone = TimeZoneInfo.Local;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? nextDue = AutopilotCron.GetNextOccurrence(cron, timeZone, nowUtc);
            if (nextDue is null)
            {
                return Results.Json(new GatewayErrorResponse("Invalid cronExpression."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!IsSupportedMode(mode))
            {
                return Results.Json(new GatewayErrorResponse("Invalid mode."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!ValidateLifecyclePolicies(expiryPolicy, timeoutSeconds, overlapPolicy, out string? policyError))
            {
                return Results.Json(new GatewayErrorResponse(policyError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            bool requiresPreRunApproval = string.Equals(mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal);

            var schedule = new GatewayAutopilotSchedule(
                Id: $"asched-{Guid.NewGuid():N}",
                OwnerUserId: user.Id,
                TargetType: targetType,
                TargetId: targetId,
                CronExpression: cron,
                TimeZoneId: timeZone.Id,
                Mode: mode,
                RequiresPreRunApproval: requiresPreRunApproval,
                SchedulePromptContext: schedulePromptContext,
                WorkspaceFilePaths: workspaceFilePaths,
                NotifyUiInbox: notifyUiInbox,
                NotifyChannels: notifyChannels,
                PreRunApprovalExpiryPolicy: expiryPolicy,
                PreRunApprovalTimeoutSeconds: timeoutSeconds,
                OverlapPolicy: overlapPolicy,
                IsPaused: false,
                CreatedAt: nowUtc.ToString("O"),
                UpdatedAt: nowUtc.ToString("O"),
                NextDueAt: nextDue.Value.ToString("O"),
                LastEvaluatedAt: nowUtc.ToString("O"),
                LeaseOwner: null,
                LeaseExpiresAt: null);

            GatewayAutopilotSchedule created = autopilotStore.CreateSchedule(schedule);
            return Results.Json(created, KautukaJsonContext.Default.GatewayAutopilotSchedule, statusCode: StatusCodes.Status201Created);
        });

        app.MapPut("/api/autopilot/schedules/{scheduleId}", (
            string scheduleId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore,
            IGatewayWorkflowStore workflowStore,
            IAgentFilePathAuthorizationService fileAuthorization,
            IGatewayCatalogStore catalog) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAutopilotSchedule? existing = autopilotStore.GetSchedule(scheduleId);
            if (existing is null)
            {
                return Results.Json(new GatewayErrorResponse("Schedule not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.IsSelf(user, existing.OwnerUserId))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string cron = body.GetStringProperty("cronExpression", existing.CronExpression).Trim();
#pragma warning disable CA1308 // Lowercase is canonical for mode comparison
            string mode = body.GetStringProperty("mode", existing.Mode).Trim().ToLowerInvariant();
#pragma warning restore CA1308
            TimeZoneInfo timeZone = TimeZoneInfo.Local;
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? nextDue = AutopilotCron.GetNextOccurrence(cron, timeZone, nowUtc);
            if (nextDue is null)
            {
                return Results.Json(new GatewayErrorResponse("Invalid cronExpression."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!IsSupportedMode(mode))
            {
                return Results.Json(new GatewayErrorResponse("Invalid mode."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            bool requiresPreRunApproval = string.Equals(mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal);
            string expiryPolicy = body.GetStringProperty("preRunApprovalExpiryPolicy", existing.PreRunApprovalExpiryPolicy);
            int? timeoutSeconds = body.TryGetProperty("preRunApprovalTimeoutSeconds", out _)
                ? GetOptionalIntProperty(body, "preRunApprovalTimeoutSeconds")
                : existing.PreRunApprovalTimeoutSeconds;
            string overlapPolicy = body.GetStringProperty("overlapPolicy", existing.OverlapPolicy);
            if (!ValidateLifecyclePolicies(expiryPolicy, timeoutSeconds, overlapPolicy, out string? policyError))
            {
                return Results.Json(new GatewayErrorResponse(policyError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            string schedulePromptContext = body.GetStringProperty("schedulePromptContext", existing.SchedulePromptContext);
            string[] workspaceFilePaths = body.TryGetProperty("workspaceFilePaths", out _)
                ? NormalizeWorkspaceFilePaths(body.GetStringArrayProperty("workspaceFilePaths"))
                : existing.WorkspaceFilePaths;
            if (!ValidateWorkflowTarget(workflowStore, catalog, fileAuthorization, user, existing.TargetType, existing.TargetId, schedulePromptContext, workspaceFilePaths, out string? targetError))
            {
                return Results.Json(new GatewayErrorResponse(targetError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            GatewayAutopilotSchedule updated = existing with
            {
                CronExpression = cron,
                TimeZoneId = timeZone.Id,
                Mode = mode,
                RequiresPreRunApproval = requiresPreRunApproval,
                SchedulePromptContext = schedulePromptContext,
                WorkspaceFilePaths = workspaceFilePaths,
                NotifyUiInbox = body.GetBoolProperty("notifyUiInbox", existing.NotifyUiInbox),
                NotifyChannels = body.GetBoolProperty("notifyChannels", existing.NotifyChannels),
                PreRunApprovalExpiryPolicy = expiryPolicy,
                PreRunApprovalTimeoutSeconds = timeoutSeconds,
                OverlapPolicy = overlapPolicy,
                IsPaused = body.GetBoolProperty("isPaused", existing.IsPaused),
                UpdatedAt = nowUtc.ToString("O"),
                NextDueAt = nextDue.Value.ToString("O"),
                LeaseOwner = null,
                LeaseExpiresAt = null,
            };

            bool saved = autopilotStore.UpdateSchedule(updated);
            return saved
                ? Results.Json(updated, KautukaJsonContext.Default.GatewayAutopilotSchedule)
                : Results.Json(new GatewayErrorResponse("Failed to update schedule."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status500InternalServerError);
        });

        app.MapPost("/api/autopilot/schedules/{scheduleId}/pause", (
            string scheduleId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) => SetPaused(scheduleId, paused: true, context, auth, autopilotStore));

        app.MapPost("/api/autopilot/schedules/{scheduleId}/resume", (
            string scheduleId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) => SetPaused(scheduleId, paused: false, context, auth, autopilotStore));

        app.MapPost("/api/autopilot/schedules/{scheduleId}/run-now", async (
            string scheduleId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore,
            IGatewayWorkflowStore workflowStore,
            IAutopilotRunLaunchService runLaunchService) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAutopilotSchedule? existing = autopilotStore.GetSchedule(scheduleId);
            if (existing is null)
            {
                return Results.Json(new GatewayErrorResponse("Schedule not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.IsSelf(user, existing.OwnerUserId))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            GatewayScheduledRun? latestRun = autopilotStore.GetLatestNonTerminalRunBySchedule(existing.Id);
            if (latestRun is not null)
            {
                return Results.Json(
                    new GatewayAutopilotRunBlockedResponse(
                        "A non-terminal run already exists for this schedule.",
                        latestRun.Id,
                        latestRun.Status,
                        AutopilotRunStatuses.GetStateCategory(latestRun.Status)),
                    KautukaJsonContext.Default.GatewayAutopilotRunBlockedResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            string inputJson = JsonSerializer.Serialize(
                AutopilotRunInput.Create(existing, workflowStore),
                KautukaJsonContext.Default.JsonObject);

            var run = new GatewayScheduledRun(
                Id: $"srun-{Guid.NewGuid():N}",
                ScheduleId: existing.Id,
                OwnerUserId: existing.OwnerUserId,
                TargetType: existing.TargetType,
                TargetId: existing.TargetId,
                Status: AutopilotRunStatuses.Scheduled,
                TriggerType: "manual",
                ScheduledFor: nowUtc.ToString("O"),
                SessionId: null,
                RuntimeRunId: null,
                PreRunApprovalRequestId: null,
                InputJson: inputJson,
                OutputJson: null,
                Error: null,
                StateCategory: AutopilotRunStateCategories.Executing,
                SkipReason: null,
                BlockingRunId: null,
                BlockingRunStatusSnapshot: null,
                BlockingRunStateCategory: null,
                BlockedAt: null,
                CreatedAt: nowUtc.ToString("O"),
                UpdatedAt: nowUtc.ToString("O"),
                StartedAt: null,
                EndedAt: null);

            if (!autopilotStore.TryInsertRunUnlessNonTerminal(run, out GatewayScheduledRun? concurrentBlocker)
                && concurrentBlocker is not null)
            {
                return Results.Json(
                    new GatewayAutopilotRunBlockedResponse(
                        "A non-terminal run already exists for this schedule.",
                        concurrentBlocker.Id,
                        concurrentBlocker.Status,
                        AutopilotRunStatuses.GetStateCategory(concurrentBlocker.Status)),
                    KautukaJsonContext.Default.GatewayAutopilotRunBlockedResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            TimeZoneInfo timeZone = TimeZoneInfo.Local;
            DateTimeOffset? nextDue = AutopilotCron.GetNextOccurrence(existing.CronExpression, timeZone, nowUtc);
            GatewayAutopilotSchedule refreshed = existing with
            {
                UpdatedAt = nowUtc.ToString("O"),
                LastEvaluatedAt = nowUtc.ToString("O"),
                NextDueAt = nextDue?.ToString("O"),
                LeaseOwner = null,
                LeaseExpiresAt = null,
            };
            _ = autopilotStore.UpdateSchedule(refreshed);

            if (AutopilotLaunchPolicy.RequiresPreRunApproval(existing))
            {
                await runLaunchService.LaunchAsync(existing, run, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _ = runLaunchService.LaunchAsync(existing, run, CancellationToken.None);
            }

            return Results.Json(new GatewaySavedResponse(true), KautukaJsonContext.Default.GatewaySavedResponse);
        });

        app.MapDelete("/api/autopilot/schedules/{scheduleId}", (
            string scheduleId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            bool deleted = autopilotStore.DeleteSchedule(scheduleId, user.Id);
            return deleted
                ? Results.Json(new GatewayDeletedResponse(true), KautukaJsonContext.Default.GatewayDeletedResponse)
                : Results.Json(new GatewayErrorResponse("Schedule not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapGet("/api/autopilot/runs", (
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            string? scheduleId = context.Request.Query.TryGetValue("scheduleId", out var scheduleIdValue)
                ? scheduleIdValue.ToString()
                : null;
            int limit = context.Request.Query.TryGetValue("limit", out var limitValue) && int.TryParse(limitValue, out int parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 250)
                : 50;
            int offset = context.Request.Query.TryGetValue("offset", out var offsetValue) && int.TryParse(offsetValue, out int parsedOffset)
                ? Math.Max(0, parsedOffset)
                : 0;

            IReadOnlyList<GatewayScheduledRun> runs = autopilotStore.ListRuns(user.Id, scheduleId, limit, offset);
            return Results.Json([.. runs], KautukaJsonContext.Default.GatewayScheduledRunArray);
        });

        app.MapGet("/api/autopilot/inbox", (
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            bool unreadOnly = context.Request.Query.TryGetValue("unreadOnly", out var unreadOnlyValue)
                && bool.TryParse(unreadOnlyValue, out bool parsedUnreadOnly)
                && parsedUnreadOnly;
            int limit = context.Request.Query.TryGetValue("limit", out var limitValue) && int.TryParse(limitValue, out int parsedLimit)
                ? Math.Clamp(parsedLimit, 1, 250)
                : 50;
            int offset = context.Request.Query.TryGetValue("offset", out var offsetValue) && int.TryParse(offsetValue, out int parsedOffset)
                ? Math.Max(0, parsedOffset)
                : 0;

            IReadOnlyList<GatewayAutopilotNotification> notifications = autopilotStore.ListNotifications(user.Id, limit, offset, unreadOnly);
            return Results.Json([.. notifications], KautukaJsonContext.Default.GatewayAutopilotNotificationArray);
        });

        app.MapPost("/api/autopilot/inbox/{notificationId}/read", (
            string notificationId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayAutopilotStore autopilotStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            bool updated = autopilotStore.MarkNotificationRead(user.Id, notificationId, DateTimeOffset.UtcNow);
            return updated
                ? Results.Json(new GatewaySavedResponse(true), KautukaJsonContext.Default.GatewaySavedResponse)
                : Results.Json(new GatewayErrorResponse("Notification not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });
    }

    private static IResult SetPaused(string scheduleId, bool paused, HttpContext context, IGatewayAuthGuard auth, IGatewayAutopilotStore autopilotStore)
    {
        if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
        {
            return Results.Unauthorized();
        }

        GatewayAutopilotSchedule? schedule = autopilotStore.GetSchedule(scheduleId);
        if (schedule is null)
        {
            return Results.Json(new GatewayErrorResponse("Schedule not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        }

        if (!GatewayOwnershipAccess.IsSelf(user, schedule.OwnerUserId))
        {
            return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
        }

        GatewayAutopilotSchedule updated = schedule with
        {
            IsPaused = paused,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner = null,
            LeaseExpiresAt = null,
        };
        bool saved = autopilotStore.UpdateSchedule(updated);
        return saved
            ? Results.Json(updated, KautukaJsonContext.Default.GatewayAutopilotSchedule)
            : Results.Json(new GatewayErrorResponse("Failed to update schedule."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status500InternalServerError);
    }

    private static bool IsSupportedTargetType(string targetType)
        => string.Equals(targetType, AutopilotTargetTypes.Agent, StringComparison.Ordinal)
            || string.Equals(targetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal);

    private static bool IsSupportedMode(string mode)
        => string.Equals(mode, AutopilotScheduleModes.Off, StringComparison.Ordinal)
        || string.Equals(mode, AutopilotScheduleModes.On, StringComparison.Ordinal)
        || string.Equals(mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal);

    private static bool ValidateLifecyclePolicies(string expiryPolicy, int? timeoutSeconds, string overlapPolicy, out string? error)
    {
        bool validExpiry = expiryPolicy is AutopilotPreRunApprovalExpiryPolicies.NextOccurrence
            or AutopilotPreRunApprovalExpiryPolicies.Duration
            or AutopilotPreRunApprovalExpiryPolicies.Never;
        bool validOverlap = overlapPolicy is AutopilotOverlapPolicies.SkipAndNotify or AutopilotOverlapPolicies.SkipSilently;
        bool validDuration = expiryPolicy != AutopilotPreRunApprovalExpiryPolicies.Duration
            || timeoutSeconds is >= 60 and <= 604800;
        error = !validExpiry
            ? "Invalid preRunApprovalExpiryPolicy."
            : !validOverlap
                ? "Invalid overlapPolicy."
                : !validDuration
                    ? "preRunApprovalTimeoutSeconds must be between 60 and 604800 for duration policy."
                    : null;
        return error is null;
    }

    private static int? GetOptionalIntProperty(JsonElement body, string name)
        => body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static bool HasTargetAccess(GatewayUser user, IGatewayCatalogStore catalog, IGatewayWorkflowStore workflowStore, string targetType, string targetId)
    {
        if (string.Equals(targetType, "agent", StringComparison.Ordinal))
        {
            return catalog.TryGetAgent(targetId, out GatewayAgent? agent)
                && GatewayOwnershipAccess.CanAccessAgent(user, agent);
        }

        GatewayWorkflowDefinition? definition = workflowStore.GetDefinition(targetId);
        return definition is not null && GatewayOwnershipAccess.IsSelf(user, definition.OwnerUserId);
    }

    private static bool ValidateWorkflowTarget(
        IGatewayWorkflowStore workflowStore,
        IGatewayCatalogStore catalog,
        IAgentFilePathAuthorizationService fileAuthorization,
        GatewayUser owner,
        string targetType,
        string targetId,
        string schedulePromptContext,
        string[] workspaceFilePaths,
        out string? error)
    {
        error = null;
        if (!string.Equals(targetType, "workflow", StringComparison.Ordinal)) return true;
        GatewayWorkflowDefinition? definition = workflowStore.GetDefinition(targetId);
        if (definition is null || definition.Nodes.Length == 0)
        {
            error = "Workflow not found or has no steps.";
            return false;
        }
        try
        {
            JsonObject input = JsonNode.Parse(schedulePromptContext)?.AsObject()
                ?? throw new WorkflowSchemaException("Workflow start values must be a JSON object.");
            WorkflowSchemaSubset.ValidateInstance(definition.InputSchema, input);
            WorkflowSchemaSubset.ValidateInstance(definition.WorkingSchema, input);
            WorkflowEnvelopeCanonical.EnsureSafe(input);
            if (!WorkflowFirstStepConnectorPolicy.ValidateMailConnector(definition, catalog, out string? connectorError))
            {
                error = connectorError;
                return false;
            }
            foreach (string path in workspaceFilePaths)
            {
                AgentFilePathAuthorizationResult authorization = fileAuthorization.Authorize(definition.Nodes[0].AgentId, owner, path);
                if (authorization.Status != AgentFileOperationStatus.Success || !File.Exists(authorization.ResolvedPath))
                {
                    error = "A selected file is not available in the first step agent's workspace.";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or WorkflowSchemaException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string[] NormalizeWorkspaceFilePaths(string[] values)
        => [.. values
            .Select(static value => value.Trim().Replace('\\', '/').Trim('/'))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)];

}

internal sealed record GatewayAutopilotRunBlockedResponse(
    string Error,
    string BlockingRunId,
    string BlockingRunStatus,
    string BlockingRunStateCategory);
