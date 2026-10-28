using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayAgentToolInvocationLinkStore(KautukaDatabase database) : IAgentToolInvocationLinkStore
{
    public void Upsert(AgentToolInvocationLink link)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        if (string.IsNullOrWhiteSpace(link.CanonicalEventId))
        {
            throw new InvalidOperationException("Agent-tool invocation links require a canonical event id.");
        }

        if (string.IsNullOrWhiteSpace(link.InvocationId)
            || string.IsNullOrWhiteSpace(link.CallerToolCallId)
            || string.IsNullOrWhiteSpace(link.CallerToolName)
            || string.IsNullOrWhiteSpace(link.ChildSessionId)
            || string.IsNullOrWhiteSpace(link.CallerRunId)
            || string.IsNullOrWhiteSpace(link.ChildRunId)
            || string.IsNullOrWhiteSpace(link.ChildAgentId)
            || string.IsNullOrWhiteSpace(link.RootRunId))
        {
            throw new InvalidOperationException("Agent-tool approval links require a stable invocation id and complete parent/child lineage.");
        }

        int changed = connection.Execute(
            """
            INSERT INTO agent_tool_invocation_links
                (approval_request_id, invocation_id, caller_session_id, caller_continuation_run_id, caller_tool_call_id, caller_tool_name, child_session_id,
                 caller_run_id, child_run_id, child_agent_id, root_run_id, canonical_event_id, created_at, updated_at)
            VALUES
                ($approvalRequestId, $invocationId, $callerSessionId, $callerContinuationRunId, $callerToolCallId, $callerToolName, $childSessionId,
                 $callerRunId, $childRunId, $childAgentId, $rootRunId, $canonicalEventId, $createdAt, $updatedAt)
            ON CONFLICT(approval_request_id) DO UPDATE SET
                updated_at = excluded.updated_at
            WHERE invocation_id = excluded.invocation_id
              AND caller_session_id IS excluded.caller_session_id
              AND caller_continuation_run_id IS excluded.caller_continuation_run_id
              AND caller_tool_call_id = excluded.caller_tool_call_id
              AND caller_tool_name = excluded.caller_tool_name
              AND child_session_id = excluded.child_session_id
              AND caller_run_id = excluded.caller_run_id
              AND child_run_id = excluded.child_run_id
              AND child_agent_id = excluded.child_agent_id
              AND root_run_id = excluded.root_run_id
              AND canonical_event_id = excluded.canonical_event_id
            """,
            new
            {
                approvalRequestId = link.ApprovalRequestId,
                invocationId = link.InvocationId,
                callerSessionId = link.CallerSessionId,
                callerContinuationRunId = link.CallerContinuationRunId,
                callerToolCallId = link.CallerToolCallId,
                callerToolName = link.CallerToolName,
                childSessionId = link.ChildSessionId,
                callerRunId = link.CallerRunId,
                childRunId = link.ChildRunId,
                childAgentId = link.ChildAgentId,
                rootRunId = link.RootRunId,
                canonicalEventId = link.CanonicalEventId,
                createdAt = link.CreatedAt,
                updatedAt = link.UpdatedAt,
            });
        if (changed != 1)
        {
            throw new InvalidOperationException(
                $"Approval '{link.ApprovalRequestId}' is already bound to different durable AAAT lineage.");
        }
    }

    public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<AgentToolInvocationLink>(
            """
            SELECT approval_request_id AS ApprovalRequestId,
                   invocation_id AS InvocationId,
                   caller_session_id AS CallerSessionId,
                     caller_continuation_run_id AS CallerContinuationRunId,
                     caller_tool_call_id AS CallerToolCallId,
                     caller_tool_name AS CallerToolName,
                     child_session_id AS ChildSessionId,
                   caller_run_id AS CallerRunId,
                   child_run_id AS ChildRunId,
                   child_agent_id AS ChildAgentId,
                   root_run_id AS RootRunId,
                   canonical_event_id AS CanonicalEventId,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM agent_tool_invocation_links
            WHERE approval_request_id = $approvalRequestId
            """,
            new { approvalRequestId });
    }

    public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<AgentToolInvocationLink>(
            """
            SELECT approval_request_id AS ApprovalRequestId,
                   invocation_id AS InvocationId,
                   caller_session_id AS CallerSessionId,
                     caller_continuation_run_id AS CallerContinuationRunId,
                     caller_tool_call_id AS CallerToolCallId,
                     caller_tool_name AS CallerToolName,
                     child_session_id AS ChildSessionId,
                   caller_run_id AS CallerRunId,
                   child_run_id AS ChildRunId,
                   child_agent_id AS ChildAgentId,
                   root_run_id AS RootRunId,
                   canonical_event_id AS CanonicalEventId,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM agent_tool_invocation_links
            WHERE caller_session_id = $callerSessionId
            ORDER BY created_at ASC, updated_at ASC
            """,
            new { callerSessionId }).AsList();
    }

    public AgentToolInvocationLink? GetByCallerSessionAndToolCallId(string callerSessionId, string callerToolCallId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<AgentToolInvocationLink>(
            """
            SELECT approval_request_id AS ApprovalRequestId,
                   invocation_id AS InvocationId,
                   caller_session_id AS CallerSessionId,
                   caller_continuation_run_id AS CallerContinuationRunId,
                   caller_tool_call_id AS CallerToolCallId,
                   caller_tool_name AS CallerToolName,
                   child_session_id AS ChildSessionId,
                   caller_run_id AS CallerRunId,
                   child_run_id AS ChildRunId,
                   child_agent_id AS ChildAgentId,
                   root_run_id AS RootRunId,
                   canonical_event_id AS CanonicalEventId,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM agent_tool_invocation_links
            WHERE caller_session_id = $callerSessionId
                AND caller_tool_call_id = $callerToolCallId
            ORDER BY updated_at DESC
            LIMIT 1
            """,
            new { callerSessionId, callerToolCallId });
    }

    public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<AgentToolInvocationLink>(
            """
            SELECT approval_request_id AS ApprovalRequestId,
                   invocation_id AS InvocationId,
                   caller_session_id AS CallerSessionId,
                   caller_continuation_run_id AS CallerContinuationRunId,
                   caller_tool_call_id AS CallerToolCallId,
                   caller_tool_name AS CallerToolName,
                   child_session_id AS ChildSessionId,
                   caller_run_id AS CallerRunId,
                   child_run_id AS ChildRunId,
                   child_agent_id AS ChildAgentId,
                   root_run_id AS RootRunId,
                   canonical_event_id AS CanonicalEventId,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM agent_tool_invocation_links
            WHERE child_session_id = $childSessionId
                AND child_session_id <> ''
            ORDER BY updated_at DESC
            LIMIT 1
            """,
            new { childSessionId });
    }

    public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<AgentToolInvocationLink>(
            """
            SELECT approval_request_id AS ApprovalRequestId,
                   invocation_id AS InvocationId,
                   caller_session_id AS CallerSessionId,
                   caller_continuation_run_id AS CallerContinuationRunId,
                   caller_tool_call_id AS CallerToolCallId,
                   caller_tool_name AS CallerToolName,
                   child_session_id AS ChildSessionId,
                   caller_run_id AS CallerRunId,
                   child_run_id AS ChildRunId,
                   child_agent_id AS ChildAgentId,
                   root_run_id AS RootRunId,
                   canonical_event_id AS CanonicalEventId,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM agent_tool_invocation_links
            WHERE root_run_id = $rootRunId
            ORDER BY created_at ASC, updated_at ASC
            """,
            new { rootRunId }).AsList();
    }

    public bool HasLinksForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QuerySingle<long>(
            "SELECT COUNT(*) FROM agent_tool_invocation_links WHERE caller_session_id = $sessionId OR child_session_id = $sessionId;",
            new { sessionId }) > 0;
    }
}
