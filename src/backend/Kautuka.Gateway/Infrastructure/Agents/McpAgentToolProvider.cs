using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Resolves and launches MCP server processes for the active tool context.
/// </summary>
internal sealed partial class McpAgentToolProvider(
    IPlatformConfigurationProvider configProvider,
    CredentialResolverService credentialResolver,
    IMcpHttpClientFactory mcpHttpClientFactory,
    IManagerGrpcService workspaceGrpcService,
    IToolExecutionPolicyService toolExecutionPolicyService,
    ILogger<McpAgentToolProvider> logger,
    ConnectorOAuthService? connectorOAuth = null,
    IConversationAssetIngestionService? documentIngestion = null,
    GatewayState? gatewayState = null,
    MailImageHandoff? mailImages = null) : IAgentToolSurfaceContributor
{
    public string SurfaceSource => AgentToolSurfaceSources.Mcp;

    public async ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        AgentToolSurfaceContribution contribution = await ContributeAsync(context, ct).ConfigureAwait(false);
        return await HydrateAsync(context, contribution.Entries, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<ResolvedAgentConnectorBinding> ResolveAgentConnectorBindings(
        AgentDefinition definition,
        IToolExecutionPolicyService toolExecutionPolicyService)
    {
        return [.. definition.McpBindings
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.ConnectorId))
            .Where(binding => toolExecutionPolicyService.EvaluateConnectorExposure(definition, NormalizeBindingContext(binding.Context)).Allowed)
            .GroupBy(static binding => binding.ConnectorId, StringComparer.Ordinal)
            .Select(static group => new ResolvedAgentConnectorBinding(group.Key, NormalizeBindingContext(group.First().Context)))];
    }

    internal static WorkspaceMcpExecutionContext NormalizeBindingContext(string? context)
    {
        if (string.Equals(context, "AgentContext", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceMcpExecutionContext.AgentSandbox;
        }

        if (string.Equals(context, "OwnContextWithAgentFiles", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceMcpExecutionContext.IsolatedWithAgentFiles;
        }

        return WorkspaceMcpExecutionContext.Isolated;
    }
}

internal sealed record ResolvedAgentConnectorBinding(
    string ConnectorId,
    WorkspaceMcpExecutionContext ExecutionContext);

internal static partial class McpAgentToolProviderLog
{
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "[MCP] Failed to load tools from connector '{ServerId}': {Message} | InnerException: {InnerMessage}")]
    public static partial void LoadToolsFailed(ILogger logger, Exception exception, string serverId, string message, string innerMessage);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "[MCP] Connector '{ServerId}' resolved env keys: {EnvKeys}; executionScope={ExecutionScope}")]
    public static partial void ConnectorEnvResolved(ILogger logger, string serverId, string envKeys, string executionScope);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "[MCP] Loaded {ToolCount} tools from connector '{ServerId}'.")]
    public static partial void ConnectorToolsLoaded(ILogger logger, string serverId, int toolCount);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "[MCP] Total MCP tools available for agent '{AgentId}': {ToolCount}")]
    public static partial void TotalToolsAvailable(ILogger logger, string agentId, int toolCount);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Invoking connector tool for agent {AgentId}, session {SessionId}, user {UserId}; connectorId={ConnectorId}, toolName={ToolName}, agentToolCallerSessionId={AgentToolCallerSessionId}, workspaceScopeType={ScopeType}, workspaceScopeId={ScopeId}")]
    private static partial void ConnectorToolInvokingCore(ILogger logger, string agentId, string sessionId, string? userId, string connectorId, string toolName, string? agentToolCallerSessionId, string scopeType, string scopeId);

    public static void ConnectorToolInvoking(ILogger logger, string agentId, string sessionId, string? userId, string connectorId, string toolName, string? agentToolCallerSessionId, string scopeType, string scopeId)
    {
        ConnectorToolInvokingCore(logger, agentId, sessionId, userId, connectorId, toolName, agentToolCallerSessionId, scopeType, scopeId);
    }

}
