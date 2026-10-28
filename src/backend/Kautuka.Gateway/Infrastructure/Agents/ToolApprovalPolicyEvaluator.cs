using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Evaluates whether a tool call should surface human approval, matching runtime tool wrapping policy.
/// </summary>
internal static class ToolApprovalPolicyEvaluator
{
    public static bool RequiresHumanApproval(
        IToolExecutionPolicyService policyService,
        PlatformConfiguration config,
        string agentId,
        string toolName,
        string? argumentsJson = null)
    {
        AgentDefinition? agent = config.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            return false;
        }

        if (string.Equals(toolName, "run_skill_script", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("read_skill_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "load_skill", StringComparison.OrdinalIgnoreCase))
        {
            SkillAttachmentMode? skillMode = ResolveSkillAttachmentMode(agent, argumentsJson);
            return policyService.EvaluateApproval(new ToolApprovalRequest(
                Identity: new ToolIdentity(ToolPolicyCategory.Skill, toolName),
                SkillMode: skillMode,
                Agent: agent)).RequiresApproval;
        }

        if (ActionIdentity.IsAgentToolInvoke(toolName))
        {
            string? targetAgentId = ResolveInvokeAgentTargetId(config, toolName);
            return policyService.EvaluateApproval(new ToolApprovalRequest(
                Identity: new ToolIdentity(ToolPolicyCategory.Other, toolName),
                Agent: agent,
                TargetAgentId: targetAgentId)).RequiresApproval;
        }

        if (string.Equals(toolName, "run_shell", StringComparison.OrdinalIgnoreCase))
        {
            return policyService.EvaluateApproval(new ToolApprovalRequest(
                Identity: new ToolIdentity(ToolPolicyCategory.SuperExec, toolName),
                Agent: agent)).RequiresApproval;
        }

        // Connector/MCP tools without server context: if MAF surfaced approval, honor it.
        return true;
    }

    /// <summary>
    /// Whether to defer <c>TOOL_CALL_START</c> until after HITL approval (gate-before-start).
    /// Only defer for tools that are actually wrapped as <c>ApprovalRequiredAIFunction</c> and
    /// will interrupt. Do not defer:
    /// <list type="bullet">
    /// <item><c>load_skill</c> — never wrapped for approval (only run/read skill tools are).</item>
    /// <item>MCP/connector tools without server id — non-wrapped tools bypass TARC when
    /// <c>DisableApprovalNotRequiredFunctionBypassing</c> is false; deferring leaves END without START.</item>
    /// </list>
    /// Wrapped MCP tools that require approval arrive as TARC (no prior FunctionCallContent START).
    /// </summary>
    public static bool ShouldDeferToolCallStartForHitl(
        IToolExecutionPolicyService policyService,
        PlatformConfiguration config,
        string agentId,
        string toolName,
        string? argumentsJson = null)
    {
        if (string.Equals(toolName, "load_skill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(toolName, "run_skill_script", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("read_skill_", StringComparison.OrdinalIgnoreCase)
            || ActionIdentity.IsAgentToolInvoke(toolName)
            || string.Equals(toolName, "run_shell", StringComparison.OrdinalIgnoreCase))
        {
            return RequiresHumanApproval(policyService, config, agentId, toolName, argumentsJson);
        }

        return false;
    }

    public static bool ShouldSurfacePendingApproval(
        IToolExecutionPolicyService policyService,
        PlatformConfiguration config,
        string sessionId,
        GatewayToolApprovalRequest approval)
    {
        if (string.Equals(approval.OuterSessionId, sessionId, StringComparison.Ordinal))
        {
            return true;
        }

        return RequiresHumanApproval(
            policyService,
            config,
            approval.AgentId ?? string.Empty,
            approval.ToolName,
            approval.ArgumentsJson);
    }

    private static SkillAttachmentMode? ResolveSkillAttachmentMode(AgentDefinition agent, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("skillId", out JsonElement skillIdElement))
            {
                return null;
            }

            string? skillId = skillIdElement.GetString();
            if (string.IsNullOrWhiteSpace(skillId))
            {
                return null;
            }

            AgentSkillAttachmentDefinition? attachment = agent.SkillAttachments
                .FirstOrDefault(candidate => string.Equals(candidate.SkillId, skillId, StringComparison.OrdinalIgnoreCase));
            return attachment?.Mode;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ResolveInvokeAgentTargetId(PlatformConfiguration config, string toolName)
    {
        const string prefix = "invoke_agent_";
        if (!toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string suffix = toolName[prefix.Length..];
        foreach (AgentDefinition agent in config.Agents)
        {
            if (!agent.ToolExposure.ExposedAsTool)
            {
                continue;
            }

            if (string.Equals(SanitizeAgentToolSuffix(agent.Name), suffix, StringComparison.OrdinalIgnoreCase))
            {
                return agent.Id;
            }
        }

        return null;
    }

    private static string SanitizeAgentToolSuffix(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is ' ' or '-' or '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }
}
