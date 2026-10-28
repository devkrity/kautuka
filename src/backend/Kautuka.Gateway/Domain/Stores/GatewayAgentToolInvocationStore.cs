using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayAgentToolInvocationStore(KautukaDatabase database) : IAgentToolInvocationStore
{
    private const string selectSql =
        """
        SELECT invocation_id AS InvocationId,
               parent_run_id AS ParentRunId,
               parent_session_id AS ParentSessionId,
               parent_tool_call_id AS ParentToolCallId,
               parent_tool_name AS ParentToolName,
               child_run_id AS ChildRunId,
               child_session_id AS ChildSessionId,
               child_agent_id AS ChildAgentId,
               root_run_id AS RootRunId,
               state AS State,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt,
               parent_continuation_run_id AS ParentContinuationRunId,
               completion_lease_owner AS CompletionLeaseOwner,
               completion_lease_expires_at AS CompletionLeaseExpiresAt,
               completed_at AS CompletedAt,
               failure_code AS FailureCode,
               child_result_json AS ChildResultJson,
               child_outcome AS ChildOutcome,
               child_completed_at AS ChildCompletedAt
        FROM agent_tool_invocations
        """;

    public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request)
    {
        Validate(request);
        string now = DateTimeOffset.UtcNow.ToString("O");
        string invocationId = $"ati-{Guid.NewGuid():N}";
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO agent_tool_invocations (
                invocation_id, parent_run_id, parent_session_id, parent_tool_call_id,
                parent_tool_name, parent_continuation_run_id, child_run_id,
                child_session_id, child_agent_id, root_run_id, state, created_at, updated_at)
            VALUES (
                $invocationId, $ParentRunId, $ParentSessionId, $ParentToolCallId,
                $ParentToolName, $ParentContinuationRunId, $ChildRunId,
                $ChildSessionId, $ChildAgentId, $RootRunId, $state, $now, $now)
            ON CONFLICT(parent_run_id, parent_tool_call_id) DO NOTHING
            """,
            new
            {
                invocationId,
                request.ParentRunId,
                request.ParentSessionId,
                request.ParentToolCallId,
                request.ParentToolName,
                request.ParentContinuationRunId,
                request.ChildRunId,
                request.ChildSessionId,
                request.ChildAgentId,
                request.RootRunId,
                state = AgentToolInvocationStates.WaitingOnChild,
                now,
            });

        AgentToolInvocation existing = connection.QuerySingle<AgentToolInvocation>(
            selectSql + " WHERE parent_run_id = $ParentRunId AND parent_tool_call_id = $ParentToolCallId",
            new { request.ParentRunId, request.ParentToolCallId });
        if (!string.Equals(existing.ChildSessionId, request.ChildSessionId, StringComparison.Ordinal)
            || !string.Equals(existing.ChildAgentId, request.ChildAgentId, StringComparison.Ordinal)
            || !string.Equals(existing.RootRunId, request.RootRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AAAT parent tool call '{request.ParentRunId}/{request.ParentToolCallId}' is already bound to a different child invocation.");
        }

        return existing;
    }

    public AgentToolInvocation? Get(string invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        using var connection = OpenConnection();
        return connection.QuerySingleOrDefault<AgentToolInvocation>(
            selectSql + " WHERE invocation_id = $invocationId",
            new { invocationId });
    }

    public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentToolCallId);
        using var connection = OpenConnection();
        return connection.QuerySingleOrDefault<AgentToolInvocation>(
            selectSql + " WHERE parent_run_id = $parentRunId AND parent_tool_call_id = $parentToolCallId",
            new { parentRunId, parentToolCallId });
    }

    public IReadOnlyList<AgentToolInvocation> ListRecoverable()
    {
        using var connection = OpenConnection();
        return connection.Query<AgentToolInvocation>(
            selectSql +
            " WHERE state IN ($waiting, $pending) OR (state = $completing AND completion_lease_expires_at <= $now) ORDER BY created_at",
            new
            {
                waiting = AgentToolInvocationStates.WaitingOnChild,
                pending = AgentToolInvocationStates.ParentCompletionPending,
                completing = AgentToolInvocationStates.CompletingParent,
                now = DateTimeOffset.UtcNow.ToString("O"),
            }).AsList();
    }

    public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childResultJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(childOutcome);
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = OpenConnection();
        int changed = connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $pending, child_result_json = $childResultJson,
                child_outcome = $childOutcome, child_completed_at = $now,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL,
                updated_at = $now
            WHERE invocation_id = $invocationId
              AND (state = $waiting
                   OR (state = $pending AND child_result_json = $childResultJson AND child_outcome = $childOutcome))
            """,
            new
            {
                invocationId,
                childResultJson,
                childOutcome,
                now,
                waiting = AgentToolInvocationStates.WaitingOnChild,
                pending = AgentToolInvocationStates.ParentCompletionPending,
            });
        if (changed != 1)
        {
            throw new InvalidOperationException($"AAAT invocation '{invocationId}' cannot record a child completion in its current state.");
        }
    }

    public void RecordApprovalLink(AgentToolInvocationLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.ApprovalRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.CallerToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.CallerToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.ChildSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.CallerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.ChildRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.ChildAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.RootRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(link.CanonicalEventId);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        int linkChanged = connection.Execute(
            """
            INSERT INTO agent_tool_invocation_links
                (approval_request_id, invocation_id, caller_session_id, caller_continuation_run_id,
                 caller_tool_call_id, caller_tool_name, child_session_id, caller_run_id,
                 child_run_id, child_agent_id, root_run_id, canonical_event_id, created_at, updated_at)
            VALUES
                ($ApprovalRequestId, $InvocationId, $CallerSessionId, $CallerContinuationRunId,
                 $CallerToolCallId, $CallerToolName, $ChildSessionId, $CallerRunId,
                 $ChildRunId, $ChildAgentId, $RootRunId, $CanonicalEventId, $CreatedAt, $UpdatedAt)
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
            link,
            transaction);
        if (linkChanged != 1)
        {
            throw new InvalidOperationException(
                $"Approval '{link.ApprovalRequestId}' is already bound to different durable AAAT lineage.");
        }

        int changed = connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $waiting, child_run_id = $childRunId,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL,
                child_result_json = NULL, child_outcome = NULL, child_completed_at = NULL,
                updated_at = $updatedAt
            WHERE invocation_id = $invocationId
              AND state IN ($waiting, $completing, $failed)
            """,
            new
            {
                invocationId = link.InvocationId,
                childRunId = link.ChildRunId,
                updatedAt = link.UpdatedAt,
                waiting = AgentToolInvocationStates.WaitingOnChild,
                completing = AgentToolInvocationStates.CompletingParent,
                failed = AgentToolInvocationStates.Failed,
            },
            transaction);
        if (changed != 1)
        {
            throw new InvalidOperationException($"AAAT invocation '{link.InvocationId}' cannot accept another approval in its current state.");
        }

        transaction.Commit();
    }

    public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $completing, completion_lease_owner = $leaseOwner,
                completion_lease_expires_at = $leaseExpiresAt, updated_at = $now
            WHERE invocation_id = $invocationId
              AND (state = $pending
                   OR (state = $completing AND completion_lease_expires_at <= $now))
            """,
            new
            {
                invocationId,
                leaseOwner,
                leaseExpiresAt = now.Add(leaseDuration).ToString("O"),
                now = now.ToString("O"),
                pending = AgentToolInvocationStates.ParentCompletionPending,
                completing = AgentToolInvocationStates.CompletingParent,
            }) == 1;
    }

    public void MarkWaitingOnChild(string invocationId, string childRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childRunId);
        using var connection = OpenConnection();
        int changed = connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $waiting, child_run_id = $childRunId,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL,
                child_result_json = NULL, child_outcome = NULL, child_completed_at = NULL,
                updated_at = $now
            WHERE invocation_id = $invocationId AND state IN ($waiting, $completing)
            """,
            new
            {
                invocationId,
                childRunId,
                waiting = AgentToolInvocationStates.WaitingOnChild,
                completing = AgentToolInvocationStates.CompletingParent,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        if (changed != 1)
        {
            throw new InvalidOperationException($"AAAT invocation '{invocationId}' cannot return to waiting_on_child from its current state.");
        }
    }

    public void MarkCompleted(string invocationId, string leaseOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = OpenConnection();
        int changed = connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $completed, completed_at = $now, updated_at = $now,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL
            WHERE invocation_id = $invocationId AND state = $completing
              AND completion_lease_owner = $leaseOwner
            """,
            new
            {
                invocationId,
                leaseOwner,
                now,
                completed = AgentToolInvocationStates.Completed,
                completing = AgentToolInvocationStates.CompletingParent,
            });
        if (changed == 1)
        {
            return;
        }

        // Lease-owned row may already be authority-sealed, or the worker lost the soft mutex
        // after the journal advanced. Prefer journal-backed seal over failing open for re-stream.
        if (!TrySealCompleted(invocationId))
        {
            AgentToolInvocation? current = Get(invocationId);
            if (current?.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.Failed)
            {
                return;
            }

            throw new InvalidOperationException($"AAAT invocation '{invocationId}' completion lease was lost.");
        }
    }

    public bool TrySealCompleted(string invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $completed, completed_at = $now, updated_at = $now,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL
            WHERE invocation_id = $invocationId
              AND state IN ($pending, $completing)
              AND child_outcome IS NOT NULL
            """,
            new
            {
                invocationId,
                now,
                completed = AgentToolInvocationStates.Completed,
                pending = AgentToolInvocationStates.ParentCompletionPending,
                completing = AgentToolInvocationStates.CompletingParent,
            }) == 1;
    }

    public void MarkFailed(string invocationId, string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        using var connection = OpenConnection();
        connection.Execute(
            """
            UPDATE agent_tool_invocations
            SET state = $failed, failure_code = $failureCode, updated_at = $now,
                completion_lease_owner = NULL, completion_lease_expires_at = NULL
            WHERE invocation_id = $invocationId AND state NOT IN ($completed, $failed)
            """,
            new
            {
                invocationId,
                failureCode,
                now = DateTimeOffset.UtcNow.ToString("O"),
                failed = AgentToolInvocationStates.Failed,
                completed = AgentToolInvocationStates.Completed,
            });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static void Validate(AgentToolInvocationCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ParentToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChildRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChildSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ChildAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootRunId);
    }
}
