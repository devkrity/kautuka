using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private const string exposedAgentToolLeafValidationMessage = "Agents exposed as tools cannot invoke other agents as tools. Remove agent tool selections or disable Expose as tool.";

    private static void MapAgentSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/agents", (IGatewayCatalogStore catalog, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            return Results.Json(
                new GatewayAgentsResponse([..
                    catalog.ListAgents()
                        .Where(agent => GatewayOwnershipAccess.CanAccessAgent(user, agent))
                        .Select(agent => GatewayAgentSettingsMapper.ToListItem(
                        agent,
                        settings.GetAgentPersonaProfile(agent.Id)))]),
                KautukaJsonContext.Default.GatewayAgentsResponse);
        });

        app.MapPut("/api/settings/agents/{agentId}", async (string agentId, JsonElement body, IGatewayCatalogStore catalog, IChannelHostOwnerTargetSyncNotifier channelSyncNotifier, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
{
    IGatewaySettingsStore settings = context.RequestServices.GetRequiredService<IGatewaySettingsStore>();
    if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
    {
        return Results.Unauthorized();
    }

    bool hadExistingAgent = catalog.TryGetAgent(agentId, out GatewayAgent? existingAgent);
    if (hadExistingAgent && !GatewayOwnershipAccess.CanManageAgent(user, existingAgent))
    {
        return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
    }

    GatewayAgentUpsertRequest request;
    try
    {
        request = body.Deserialize(KautukaJsonContext.Default.GatewayAgentUpsertRequest) ?? new GatewayAgentUpsertRequest();
    }
    catch (JsonException)
    {
        return Results.BadRequest(new GatewayErrorResponse("Invalid agent payload."));
    }

    GatewayAgent agent;
    try
    {
        if (request.ToolExposure?.ExposedAsTool == true && request.ApprovalConfig?.AgentToolIds is { Length: > 0 })
        {
            return Results.BadRequest(new GatewayErrorResponse(exposedAgentToolLeafValidationMessage));
        }

        agent = await catalog.SaveAgentAsync(agentId, request, user.Id, ct).ConfigureAwait(false);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new GatewayErrorResponse(ex.Message));
    }
    _ = channelSyncNotifier.TryNotify(user.Id, hadExistingAgent ? "agent-updated" : "agent-created");

    return Results.Json(
        new GatewayAgentResponse(GatewayAgentSettingsMapper.ToListItem(
            agent,
            settings.GetAgentPersonaProfile(agent.Id))),
        KautukaJsonContext.Default.GatewayAgentResponse);
});

        app.MapDelete("/api/settings/agents/{agentId}", async (string agentId, IGatewayCatalogStore catalog, IChannelHostOwnerTargetSyncNotifier channelSyncNotifier, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || agent is null)
            {
                return Results.Json(new GatewayErrorResponse("Agent not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanManageAgent(user, agent))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string[] dependentAgentNames =
            [..
                catalog.ListAgents()
                    .Where(candidate =>
                        !string.Equals(candidate.Id, agentId, StringComparison.Ordinal)
                        && GatewayAgentSettingsMapper.NormalizeStoredApprovalConfig(candidate.ApprovalConfig)
                            .AgentToolIds.Contains(agentId, StringComparer.Ordinal))
                    .Select(candidate => candidate.Name)
                    .Order(StringComparer.OrdinalIgnoreCase)];
            if (dependentAgentNames.Length > 0)
            {
                return Results.Json(
                    new GatewayErrorResponse($"Agent is still used as a tool by: {string.Join(", ", dependentAgentNames)}."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            bool deleted = await catalog.DeleteAgentAsync(agentId, ct).ConfigureAwait(false);
            if (deleted)
            {
                _ = channelSyncNotifier.TryNotify(user.Id, "agent-deleted");
                return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
            }

            return Results.Json(new GatewayErrorResponse("Agent not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapGet("/api/settings/agents/{agentId}/tool-policy", (
            string agentId,
            IGatewayCatalogStore catalog,
            IToolExecutionPolicyService toolExecutionPolicyService,
            IGatewayAuthGuard auth,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || agent is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Agent not found"),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                return Results.Json(
                    new GatewayErrorResponse("Forbidden."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            AgentDefinition policyAgent = BuildPolicyAgent(agent);

            GatewayToolPolicyCategoryProjection[] categories =
            [
                BuildCategoryProjection(ToolPolicyCategory.Connector, toolExecutionPolicyService, policyAgent),
                BuildCategoryProjection(ToolPolicyCategory.Skill, toolExecutionPolicyService, policyAgent),
                BuildCategoryProjection(ToolPolicyCategory.SuperExec, toolExecutionPolicyService, policyAgent),
                BuildCategoryProjection(ToolPolicyCategory.Other, toolExecutionPolicyService, policyAgent),
            ];

            return Results.Json(
                new GatewayAgentToolPolicyProjectionResponse(agentId, categories),
                KautukaJsonContext.Default.GatewayAgentToolPolicyProjectionResponse);
        });

        app.MapGet("/api/settings/agents/{agentId}/revisions", (
            string agentId,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || agent is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Agent not found"),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                return Results.Json(
                    new GatewayErrorResponse("Forbidden."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            GatewayAgentRevisionsResponse? response = catalog.TryListAgentRevisions(agentId);
            if (response is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Agent not found"),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(response, KautukaJsonContext.Default.GatewayAgentRevisionsResponse);
        });

        app.MapGet("/api/settings/agents/{agentId}/revisions/{revision}/diff", (
            string agentId,
            long revision,
            long? from,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || agent is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Agent not found"),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanAccessAgent(user, agent))
            {
                return Results.Json(
                    new GatewayErrorResponse("Forbidden."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            GatewayAgentRevisionDiffResponse? response = catalog.TryGetAgentRevisionDiff(agentId, revision, from);
            if (response is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Revision not found"),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(response, KautukaJsonContext.Default.GatewayAgentRevisionDiffResponse);
        });
    }

    private static GatewayToolPolicyCategoryProjection BuildCategoryProjection(
        ToolPolicyCategory category,
        IToolExecutionPolicyService toolExecutionPolicyService,
        AgentDefinition agent)
    {
        ToolPolicyDecision exposure = toolExecutionPolicyService.EvaluateExposure(category, agent);
        ToolPolicyDecision execution = toolExecutionPolicyService.ValidateExecution(category, agent);

        return new GatewayToolPolicyCategoryProjection(
            Category: ToPolicyCategoryCode(category),
            Exposure: ToPolicyDecisionProjection(exposure),
            Execution: ToPolicyDecisionProjection(execution));
    }

    private static GatewayToolPolicyDecisionProjection ToPolicyDecisionProjection(ToolPolicyDecision decision)
        => new(
            decision.Allowed,
            ToPolicyReasonCode(decision.ReasonCode),
            decision.Message);

    private static string ToPolicyCategoryCode(ToolPolicyCategory category)
        => category switch
        {
            ToolPolicyCategory.Connector => "connector",
            ToolPolicyCategory.Skill => "skill",
            ToolPolicyCategory.SuperExec => "superexec",
            ToolPolicyCategory.Other => "other",
            _ => "other",
        };

    private static string ToPolicyReasonCode(ToolPolicyReasonCode reasonCode)
        => reasonCode switch
        {
            ToolPolicyReasonCode.Allowed => "allowed",
            ToolPolicyReasonCode.SandboxRequired => "sandbox_required",
            ToolPolicyReasonCode.SuperExecDisabled => "superexec_disabled",
            ToolPolicyReasonCode.UncategorizedCategoryDenied => "uncategorized_category_denied",
            ToolPolicyReasonCode.UnknownCategoryDenied => "unknown_category_denied",
            _ => "unknown_category_denied",
        };

    private static AgentDefinition BuildPolicyAgent(GatewayAgent agent)
        => new()
        {
            SuperExecMode = agent.SuperExecMode,
            Sandbox = new AgentSandboxDefinition
            {
                Enabled = ResolveSandboxEnabled(agent.Sandbox),
            },
        };

    private static bool ResolveSandboxEnabled(JsonObject? sandbox)
    {
        if (sandbox is null)
        {
            return true;
        }

        if (!sandbox.TryGetPropertyValue("enabled", out JsonNode? enabledNode)
            || enabledNode is not JsonValue enabledValue
            || !enabledValue.TryGetValue<bool>(out bool enabled))
        {
            return true;
        }

        return enabled;
    }
}
