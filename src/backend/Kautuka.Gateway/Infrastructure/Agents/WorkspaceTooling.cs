using Kautuka.Gateway.Domain.Sandbox;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class WorkspaceTooling
{
    private const string agentScopeType = "agent";

    public static string BuildAgentScopeId(string value)
        => SanitizePathSegment(value);

    public static string BuildAgentScopeId(string? ownerKey, string value)
    {
        string ownerSegment = SanitizePathSegment(ownerKey ?? string.Empty);
        string agentSegment = SanitizePathSegment(value);

        if (string.Equals(ownerSegment, "default", StringComparison.Ordinal))
        {
            return agentSegment;
        }

        return $"{ownerSegment}-{agentSegment}";
    }

    public static AgentToolContext BuildAgentToolContext(
        IWorkspaceRuntimeTool workspaceTool,
        AgentDefinition agentDefinition,
        string agentId,
        string sessionId,
        string? userId,
        string? stageTransitionId,
        IReadOnlyList<EffectiveSkillManifestEntry>? effectiveSkillManifest = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceTool);
        ArgumentNullException.ThrowIfNull(agentDefinition);

        WorkspaceSessionBinding workspaceBinding = BuildAgentSessionBinding(workspaceTool, agentId, userId, stageTransitionId);
        return new AgentToolContext(agentDefinition, agentId, userId, workspaceTool, workspaceBinding, effectiveSkillManifest, SessionId: sessionId);
    }

    public static WorkspaceSessionBinding BuildAgentSessionBinding(
        AgentDefinition agentDefinition,
        string agentId,
        string? userId,
        string? stageTransitionId)
    {
        ArgumentNullException.ThrowIfNull(agentDefinition);

        string scopeId = BuildAgentScopeId(string.IsNullOrWhiteSpace(agentDefinition.Name) ? agentId : agentDefinition.Name);

        return new WorkspaceSessionBinding(
            WorkspaceId: KautukaAgentSession.BuildWorkspaceId(agentScopeType, scopeId, userId),
            ScopeType: agentScopeType,
            ScopeId: scopeId,
            UserId: userId,
            StageTransitionId: stageTransitionId);
    }

    public static WorkspaceSessionBinding BuildAgentSessionBinding(
        IWorkspaceRuntimeTool workspaceTool,
        string agentId,
        string? userId,
        string? stageTransitionId)
    {
        ArgumentNullException.ThrowIfNull(workspaceTool);

        WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, userId);
        string scopeId = BuildAgentScopeId(string.IsNullOrWhiteSpace(workspace.ScopeId) ? agentId : workspace.ScopeId);

        return new WorkspaceSessionBinding(
            WorkspaceId: KautukaAgentSession.BuildWorkspaceId(agentScopeType, scopeId, userId),
            ScopeType: agentScopeType,
            ScopeId: scopeId,
            UserId: userId,
            StageTransitionId: stageTransitionId);
    }

    public static WorkspaceSessionBinding BuildAgentSessionBinding(
        IWorkspaceRuntimeTool workspaceTool,
        AgentDefinition agentDefinition,
        string agentId,
        string? userId,
        string? stageTransitionId)
    {
        ArgumentNullException.ThrowIfNull(workspaceTool);
        return BuildAgentSessionBinding(agentDefinition, agentId, userId, stageTransitionId);
    }

    private static string SanitizePathSegment(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char character in value.Trim())
        {
            buffer[index++] = char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-';
        }

        string sanitized = new string(buffer[..index]).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}
