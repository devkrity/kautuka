using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeWorkflowEndpoints(WebApplication app)
    {
        app.MapGet("/api/workflows", (
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            bool archived = false) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            IReadOnlyList<GatewayWorkflowDefinition> definitions = workflowStore.ListDefinitions(user.Id, archived)
                .Select(definition => definition with { HasRuns = workflowStore.HasRuns(definition.Id, user.Id) }).ToArray();
            return Results.Json([.. definitions], KautukaJsonContext.Default.GatewayWorkflowDefinitionArray);
        });

        app.MapPost("/api/workflows/{workflowId}/archive", (string workflowId, HttpContext context, IGatewayAuthGuard auth, IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out _, out IResult? error)) return error!;
            if (!workflowStore.SetArchived(workflowId, user.Id, true))
                return Results.Json(new GatewayErrorResponse("This workflow has a run in progress. Stop it first."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            return Results.NoContent();
        });

        app.MapPost("/api/workflows/{workflowId}/unarchive", (string workflowId, HttpContext context, IGatewayAuthGuard auth, IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out _, out IResult? error)) return error!;
            workflowStore.SetArchived(workflowId, user.Id, false);
            return Results.NoContent();
        });

        app.MapDelete("/api/workflows/{workflowId}", (string workflowId, HttpContext context, IGatewayAuthGuard auth, IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out _, out IResult? error)) return error!;
            if (!workflowStore.DeleteDefinitionIfNeverRun(workflowId, user.Id))
                return Results.Json(new GatewayErrorResponse("This workflow has runs, so it can't be deleted. Archive it instead."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            return Results.NoContent();
        });

        app.MapPost("/api/workflows", (
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryParseDefinition(body, user, existing: null, catalog, out GatewayWorkflowDefinition? definition, out IResult? error))
            {
                return error!;
            }

            if (!WorkflowFirstStepConnectorPolicy.ValidateMailConnector(definition!, catalog, out string? connectorError))
            {
                return Results.Json(new GatewayErrorResponse(connectorError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            GatewayWorkflowDefinition created = workflowStore.CreateDefinition(definition!);
            return Results.Json(WithDiagram(created, catalog), KautukaJsonContext.Default.GatewayWorkflowDefinition, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/workflows/instances", (
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IGatewayApprovalStore approvalStore,
            int limit = 50,
            int offset = 0) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            IReadOnlyList<GatewayWorkflowInstance> instances = workflowStore.ListInstances(user.Id, workflowId: null, limit, offset);
            return Results.Json(ToInstanceResponses(instances, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponseArray);
        });

        app.MapGet("/api/workflows/{workflowId}", (
            string workflowId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out GatewayWorkflowDefinition? definition, out IResult? error))
            {
                return error!;
            }

            return Results.Json(WithDiagram(definition! with { HasRuns = workflowStore.HasRuns(workflowId, user.Id) }, catalog), KautukaJsonContext.Default.GatewayWorkflowDefinition);
        });

        app.MapPut("/api/workflows/{workflowId}", (
            string workflowId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewayWorkflowStore workflowStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out GatewayWorkflowDefinition? existing, out IResult? error))
            {
                return error!;
            }

            if (!TryParseDefinition(body, user, existing, catalog, out GatewayWorkflowDefinition? updated, out error))
            {
                return error!;
            }

            if (!WorkflowFirstStepConnectorPolicy.ValidateMailConnector(updated!, catalog, out string? connectorError))
            {
                return Results.Json(new GatewayErrorResponse(connectorError!), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!workflowStore.UpdateDefinition(updated!))
            {
                return Results.Json(new GatewayErrorResponse("Failed to update workflow."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(WithDiagram(updated!, catalog), KautukaJsonContext.Default.GatewayWorkflowDefinition);
        });

        app.MapGet("/api/workflows/{workflowId}/instances", (
            string workflowId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IGatewayApprovalStore approvalStore,
            int limit = 50,
            int offset = 0) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out _, out IResult? error))
            {
                return error!;
            }

            IReadOnlyList<GatewayWorkflowInstance> instances = workflowStore.ListInstances(user.Id, workflowId, limit, offset);
            return Results.Json(ToInstanceResponses(instances, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponseArray);
        });

        app.MapPost("/api/workflows/{workflowId}/instances", (
            string workflowId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IGatewayApprovalStore approvalStore,
            WorkflowInstanceLauncher launcher) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out GatewayWorkflowDefinition? definition, out IResult? error))
            {
                return error!;
            }

            if (definition!.Archived)
            {
                return Results.Json(new GatewayErrorResponse("This workflow is archived. Restore it to run it."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }

            JsonObject input = body.GetObjectProperty("input");
            WorkflowInstanceStartResult start = launcher.Start(
                definition!,
                user.Id,
                input,
                body.GetNullableStringProperty("correlationKey"),
                body.GetStringArrayProperty("files"));
            return start.Succeeded
                ? Results.Json(ToInstanceResponse(start.Instance!, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponse, statusCode: StatusCodes.Status201Created)
                : Results.Json(new GatewayErrorResponse(start.Error ?? "Couldn't start this run."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: start.StatusCode ?? StatusCodes.Status500InternalServerError);
        });

        app.MapPost("/api/workflows/{workflowId}/steps/{nodeId}/test", async (
            string workflowId,
            string nodeId,
            JsonElement body,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewayWorkflowStore workflowStore,
            IGatewaySessionStore sessions,
            IGatewayApprovalStore approvalStore,
            IChatGatewayService chat,
            IThreadCommandService commands,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (!TryGetOwnedDefinition(workflowStore, workflowId, user, out GatewayWorkflowDefinition? definition, out IResult? error)) return error!;
            GatewayWorkflowNode? node = definition!.Nodes.FirstOrDefault(candidate => string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
            if (node is null) return Results.NotFound();
            if (string.Equals(node.SideEffects, "external", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new GatewayWorkflowStepTestResponse(null, "This step does real things. Try it in a real run.", []), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse);
            if (!catalog.TryGetAgent(node.AgentId, out GatewayAgent? agent) || agent is null || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
                return Results.Json(new GatewayWorkflowStepTestResponse(null, "This step's agent is unavailable.", []), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse, statusCode: StatusCodes.Status400BadRequest);
            JsonObject input = body.GetObjectProperty("input");
            string sessionId = sessions.CreateSession("agent", node.AgentId, user.Id, "workflow_test").Id;
            string runId = Guid.NewGuid().ToString("N");
            try
            {
                JsonObject envelope = input.DeepClone().AsObject();
                string prompt = WorkflowPromptRenderer.RenderStep(node, envelope, definition.Governed);
                var request = new JsonObject
                {
                    ["target_type"] = "agent",
                    ["target_id"] = node.AgentId,
                    ["user_id"] = user.Id,
                    ["message"] = prompt,
                    ["session_id"] = sessionId,
                    ["run_id"] = runId,
                    ["source"] = "workflow_test",
                    ["memoryWrites"] = false,
                };
                string? responseText = null;
                bool paused = false;
                await foreach (NaradPersistedEvent evt in chat.StreamChatAsync(JsonSerializer.SerializeToElement(request, KautukaJsonContext.Default.JsonObject), cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal)
                        || string.Equals(evt.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal)) paused = true;
                    if (string.Equals(evt.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal))
                        responseText = NaradStreamObservation.BuildTerminalDone(evt, "agent", node.AgentId, sessionId).Result.Response;
                    if (string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal))
                        return Results.Json(new GatewayWorkflowStepTestResponse(null, "The step could not finish. Try again.", []), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse);
                }
                GatewayToolApprovalRequest[] pending = approvalStore.ListBySession(sessionId)
                    .Where(item => string.Equals(item.RunId, runId, StringComparison.Ordinal) && item.Decision is null).ToArray();
                if (paused || pending.Length > 0)
                {
                    foreach (GatewayToolApprovalRequest approval in pending)
                        await commands.ResolveInterruptAsync(new ResolveThreadInterruptCommand(approval.Id, ApprovalActionCatalog.StopRun, null, user.Id, user.DisplayName ?? user.Username, null), cancellationToken).ConfigureAwait(false);
                    string message = pending.Length > 0
                        ? $"This step wants to use a tool ({pending[0].ToolName}). Tests stop before any tool runs."
                        : "This step wants to use a tool. Tests stop before any tool runs.";
                    return Results.Json(new GatewayWorkflowStepTestResponse(null, message, []), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse);
                }
                try
                {
                    JsonObject results = WorkflowStepOutputParser.Parse(responseText ?? string.Empty, node, definition.Governed, out IReadOnlyList<string> droppedKeys);
                    return Results.Json(new GatewayWorkflowStepTestResponse(results, null, [.. droppedKeys]), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse);
                }
                catch (WorkflowSchemaException)
                {
                    return Results.Json(new GatewayWorkflowStepTestResponse(null, "The step did not return the expected results.", []), KautukaJsonContext.Default.GatewayWorkflowStepTestResponse);
                }
            }
            finally { sessions.DeleteSession(sessionId); }
        });

        app.MapGet("/api/workflows/{workflowId}/instances/{instanceId}", (
            string workflowId,
            string instanceId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IGatewayApprovalStore approvalStore) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedInstance(workflowStore, workflowId, instanceId, user, out GatewayWorkflowInstance? instance, out IResult? error))
            {
                return error!;
            }

            return Results.Json(ToInstanceResponse(instance!, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponse);
        });

        app.MapPost("/api/workflows/{workflowId}/instances/{instanceId}/stop", async (
            string workflowId,
            string instanceId,
            HttpContext context,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IGatewayApprovalStore approvalStore,
            IWorkflowExecutionService executionService,
            IApprovalResolutionService approvalResolutionService,
            IWorkflowNaradPublisher naradPublisher) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedInstance(workflowStore, workflowId, instanceId, user, out GatewayWorkflowInstance? instance, out IResult? error))
            {
                return error!;
            }

            GatewayToolApprovalRequest? openReview = approvalStore.ListPending(user.Id, adminAll: false)
                .FirstOrDefault(request => string.Equals(request.InstanceId, instanceId, StringComparison.Ordinal)
                    && (string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
                        || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal)));
            if (openReview is not null)
            {
                await approvalResolutionService.ResolveAsync(new ApprovalResolutionCommand(
                    openReview.Id,
                    Approved: false,
                    Reason: null,
                    DecidedByUserId: user.Id,
                    Source: HitlResolutionSource.Browser,
                    UserId: user.Id,
                    StopRun: true,
                    Action: ApprovalActionCatalog.StopRun), CancellationToken.None).ConfigureAwait(false);
                executionService.Cancel(instanceId);
                GatewayWorkflowInstance? resolvedStop = workflowStore.GetInstance(instanceId);
                if (resolvedStop is not null && string.Equals(resolvedStop.Status, WorkflowInstanceStatuses.Cancelled, StringComparison.Ordinal))
                {
                    return Results.Json(ToInstanceResponse(resolvedStop, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponse);
                }
            }

            naradPublisher.Publish(instance!, NaradEventTypes.WorkflowStopRequested, new JsonObject());
            executionService.Cancel(instanceId);
            if (!workflowStore.TryRequestStop(instanceId, user.Id, DateTimeOffset.UtcNow, out GatewayWorkflowInstance? stopped) || stopped is null)
            {
                return Results.Json(new GatewayErrorResponse("Failed to stop instance."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status500InternalServerError);
            }

            naradPublisher.Publish(stopped, NaradEventTypes.WorkflowCancelled, new JsonObject
            {
                ["code"] = WorkflowFailureCodes.WorkflowStopped,
            });
            return Results.Json(ToInstanceResponse(stopped, approvalStore), KautukaJsonContext.Default.GatewayWorkflowInstanceResponse);
        });
    }

    private static bool TryGetOwnedDefinition(
        IGatewayWorkflowStore workflowStore,
        string workflowId,
        GatewayUser user,
        out GatewayWorkflowDefinition? definition,
        out IResult? error)
    {
        definition = workflowStore.GetDefinition(workflowId);
        if (definition is null)
        {
            error = Results.Json(new GatewayErrorResponse("Workflow not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            return false;
        }

        if (!GatewayOwnershipAccess.IsSelf(user, definition.OwnerUserId))
        {
            error = Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            definition = null;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetOwnedInstance(
        IGatewayWorkflowStore workflowStore,
        string workflowId,
        string instanceId,
        GatewayUser user,
        out GatewayWorkflowInstance? instance,
        out IResult? error)
    {
        instance = workflowStore.GetInstance(instanceId);
        if (instance is null || !string.Equals(instance.WorkflowId, workflowId, StringComparison.Ordinal))
        {
            error = Results.Json(new GatewayErrorResponse("Instance not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            instance = null;
            return false;
        }

        if (!GatewayOwnershipAccess.IsSelf(user, instance.OwnerUserId))
        {
            error = Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            instance = null;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseDefinition(
        JsonElement body,
        GatewayUser user,
        GatewayWorkflowDefinition? existing,
        IGatewayCatalogStore catalog,
        out GatewayWorkflowDefinition? definition,
        out IResult? error)
    {
        definition = null;
        string name = body.GetStringProperty("name", existing?.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = Results.Json(new GatewayErrorResponse("name is required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        if (!TryParseNodes(body, catalog, user, out GatewayWorkflowNode[] nodes, out error))
        {
            return false;
        }

        string now = DateTimeOffset.UtcNow.ToString("O");
        var parsed = new GatewayWorkflowDefinition(
            existing?.Id ?? $"wf-{Guid.NewGuid():N}",
            user.Id,
            name,
            body.GetBoolProperty("governed", existing?.Governed ?? false),
            existing is null ? 1 : existing.Revision + 1,
            CloneObject(body.GetObjectProperty("inputSchema")),
            CloneObject(body.GetObjectProperty("workingSchema")),
            CloneObject(body.GetObjectProperty("outputSchema")),
            nodes,
            existing?.CreatedAt ?? now,
            now,
            existing?.Archived ?? false);

        try
        {
            WorkflowDefinitionValidator.Validate(WorkflowDefinitionCodec.ToDraft(parsed));
        }
        catch (WorkflowSchemaException ex)
        {
            error = Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        definition = parsed;
        error = null;
        return true;
    }

    private static bool TryParseNodes(
        JsonElement body,
        IGatewayCatalogStore catalog,
        GatewayUser user,
        out GatewayWorkflowNode[] nodes,
        out IResult? error)
    {
        nodes = [];
        if (!body.TryGetProperty("nodes", out JsonElement nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            error = Results.Json(new GatewayErrorResponse("nodes is required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        var parsed = new List<GatewayWorkflowNode>();
        foreach (JsonElement node in nodesElement.EnumerateArray())
        {
            string nodeId = node.GetStringProperty("nodeId", string.Empty).Trim();
            string agentId = node.GetStringProperty("agentId", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(agentId))
            {
                error = Results.Json(new GatewayErrorResponse("Each node needs nodeId and agentId."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                return false;
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                error = Results.Json(
                    new GatewayErrorResponse($"Node '{nodeId}' agent '{agentId}' is not available."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
                return false;
            }

            parsed.Add(new GatewayWorkflowNode(
                nodeId,
                agentId,
                node.GetStringProperty("promptTemplate", string.Empty),
                node.GetStringProperty("outputKind", "schema"),
                node.TryGetProperty("outputSchema", out JsonElement schema) && schema.ValueKind == JsonValueKind.Object
                    ? CloneObject(JsonNode.Parse(schema.GetRawText())!.AsObject())
                    : null,
                ParseMaps(node),
                node.GetStringProperty("sideEffects", "none"),
                node.GetBoolProperty("memoryWrites", false),
                node.GetBoolProperty("requiresReview", false),
                node.GetIntProperty("maxRetries", 0),
                ParseSwitchCases(node),
                node.GetStringProperty("defaultGoTo", string.Empty).Trim() is { Length: > 0 } defaultGoTo
                    ? defaultGoTo
                    : null));
        }

        nodes = [.. parsed];
        error = null;
        return true;
    }

    private static GatewayWorkflowDefinition WithDiagram(
        GatewayWorkflowDefinition definition,
        IGatewayCatalogStore catalog)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.AgentId) || !names.TryAdd(node.AgentId, string.Empty))
            {
                continue;
            }

            if (catalog.TryGetAgent(node.AgentId, out GatewayAgent? agent) && agent is not null && !string.IsNullOrWhiteSpace(agent.Name))
            {
                names[node.AgentId] = agent.Name;
            }
            else
            {
                names.Remove(node.AgentId);
            }
        }

        return definition with { Diagram = WorkflowMafGraphFactory.Diagram(definition.Nodes, names) };
    }

    private static GatewayWorkflowMap[] ParseMaps(JsonElement node)
    {
        if (!node.TryGetProperty("maps", out JsonElement maps) || maps.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. maps.EnumerateArray()
            .Select(static item => new GatewayWorkflowMap(
                item.GetStringProperty("from", string.Empty),
                item.GetStringProperty("to", string.Empty)))
            .Where(static map => !string.IsNullOrWhiteSpace(map.From) && !string.IsNullOrWhiteSpace(map.To))];
    }

    private static GatewayWorkflowSwitchCase[] ParseSwitchCases(JsonElement node)
    {
        if (!node.TryGetProperty("switchCases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. cases.EnumerateArray()
            .Select(static item => new GatewayWorkflowSwitchCase(
                item.GetStringProperty("pointer", string.Empty),
                item.GetStringProperty("op", string.Empty),
                item.TryGetProperty("value", out JsonElement value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? value.ToString()
                    : item.GetStringProperty("value", string.Empty),
                item.GetStringProperty("goTo", string.Empty)))
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.Pointer) && !string.IsNullOrWhiteSpace(rule.Op) && !string.IsNullOrWhiteSpace(rule.GoTo))];
    }

    private static JsonObject CloneObject(JsonObject value)
        => JsonNode.Parse(value.ToJsonString())!.AsObject();

    private static GatewayWorkflowInstanceResponse[] ToInstanceResponses(
        IReadOnlyList<GatewayWorkflowInstance> instances,
        IGatewayApprovalStore approvalStore)
        => [.. instances.Select(instance => ToInstanceResponse(instance, approvalStore))];

    private static GatewayWorkflowInstanceResponse ToInstanceResponse(
        GatewayWorkflowInstance instance,
        IGatewayApprovalStore approvalStore)
        => new(
            instance.Id,
            instance.WorkflowId,
            instance.DefinitionRevision,
            instance.OwnerUserId,
            instance.CorrelationKey,
            instance.Status,
            instance.StopRequested,
            instance.CurrentNodeId,
            ParseJsonElement(instance.Envelope.ToJsonString()),
            instance.EnvelopeHash,
            instance.EnvelopeRevision,
            ParseJsonElement(JsonSerializer.Serialize(instance.Snapshot, KautukaJsonContext.Default.GatewayWorkflowDefinition)),
            [.. instance.Steps.Select(attempt => attempt with
            {
                ReviewNote = FindReviewNote(instance, attempt, approvalStore),
            })],
            instance.FailureCode,
            instance.Error,
            instance.CreatedAt,
            instance.UpdatedAt,
            instance.StartedAt,
            instance.EndedAt);

    private static string? FindReviewNote(
        GatewayWorkflowInstance instance,
        GatewayWorkflowStepAttempt attempt,
        IGatewayApprovalStore approvalStore)
    {
        GatewayWorkflowStepAttempt? lastFinished = instance.Steps
            .Where(step => string.Equals(step.NodeId, attempt.NodeId, StringComparison.Ordinal)
                && step.Attempt < attempt.Attempt
                && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal))
            .OrderByDescending(step => step.Attempt)
            .FirstOrDefault();
        return lastFinished is not null && attempt.Attempt == lastFinished.Attempt + 1
            ? WorkflowReviewNotes.ForAttempt(instance, attempt, approvalStore)
            : null;
    }

    private static JsonElement ParseJsonElement(string json)
        => JsonSerializer.Deserialize(json, KautukaJsonContext.Default.JsonElement);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Error, Message = "Failed to start workflow instance for {WorkflowId}")]
    private static partial void LogWorkflowInstanceStartFailed(ILogger logger, Exception exception, string workflowId);
}
