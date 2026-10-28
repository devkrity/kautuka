using Microsoft.Agents.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Static helpers for workspace binding on any MAF AgentSession (e.g. ChatClientAgentSession).
/// History and serialization are handled by the native MAF session type.
/// </summary>
internal static class KautukaAgentSession
{
    private const string workspaceIdKey = "workspace.id";
    private const string workspaceScopeTypeKey = "workspace.scope_type";
    private const string workspaceScopeIdKey = "workspace.scope_id";
    private const string workspaceUserIdKey = "workspace.user_id";
    private const string workspaceTransitionKey = "workspace.stage_transition_id";

    public static string BuildWorkspaceId(string scopeType, string scopeId, string? userId)
    {
        string suffix = string.IsNullOrWhiteSpace(userId) ? "shared" : userId;
        return $"{scopeType}:{scopeId}:{suffix}";
    }

    public static void BindWorkspace(
        AgentSession session,
        string workspaceId,
        string scopeType,
        string scopeId,
        string? userId,
        string? stageTransitionId = null)
    {
        string? currentWorkspaceId = TryGetString(session.StateBag, workspaceIdKey);
        if (string.IsNullOrWhiteSpace(currentWorkspaceId))
        {
            WriteWorkspace(session.StateBag, workspaceId, scopeType, scopeId, userId, stageTransitionId);
            return;
        }

        if (string.Equals(currentWorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(stageTransitionId))
            {
                session.StateBag.SetValue(workspaceTransitionKey, stageTransitionId);
            }
            return;
        }

        string? previousTransitionId = TryGetString(session.StateBag, workspaceTransitionKey);
        bool hasValidTransition = !string.IsNullOrWhiteSpace(stageTransitionId)
            && !string.Equals(previousTransitionId, stageTransitionId, StringComparison.Ordinal);

        if (!hasValidTransition)
        {
            throw new InvalidOperationException(
                "WorkspaceId is immutable for this session. Provide a new stage transition id to rebind workspace.");
        }

        WriteWorkspace(session.StateBag, workspaceId, scopeType, scopeId, userId, stageTransitionId);
    }

    private static void WriteWorkspace(
        AgentSessionStateBag stateBag,
        string workspaceId,
        string scopeType,
        string scopeId,
        string? userId,
        string? stageTransitionId)
    {
        stateBag.SetValue(workspaceIdKey, workspaceId);
        stateBag.SetValue(workspaceScopeTypeKey, scopeType);
        stateBag.SetValue(workspaceScopeIdKey, scopeId);
        stateBag.SetValue(workspaceUserIdKey, userId ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(stageTransitionId))
        {
            stateBag.SetValue(workspaceTransitionKey, stageTransitionId);
        }
    }

    private static string? TryGetString(AgentSessionStateBag stateBag, string key)
    {
        if (!stateBag.TryGetValue<string>(key, out string? value) || value is null)
        {
            return null;
        }

        return value;
    }
}

internal sealed record WorkspaceSessionBinding(
    string WorkspaceId,
    string ScopeType,
    string ScopeId,
    string? UserId,
    string? StageTransitionId);
