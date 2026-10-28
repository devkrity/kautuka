using Dapper;

using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class SqliteRunWaitStateStore(KautukaDatabase database) : IRunWaitStateStore
{
    public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request)
        => Pause(request, RunWaitReasons.Approval);

    public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request)
        => Pause(request, RunWaitReasons.Child);

    public RunWaitState? GetByWaitId(string waitId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<RunWaitState>(selectSql + " WHERE wait_id = $waitId", new { waitId });
    }

    public RunWaitState? GetActiveByContinuationRunId(string continuationRunId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<RunWaitState>(
            selectSql + " WHERE continuation_run_id = $continuationRunId AND status IN ('ready', 'executing') ORDER BY paused_at DESC LIMIT 1",
            new { continuationRunId });
    }

    public RunWaitState? GetActiveByCorrelationId(string correlationId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        RunWaitState? exact = connection.QueryFirstOrDefault<RunWaitState>(
            selectSql + " WHERE correlation_id = $correlationId AND status IN ('ready', 'executing') ORDER BY paused_at DESC LIMIT 1",
            new { correlationId });
        if (exact is not null)
        {
            return exact;
        }

        foreach (RunWaitState wait in connection.Query<RunWaitState>(
            selectSql + " WHERE wait_reason = $waitReason AND status IN ('ready', 'executing')",
            new { waitReason = RunWaitReasons.Approval }))
        {
            if (string.Equals(wait.ApprovalRequestId, correlationId, StringComparison.Ordinal))
            {
                return wait;
            }

            if (RunClockEnvelope.TryReadPackedApprovalRequestIds(wait.BehaviorJson, out IReadOnlyList<string> packedIds)
                && packedIds.Contains(correlationId, StringComparer.Ordinal))
            {
                return wait;
            }
        }

        return null;
    }

    public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<RunWaitState>(
            selectSql + """
              WHERE session_id = $callerSessionId
               AND tool_call_id = $callerToolCallId
               AND wait_reason = $waitReason
               AND status IN ('ready', 'executing')
             ORDER BY paused_at DESC
             LIMIT 1
             """,
            new
            {
                callerSessionId,
                callerToolCallId,
                waitReason = RunWaitReasons.Child,
            });
    }

    public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Execute(
            """
            UPDATE run_wait_states
            SET status = 'executing', attempt_count = attempt_count + 1,
                lease_owner = $leaseOwner, lease_expires_at = $leaseExpiresAt
            WHERE wait_id = $waitId
              AND (status = 'ready' OR (status = 'executing' AND lease_expires_at <= $now))
            """,
            new
            {
                waitId,
                leaseOwner,
                leaseExpiresAt = now.Add(leaseDuration).ToString("O"),
                now = now.ToString("O"),
            }) == 1;
    }

    public void ReleaseForRetry(string waitId, string leaseOwner)
        => TransitionOwned(waitId, leaseOwner, RunWaitStatuses.Ready, failureCode: null, terminal: false);

    public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        int changed = connection.Execute(
            """
            UPDATE run_wait_states SET lease_expires_at = $leaseExpiresAt
            WHERE wait_id = $waitId AND status = 'executing' AND lease_owner = $leaseOwner
            """,
            new
            {
                waitId,
                leaseOwner,
                leaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration).ToString("O"),
            });
        if (changed != 1)
        {
            throw new InvalidOperationException($"Wait '{waitId}' execution lease was lost.");
        }
    }

    public void MarkPausedAgain(string waitId)
        => Transition(waitId, RunWaitStatuses.PausedAgain, failureCode: null, terminal: false);

    public void ReleaseExecutingWait(string waitId)
        => Transition(waitId, RunWaitStatuses.Ready, failureCode: null, terminal: false);

    public void MarkCompleted(string waitId)
        => Transition(waitId, RunWaitStatuses.Completed, failureCode: null, terminal: true);

    public void MarkFailed(string waitId, string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        Transition(waitId, RunWaitStatuses.Failed, failureCode, terminal: true);
    }

    public IReadOnlyList<RunWaitState> ListRecoverable()
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<RunWaitState>(
            selectSql + " WHERE status = 'ready' OR (status = 'executing' AND lease_expires_at <= $now) ORDER BY paused_at",
            new { now = DateTimeOffset.UtcNow.ToString("O") }).AsList();
    }

    public void DeleteBySession(string sessionId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("DELETE FROM run_wait_states WHERE session_id = $sessionId", new { sessionId });
    }

    public void PutPackedToolResult(string waitId, string requestId, string resultJson, bool journaledStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        RunWaitState? wait = connection.QueryFirstOrDefault<RunWaitState>(
            selectSql + " WHERE wait_id = $waitId",
            new { waitId },
            transaction);
        if (wait is null)
        {
            throw new InvalidOperationException($"Wait '{waitId}' was not found.");
        }

        string next = RunClockEnvelope.PutPackedToolResult(wait.BehaviorJson, requestId, resultJson, journaledStart);
        int changed = connection.Execute(
            """
            UPDATE run_wait_states
            SET behavior_json = $behaviorJson
            WHERE wait_id = $waitId AND status IN ('ready', 'executing')
            """,
            new { waitId, behaviorJson = next },
            transaction);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Wait '{waitId}' cannot store a packed tool result from its current state.");
        }

        transaction.Commit();
    }

    private RunWaitState Pause(RunWaitStateCreateRequest request, string waitReason)
    {
        if (string.Equals(waitReason, RunWaitReasons.Approval, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(request.ApprovalRequestId)
                || !string.Equals(request.CorrelationId, request.ApprovalRequestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Direct approval waits require CorrelationId to equal their immutable ApprovalRequestId.");
        }

        string waitId = $"wait-{Guid.NewGuid():N}";
        string pausedAt = DateTimeOffset.UtcNow.ToString("O");
        var state = new RunWaitState(
            waitId,
            request.ContinuationRunId,
            request.RunId,
            request.TargetType,
            request.TargetId,
            request.SessionId,
            request.AgentId,
            request.UserId,
            waitReason,
            request.CorrelationId,
            request.SessionStateJson,
            request.BehaviorJson,
            request.ArtifactManifestJson,
            request.ToolCallId,
            request.ToolName,
            request.ApprovalRequestId,
            request.ChildSessionId,
            request.ParentRunId,
            request.ChildRunId,
            request.TraceContext?.TraceParent,
            request.TraceContext?.TraceState,
            pausedAt,
            CompletedAt: null,
            CanonicalEventId: string.IsNullOrWhiteSpace(request.CanonicalEventId)
                ? throw new InvalidOperationException("Run wait states require a canonical event id.")
                : request.CanonicalEventId,
            RootRunId: request.RootRunId ?? request.RunId,
            Status: RunWaitStatuses.Ready,
            AttemptCount: 0,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            FailureCode: null);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute(
            """
            INSERT INTO run_wait_states (
                wait_id, continuation_run_id, run_id, target_type, target_id, session_id,
                agent_id, user_id, wait_reason, correlation_id, session_state_json,
                behavior_json, artifact_manifest_json, tool_call_id, tool_name,
                approval_request_id, child_session_id, parent_run_id, child_run_id,
                root_run_id, trace_parent, trace_state, canonical_event_id, paused_at,
                status, attempt_count, lease_owner, lease_expires_at, completed_at, failure_code)
            VALUES (
                $WaitId, $ContinuationRunId, $RunId, $TargetType, $TargetId, $SessionId,
                $AgentId, $UserId, $WaitReason, $CorrelationId, $SessionStateJson,
                $BehaviorJson, $ArtifactManifestJson, $ToolCallId, $ToolName,
                $ApprovalRequestId, $ChildSessionId, $ParentRunId, $ChildRunId,
                $RootRunId, $TraceParent, $TraceState, $CanonicalEventId, $PausedAt,
                $Status, $AttemptCount, $LeaseOwner, $LeaseExpiresAt, $CompletedAt, $FailureCode)
            """,
            new
            {
                state.WaitId,
                state.ContinuationRunId,
                state.RunId,
                state.TargetType,
                state.TargetId,
                state.SessionId,
                state.AgentId,
                state.UserId,
                state.WaitReason,
                state.CorrelationId,
                state.SessionStateJson,
                state.BehaviorJson,
                state.ArtifactManifestJson,
                state.ToolCallId,
                state.ToolName,
                state.ApprovalRequestId,
                state.ChildSessionId,
                state.ParentRunId,
                state.ChildRunId,
                state.RootRunId,
                state.TraceParent,
                state.TraceState,
                state.CanonicalEventId,
                state.PausedAt,
                state.Status,
                state.AttemptCount,
                state.LeaseOwner,
                state.LeaseExpiresAt,
                state.CompletedAt,
                state.FailureCode,
            });

        return state;
    }

    private const string selectSql = """
        SELECT wait_id AS WaitId,
               continuation_run_id AS ContinuationRunId,
               run_id AS RunId,
               target_type AS TargetType,
               target_id AS TargetId,
               session_id AS SessionId,
               agent_id AS AgentId,
               user_id AS UserId,
               wait_reason AS WaitReason,
               correlation_id AS CorrelationId,
               session_state_json AS SessionStateJson,
               behavior_json AS BehaviorJson,
               artifact_manifest_json AS ArtifactManifestJson,
               tool_call_id AS ToolCallId,
               tool_name AS ToolName,
               approval_request_id AS ApprovalRequestId,
               child_session_id AS ChildSessionId,
               parent_run_id AS ParentRunId,
               child_run_id AS ChildRunId,
               root_run_id AS RootRunId,
               trace_parent AS TraceParent,
               trace_state AS TraceState,
               canonical_event_id AS CanonicalEventId,
               paused_at AS PausedAt,
               status AS Status,
               attempt_count AS AttemptCount,
               lease_owner AS LeaseOwner,
               lease_expires_at AS LeaseExpiresAt,
               completed_at AS CompletedAt,
               failure_code AS FailureCode
        FROM run_wait_states
        """;

    private void Transition(string waitId, string status, string? failureCode, bool terminal)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        int changed = connection.Execute(
            """
            UPDATE run_wait_states
            SET status = $status, lease_owner = NULL, lease_expires_at = NULL,
                completed_at = $completedAt, failure_code = $failureCode
            WHERE wait_id = $waitId AND status IN ('ready', 'executing')
            """,
            new
            {
                waitId,
                status,
                completedAt = terminal ? DateTimeOffset.UtcNow.ToString("O") : null,
                failureCode,
            });
        if (changed != 1)
        {
            // Completion, cancellation and recovery may settle the same wait concurrently.
            // A closed wait belongs to its original cycle: never overwrite its outcome or
            // reactivate it. Session deletion can also precede this late cleanup.
            if (terminal)
            {
                string? existing = connection.QueryFirstOrDefault<string>(
                    "SELECT status FROM run_wait_states WHERE wait_id = $waitId", new { waitId });
                if (existing is null or RunWaitStatuses.Completed or RunWaitStatuses.Failed or RunWaitStatuses.PausedAgain) return;
            }
            throw new InvalidOperationException($"Wait '{waitId}' cannot transition to '{status}' from its current state.");
        }
    }

    private void TransitionOwned(string waitId, string leaseOwner, string status, string? failureCode, bool terminal)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        int changed = connection.Execute(
            """
            UPDATE run_wait_states
            SET status = $status, lease_owner = NULL, lease_expires_at = NULL,
                completed_at = $completedAt, failure_code = $failureCode
            WHERE wait_id = $waitId AND status = 'executing' AND lease_owner = $leaseOwner
            """,
            new
            {
                waitId,
                leaseOwner,
                status,
                completedAt = terminal ? DateTimeOffset.UtcNow.ToString("O") : null,
                failureCode,
            });
        if (changed != 1)
        {
            throw new InvalidOperationException($"Wait '{waitId}' is not leased by '{leaseOwner}'.");
        }
    }

}
