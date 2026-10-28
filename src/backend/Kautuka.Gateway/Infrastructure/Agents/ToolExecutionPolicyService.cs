namespace Kautuka.Gateway.Infrastructure.Agents;

using Kautuka.Manager.Protos;

internal enum ToolPolicyCategory
{
    Connector,
    Skill,
    SuperExec,
    Other,
}

internal enum ToolPolicyReasonCode
{
    Allowed,
    SandboxRequired,
    SuperExecDisabled,
    UncategorizedCategoryDenied,
    UnknownCategoryDenied,
}

internal readonly record struct ToolPolicyDecision(
    bool Allowed,
    ToolPolicyReasonCode ReasonCode,
    string Message);

internal readonly record struct ToolApprovalDirective(
    bool Include,
    bool RequiresApproval);

internal readonly record struct ToolIdentity(
    ToolPolicyCategory Category,
    string ToolName,
    string? SourceId = null);

internal readonly record struct ToolApprovalRequest(
    ToolIdentity Identity,
    ApprovalPolicy? ApprovalPolicy = null,
    SkillAttachmentMode? SkillMode = null,
    AgentDefinition? Agent = null,
    string? TargetAgentId = null);

internal interface IToolExecutionPolicyService
{
    ToolPolicyDecision EvaluateExposure(ToolPolicyCategory category, AgentDefinition agent);

    ToolPolicyDecision ValidateExecution(ToolPolicyCategory category, AgentDefinition agent);

    ToolPolicyDecision EvaluateConnectorExposure(AgentDefinition agent, WorkspaceMcpExecutionContext executionContext);

    ToolApprovalDirective EvaluateApproval(ToolApprovalRequest request);
}

internal sealed class ToolExecutionPolicyService : IToolExecutionPolicyService
{
    private readonly Dictionary<ToolPolicyCategory, IToolApprovalStrategy> approvalStrategies;

    public ToolExecutionPolicyService()
    {
        approvalStrategies = new Dictionary<ToolPolicyCategory, IToolApprovalStrategy>
        {
            [ToolPolicyCategory.Connector] = new ConnectorApprovalStrategy(),
            [ToolPolicyCategory.Skill] = new SkillApprovalStrategy(),
            [ToolPolicyCategory.SuperExec] = new SuperExecApprovalStrategy(),
            [ToolPolicyCategory.Other] = new OtherApprovalStrategy(),
        };
    }

    public ToolPolicyDecision EvaluateExposure(ToolPolicyCategory category, AgentDefinition agent)
        => Evaluate(category, agent);

    public ToolPolicyDecision ValidateExecution(ToolPolicyCategory category, AgentDefinition agent)
        => Evaluate(category, agent);

    public ToolPolicyDecision EvaluateConnectorExposure(AgentDefinition agent, WorkspaceMcpExecutionContext executionContext)
    {
        ToolPolicyDecision categoryDecision = Evaluate(ToolPolicyCategory.Connector, agent);
        if (!categoryDecision.Allowed)
        {
            return categoryDecision;
        }

        if (executionContext == WorkspaceMcpExecutionContext.AgentSandbox && !(agent.Sandbox?.Enabled ?? false))
        {
            return Denied(
                ToolPolicyReasonCode.SandboxRequired,
                "Agent sandbox execution context requires sandbox to be enabled.");
        }

        return Allowed("Allowed by policy.");
    }

    public ToolApprovalDirective EvaluateApproval(ToolApprovalRequest request)
    {
        return approvalStrategies.TryGetValue(request.Identity.Category, out IToolApprovalStrategy? strategy)
            ? strategy.Evaluate(request)
            : NoApprovalStrategy.Instance.Evaluate(request);
    }

