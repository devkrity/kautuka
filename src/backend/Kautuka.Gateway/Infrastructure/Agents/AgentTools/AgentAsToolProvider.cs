using System.ComponentModel;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents.AgentTools;

/// <summary>
/// Builds <see cref="AIFunction"/> wrappers for every exposed agent that the current caller
/// is permitted to invoke as a tool.
/// </summary>
internal sealed class AgentAsToolProvider(
    IPlatformConfigurationProvider configProvider,
    IAgentToolInvocationOrchestrator orchestrator,
    IToolExecutionPolicyService toolExecutionPolicyService,
    IRunLimitResolver? runLimitResolver = null) : IAgentToolSurfaceContributor
{
    public string SurfaceSource => AgentToolSurfaceSources.Aaat;

    public ValueTask<AgentToolSurfaceContribution> ContributeAsync(AgentToolContext context, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(context);

        if (!TryResolveTargets(context, out AgentDefinition[] targets, out _))
        {
            return ValueTask.FromResult(new AgentToolSurfaceContribution([], Complete: true));
        }

        return ValueTask.FromResult(new AgentToolSurfaceContribution(
            [.. targets.Select(target =>
            {
                string toolName = $"invoke_agent_{SanitizeName(target.Name)}";
                string description = string.IsNullOrWhiteSpace(target.ToolExposure.Description)
                    ? $"Invoke the '{target.Name}' agent with a message and receive its response."
                    : target.ToolExposure.Description;
                return AgentToolSurfaceEntry.Describe(SurfaceSource, toolName, toolName, description, "{}");
            })],
            Complete: true));
    }

    public ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryResolveTargets(context, out AgentDefinition[] targets, out AgentDefinition? callerDef))
        {
            return ValueTask.FromResult<IEnumerable<AIFunction>>([]);
        }

        var tools = new List<AIFunction>(targets.Length);
        foreach (AgentDefinition target in targets)
        {
            AIFunction tool = BuildTool(context, target);
            ToolApprovalDirective directive = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
                Identity: new ToolIdentity(
                    Category: ToolPolicyCategory.Other,
                    ToolName: tool.Name),
                Agent: callerDef,
                TargetAgentId: target.Id));
            tools.Add(directive.RequiresApproval ? new ApprovalRequiredAIFunction(tool) : tool);
        }

        return ValueTask.FromResult<IEnumerable<AIFunction>>(tools);
    }

    private bool TryResolveTargets(
        AgentToolContext context,
        out AgentDefinition[] targets,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentDefinition? callerDef)
    {
        PlatformConfiguration config = configProvider.GetCurrent();
        IReadOnlyList<string> agentToolIds = context.AgentDefinition.ApprovalPolicy?.AgentToolIds ?? [];
        targets = [.. config.Agents
            .Where(agent => agent.ToolExposure.ExposedAsTool)
            .Where(agent => agentToolIds.Any(id => string.Equals(id, agent.Id, StringComparison.Ordinal)))
            .Where(agent => GatewayOwnershipAccess.CallerCanUse(context.UserId, agent.Scope, agent.OwnerUserId))];
        if (targets.Length == 0)
        {
            callerDef = null;
            return false;
        }

        callerDef = config.Agents
            .FirstOrDefault(a => string.Equals(a.Id, context.AgentId, StringComparison.Ordinal));
        return callerDef is not null;
    }

    private AIFunction BuildTool(AgentToolContext context, AgentDefinition target)
    {
        string toolName = $"invoke_agent_{SanitizeName(target.Name)}";
        string description = string.IsNullOrWhiteSpace(target.ToolExposure.Description)
            ? $"Invoke the '{target.Name}' agent with a message and receive its response."
            : target.ToolExposure.Description;

        string callerAgentId = context.AgentId;
        string targetAgentId = target.Id;
        string? userId = context.UserId;
        TimeSpan maxRunDuration = runLimitResolver?.ResolveForAgent(targetAgentId).MaxRunDuration ?? TimeSpan.Zero;

        return AIFunctionFactory.Create(
            async ([Description("The message to send to the agent.")] string message,
                   CancellationToken cancellationToken) =>
            {
                string callerToolCallId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(callerToolCallId))
                {
                    throw new InvalidOperationException($"AAAT tool '{toolName}' was invoked without a framework call id.");
                }

                AgentToolCallerContextState callerContext = AgentToolCallerContext.TryGetRegistered(context.SessionId, callerToolCallId)
                    ?? throw new InvalidOperationException($"AAAT caller context is missing for session '{context.SessionId}' and call '{callerToolCallId}'.");
                if (!string.Equals(callerContext.CallerToolName, toolName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AAAT caller call '{callerToolCallId}' was registered for tool '{callerContext.CallerToolName}', not '{toolName}'.");
                }

                return await orchestrator.InvokeAsync(
                    callerAgentId,
                    targetAgentId,
                    message,
                    userId,
                    context.SessionId,
                    callerContext,
                    cancellationToken,
                    maxRunDuration).ConfigureAwait(false);
            },
            toolName,
            description,
            serializerOptions: null);
    }

    private static string SanitizeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (c is ' ' or '-' or '_')
                sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }
}
