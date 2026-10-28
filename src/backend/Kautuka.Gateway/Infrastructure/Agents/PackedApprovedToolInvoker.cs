using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Runs one packed tool after the human already decided. Unwraps approval wrappers so
/// the body executes; does not continue the model turn.
/// </summary>
internal sealed class PackedApprovedToolInvoker(
    IPlatformConfigurationProvider configProvider,
    IWorkspaceRuntimeTool workspaceTool,
    IAgentToolCatalog toolCatalog,
    IEffectiveSkillManifestResolver skillManifestResolver,
    ILogger<PackedApprovedToolInvoker> logger) : IPackedApprovedToolInvoker
{
    public async ValueTask<string> InvokeAsync(
        string agentId,
        string sessionId,
        string? userId,
        string toolName,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        PlatformConfiguration config = configProvider.GetCurrent();
        AgentDefinition? agent = config.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            throw new InvalidOperationException($"Agent '{agentId}' was not found for packed tool execution.");
        }

        IReadOnlyList<EffectiveSkillManifestEntry> skillManifest = skillManifestResolver.Resolve(config, agent, userId);
        AgentToolContext toolContext = WorkspaceTooling.BuildAgentToolContext(
            workspaceTool,
            agent,
            agentId,
            sessionId,
            userId,
            stageTransitionId: null,
            effectiveSkillManifest: skillManifest);
        IReadOnlyList<AIFunction> tools = await toolCatalog.BuildToolsAsync(toolContext, cancellationToken).ConfigureAwait(false);
        AIFunction? match = tools.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (match is null)
        {
            PackedApprovedToolInvokerLog.ToolNotFound(logger, agentId, sessionId, toolName);
            return PackedToolApproval.CreateOutcomeResultJson(toolName, "error", $"Tool '{toolName}' was not found.");
        }

        AIFunction executable = ConcurrencyLimitedAIFunction.UnwrapApprovalRequired(match);
        AIFunctionArguments arguments = ParseArguments(argumentsJson);
        try
        {
            object? result = await executable.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            return SerializeResult(result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PackedApprovedToolInvokerLog.ToolFailed(logger, ex, agentId, sessionId, toolName);
            return PackedToolApproval.CreateOutcomeResultJson(toolName, "error", ex.Message);
        }
    }

    private static AIFunctionArguments ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new AIFunctionArguments();
        }

        try
        {
            Dictionary<string, object?>? parsed = JsonSerializer.Deserialize(
                argumentsJson,
                KautukaJsonContext.Default.DictionaryStringObject);
            return parsed is null ? new AIFunctionArguments() : new AIFunctionArguments(parsed);
        }
        catch (JsonException)
        {
            return new AIFunctionArguments();
        }
    }

    private static string SerializeResult(object? result)
    {
        if (result is null)
        {
            return "{}";
        }

        if (result is string text)
        {
            return new JsonObject { ["result"] = text }.ToJsonString();
        }

        if (result is JsonNode node)
        {
            return node.ToJsonString();
        }

        try
        {
            return JsonSerializer.Serialize(result, KautukaJsonContext.Default.Object);
        }
        catch (NotSupportedException)
        {
            return new JsonObject { ["result"] = result.ToString() }.ToJsonString();
        }
    }
}

internal static partial class PackedApprovedToolInvokerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Packed tool {ToolName} was not found for agent {AgentId} session {SessionId}")]
    public static partial void ToolNotFound(ILogger logger, string agentId, string sessionId, string toolName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Packed tool {ToolName} failed for agent {AgentId} session {SessionId}")]
    public static partial void ToolFailed(ILogger logger, Exception exception, string agentId, string sessionId, string toolName);
}