    private static bool RequiresApprovalForSuperExecCore(AgentDefinition agent)
        => string.Equals(agent.SuperExecMode, "ask", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresApprovalForAgentToolCore(AgentDefinition callerAgent, string targetAgentId)
        => callerAgent.ApprovalPolicy?.ApprovalRequiredAgentToolIds.Any(id => string.Equals(id, targetAgentId, StringComparison.Ordinal)) == true;

    internal static ToolApprovalDirective EvaluateMcpToolApprovalCore(ApprovalPolicy policy, string serverId, string toolName)
    {
        IReadOnlyList<string>? disabledFunctions = policy.DisabledMcpFunctions.TryGetValue(serverId, out IReadOnlyList<string>? disabledList)
            ? disabledList
            : null;

        if (disabledFunctions is { Count: > 0 } && disabledFunctions.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return new ToolApprovalDirective(Include: false, RequiresApproval: false);
        }

        bool policyRequiresThisServer = policy.RequireMcpIds.Contains(serverId, StringComparer.OrdinalIgnoreCase);

        bool policyHasFunctionFilter = policy.RequireMcpFunctionFilters.TryGetValue(serverId, out IReadOnlyList<string>? functionFilters)
            && functionFilters is { Count: > 0 };

        bool wrapAll = policyRequiresThisServer && !policyHasFunctionFilter;
        bool wrapThis = wrapAll
            || (functionFilters is { Count: > 0 } && functionFilters.Any(pattern => MatchesGlobToolName(toolName, pattern)));

        return new ToolApprovalDirective(Include: true, RequiresApproval: wrapThis);
    }

    internal static bool MatchesGlobToolName(string toolName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        if (string.Equals(pattern, "*", StringComparison.Ordinal))
            return true;

        if (!pattern.Contains('*', StringComparison.Ordinal))
            return string.Equals(toolName, pattern, StringComparison.OrdinalIgnoreCase);

        string[] parts = pattern.Split('*');
        if (parts.Length == 2)
        {
            string prefix = parts[0];
            string suffix = parts[1];
            if (toolName.Length < prefix.Length + suffix.Length)
                return false;
            return toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && toolName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return toolName.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase);
    }

    private static ToolPolicyDecision Evaluate(ToolPolicyCategory category, AgentDefinition agent)
    {
        if (!IsKnownCategory(category))
        {
            return Denied(
                ToolPolicyReasonCode.UnknownCategoryDenied,
                "Tool category is unknown and is denied by policy.");
        }

        if (category == ToolPolicyCategory.Other)
        {
            return Denied(
                ToolPolicyReasonCode.UncategorizedCategoryDenied,
                "Uncategorized tools are denied by policy.");
        }

        // Connector sandbox semantics are evaluated via execution context
        // (isolated / isolated-with-agent-files / agent-sandbox) in EvaluateConnectorExposure.
        if (category is ToolPolicyCategory.Skill or ToolPolicyCategory.SuperExec or ToolPolicyCategory.Other
            && !agent.Sandbox.Enabled)
        {
            return Denied(
                ToolPolicyReasonCode.SandboxRequired,
                "Sandbox is required by policy for this capability.");
        }

        if (category == ToolPolicyCategory.SuperExec
            && string.Equals(agent.SuperExecMode, "disabled", StringComparison.Ordinal))
        {
            return Denied(
                ToolPolicyReasonCode.SuperExecDisabled,
                "SuperExec is disabled for this agent.");
        }

        return Allowed("Allowed by policy.");
    }

    private static bool IsKnownCategory(ToolPolicyCategory category)
        => category switch
        {
            ToolPolicyCategory.Connector => true,
            ToolPolicyCategory.Skill => true,
            ToolPolicyCategory.SuperExec => true,
            ToolPolicyCategory.Other => true,
            _ => false,
        };

    private static ToolPolicyDecision Allowed(string message)
        => new(true, ToolPolicyReasonCode.Allowed, message);

    private static ToolPolicyDecision Denied(ToolPolicyReasonCode reasonCode, string message)
        => new(false, reasonCode, message);

    private interface IToolApprovalStrategy
    {
        ToolApprovalDirective Evaluate(ToolApprovalRequest request);
    }

    private sealed class ConnectorApprovalStrategy : IToolApprovalStrategy
    {
        public ToolApprovalDirective Evaluate(ToolApprovalRequest request)
            => request.ApprovalPolicy is not null && !string.IsNullOrWhiteSpace(request.Identity.SourceId)
                ? EvaluateMcpToolApprovalCore(request.ApprovalPolicy, request.Identity.SourceId, request.Identity.ToolName)
                : new ToolApprovalDirective(Include: true, RequiresApproval: false);
    }

    private sealed class SkillApprovalStrategy : IToolApprovalStrategy
    {
        public ToolApprovalDirective Evaluate(ToolApprovalRequest request)
            => new(
                Include: true,
                RequiresApproval: request.SkillMode == SkillAttachmentMode.RequireApproval);
    }

    private sealed class SuperExecApprovalStrategy : IToolApprovalStrategy
    {
        public ToolApprovalDirective Evaluate(ToolApprovalRequest request)
            => new(
                Include: true,
                RequiresApproval: request.Agent is not null && RequiresApprovalForSuperExecCore(request.Agent));
    }

    private sealed class OtherApprovalStrategy : IToolApprovalStrategy
    {
        public ToolApprovalDirective Evaluate(ToolApprovalRequest request)
            => new(
                Include: true,
                RequiresApproval: request.Agent is not null
                    && !string.IsNullOrWhiteSpace(request.TargetAgentId)
                    && RequiresApprovalForAgentToolCore(request.Agent, request.TargetAgentId));
    }

    private sealed class NoApprovalStrategy : IToolApprovalStrategy
    {
        public static NoApprovalStrategy Instance { get; } = new();

        public ToolApprovalDirective Evaluate(ToolApprovalRequest request)
            => new(Include: true, RequiresApproval: false);
    }
}
