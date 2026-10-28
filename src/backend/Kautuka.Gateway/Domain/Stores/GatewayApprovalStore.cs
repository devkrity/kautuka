using System.Diagnostics;
using System.Text.Json;

using Dapper;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Infrastructure.Observability;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayApprovalStore(KautukaDatabase database) : IGatewayApprovalStore, IApprovalResolutionEventStore
{
    private const string channelTetherModeColumn = "channel_tether_mode";
    // A pre-run decision cannot start an already terminal/deleted scheduled run.
    // Keep this eligibility identical for recovery, expiry sweeping and the atomic update.
    // Scheduled runs are excluded: creating their approval and attaching it are separate writes.
    private const string obsoleteAutopilotApprovalSql = """
        (tool_type = 'autopilot_prerun' AND (
            NOT EXISTS (SELECT 1 FROM autopilot_runs r WHERE r.id = tool_approval_requests.run_id)
            OR EXISTS (SELECT 1 FROM autopilot_runs r
                WHERE r.id = tool_approval_requests.run_id
                  AND r.status IN ('completed', 'no_op', 'failed', 'rejected', 'timed_out', 'skipped', 'stale'))))
        """;

    public void CreateRequest(GatewayToolApprovalRequest request)
    {
        using Activity? activity = AgentRunHostCallTracer.StartScopedInternalActivity("hitl approval create request");
        activity?.SetTag("hitl.request.id", request.Id);
        activity?.SetTag("hitl.tool.name", request.ToolName);
        activity?.SetTag("hitl.tool.type", request.ToolType ?? string.Empty);

        string tetherMode = ChannelHitlTetherModeExtensions.Parse(request.ChannelTetherMode).ToStorageValue();
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        int changed = connection.Execute(
            $"""
            INSERT INTO tool_approval_requests
                (id, session_id, run_id, checkpoint_id, agent_id, user_id,
                 tool_call_id, tool_name, tool_type, mcp_server_id,
                arguments_json, requested_at, expires_at, outer_session_id, outer_agent_id, resolution_source, {channelTetherModeColumn},
                agent_definition_revision, kind, workflow_id, instance_id, node_id)
            VALUES
                ($id, $sessionId, $runId, $checkpointId, $agentId, $userId,
                 $toolCallId, $toolName, $toolType, $mcpServerId,
               $argsJson, $requestedAt, $expiresAt, $outerSessionId, $outerAgentId, $resolutionSource, $ChannelTetherMode,
               $agentDefinitionRevision, $kind, $workflowId, $instanceId, $nodeId)
            ON CONFLICT(id) DO NOTHING
            """,
            new
            {
                id = request.Id,
                sessionId = request.SessionId,
                runId = request.RunId,
                checkpointId = request.CheckpointId,
                agentId = request.AgentId,
                userId = request.UserId,
                toolCallId = request.ToolCallId,
                toolName = request.ToolName,
                toolType = request.ToolType,
                mcpServerId = request.McpServerId,
                argsJson = request.ArgumentsJson,
                requestedAt = request.RequestedAt,
                expiresAt = request.ExpiresAt,
                outerSessionId = request.OuterSessionId,
                outerAgentId = request.OuterAgentId,
                resolutionSource = request.ResolutionSource,
                ChannelTetherMode = tetherMode,
                agentDefinitionRevision = request.AgentDefinitionRevision,
                kind = string.IsNullOrWhiteSpace(request.Kind) ? "tool" : request.Kind,
                workflowId = request.WorkflowId,
                instanceId = request.InstanceId,
                nodeId = request.NodeId,
            });
        if (changed == 0)
        {
            GatewayToolApprovalRequest? existing = GetById(request.Id);
            if (existing is null || !HasSameImmutablePayload(existing, request, tetherMode))
            {
                throw new InvalidOperationException(
                    $"Approval '{request.Id}' already exists with different immutable payload or lineage.");
            }
        }
    }

    public bool Resolve(
        string requestId,
        bool approved,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation = null)
        => Resolve(
            requestId,
            approved ? "approved" : "denied",
            reason,
            decidedBy,
            resolutionSource,
            continuation,
            resolutionEvent: null);

    public bool Resolve(
        string requestId,
        bool approved,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
        => Resolve(
            requestId,
            approved ? "approved" : "denied",
            reason,
            decidedBy,
            resolutionSource,
            continuation,
            resolutionEvent);

    public bool Resolve(
        string requestId,
        string decision,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
    {
        using Activity? activity = AgentRunHostCallTracer.StartScopedInternalActivity("hitl approval resolve");
        activity?.SetTag("hitl.request.id", requestId);
        activity?.SetTag("hitl.decision", decision);
        string now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        bool resolved = connection.Execute(
            """
            UPDATE tool_approval_requests
            SET decision = $decision,
                decided_by = $decidedBy,
                decided_at = $now,
                reason = $reason,
                resolution_source = $resolutionSource
            WHERE id = $id AND decision IS NULL AND (expires_at IS NULL OR expires_at > $now)
            """,
            new { decision, decidedBy, now, reason, resolutionSource, id = requestId },
            transaction) > 0;
        if (!resolved)
        {
            transaction.Rollback();
            return false;
        }

        StopPendingPackedSiblings(
            connection,
            transaction,
            resolutionEvent,
            requestId,
            decision,
            decidedBy,
            now,
            reason,
            resolutionSource);

        SuppressIncompletePackWork(
            connection,
            transaction,
            resolutionEvent,
            ref continuation,
            ref resolutionEvent);

        if (continuation is not null)
        {
            InsertContinuation(connection, transaction, continuation, now);
        }

        if (resolutionEvent is not null)
        {
            InsertResolutionEvent(connection, transaction, resolutionEvent, now);
        }

        transaction.Commit();
        return true;
    }

    public bool Expire(string requestId, DateTimeOffset nowUtc, string reason)
        => Expire(requestId, nowUtc, reason, continuation: null, resolutionEvent: null);

    public bool Expire(
        string requestId,
        DateTimeOffset nowUtc,
        string reason,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
    {
        string now = nowUtc.ToString("O");
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        bool expired = connection.Execute(
            $"""
            UPDATE tool_approval_requests
            SET decision = 'expired',
                decided_at = $now,
                reason = $reason,
                resolution_source = 'system_timeout'
            WHERE id = $id AND decision IS NULL
              AND ((expires_at IS NOT NULL AND expires_at <= $now) OR {obsoleteAutopilotApprovalSql})
            """,
            new { id = requestId, now, reason }, transaction) > 0;
        if (!expired)
        {
            transaction.Rollback();
            return false;
        }

        SuppressIncompletePackWork(
            connection,
            transaction,
            resolutionEvent,
            ref continuation,
            ref resolutionEvent);

        if (continuation is not null)
        {
            InsertContinuation(connection, transaction, continuation, now);
        }

        if (resolutionEvent is not null)
        {
            InsertResolutionEvent(connection, transaction, resolutionEvent, now);
        }

        transaction.Commit();
        return true;
    }

    public ApprovalResolutionEventLease? TryLeaseResolutionEvent(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        string now = nowUtc.ToString("O");
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT event_id AS EventId, request_id AS RequestId, payload_json AS PayloadJson,
                   agent_id AS AgentId, session_id AS SessionId, user_id AS UserId,
                   run_id AS RunId, root_run_id AS RootRunId, parent_run_id AS ParentRunId,
                   parent_session_id AS ParentSessionId, parent_tool_call_id AS ParentToolCallId,
                   continuation_run_id AS ContinuationRunId, trace_id AS TraceId,
                   tool_call_id AS ToolCallId, checkpoint_id AS CheckpointId,
                   attempt_count AS AttemptCount
            FROM approval_resolution_events
            WHERE ($requestId IS NULL OR request_id = $requestId)
              AND ((status = 'pending' AND available_at <= $now)
                OR (status = 'leased' AND lease_expires_at <= $now))
            ORDER BY created_at
            LIMIT 1
            """;
        select.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        select.Parameters.AddWithValue("$now", now);
        using SqliteDataReader reader = select.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return null;
        }

        var row = new ApprovalResolutionEventRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            ReadNullableString(reader, 12),
            ReadNullableString(reader, 13),
            ReadNullableString(reader, 14),
            reader.GetInt64(15));
        reader.Close();

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE approval_resolution_events
            SET status = 'leased', attempt_count = attempt_count + 1,
                lease_owner = $leaseOwner, lease_expires_at = $leaseExpiresAt, updated_at = $now
            WHERE event_id = $eventId
              AND ((status = 'pending' AND available_at <= $now)
                OR (status = 'leased' AND lease_expires_at <= $now))
            """;
        update.Parameters.AddWithValue("$eventId", row.EventId);
        update.Parameters.AddWithValue("$leaseOwner", leaseOwner);
        update.Parameters.AddWithValue("$leaseExpiresAt", leaseExpiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$now", now);
        int changed = update.ExecuteNonQuery();
        if (changed != 1)
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return new ApprovalResolutionEventLease(row.ToWork(), leaseOwner, checked((int)row.AttemptCount + 1));
    }

    public void MarkResolutionEventPublished(ApprovalResolutionEventLease lease, DateTimeOffset nowUtc)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        string now = nowUtc.ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE approval_resolution_events
            SET status = 'published', published_at = $now, lease_owner = NULL,
                lease_expires_at = NULL, updated_at = $now, last_error = NULL
            WHERE event_id = $eventId AND status = 'leased' AND lease_owner = $leaseOwner
            """;
        command.Parameters.AddWithValue("$eventId", lease.Work.EventId);
        command.Parameters.AddWithValue("$leaseOwner", lease.LeaseOwner);
        command.Parameters.AddWithValue("$now", now);
        int changed = command.ExecuteNonQuery();
        if (changed != 1)
        {
            throw new InvalidOperationException($"Approval resolution event '{lease.Work.EventId}' lease is no longer owned by '{lease.LeaseOwner}'.");
        }
    }

    public ApprovalResolutionEventFailureDisposition FailResolutionEvent(
        ApprovalResolutionEventLease lease,
        string safeError,
        DateTimeOffset nowUtc)
    {
        const int maxAttempts = 8;
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        string normalized = string.IsNullOrWhiteSpace(safeError) ? "publication_failed" : safeError.ReplaceLineEndings(" ").Trim();
        bool deadLetter = lease.AttemptCount >= maxAttempts;
        string now = nowUtc.ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE approval_resolution_events
            SET status = $status, available_at = $availableAt, lease_owner = NULL,
                lease_expires_at = NULL, last_error = $error, updated_at = $now
            WHERE event_id = $eventId AND status = 'leased' AND lease_owner = $leaseOwner
            """;
        command.Parameters.AddWithValue("$eventId", lease.Work.EventId);
        command.Parameters.AddWithValue("$leaseOwner", lease.LeaseOwner);
        command.Parameters.AddWithValue("$status", deadLetter ? "dead_letter" : "pending");
        command.Parameters.AddWithValue(
            "$availableAt",
            nowUtc.AddSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(lease.AttemptCount, 1, 6)))).ToString("O"));
        command.Parameters.AddWithValue("$error", normalized[..Math.Min(normalized.Length, 512)]);
        command.Parameters.AddWithValue("$now", now);
        int changed = command.ExecuteNonQuery();
        if (changed != 1)
        {
            throw new InvalidOperationException($"Approval resolution event '{lease.Work.EventId}' lease is no longer owned by '{lease.LeaseOwner}'.");
        }

        return deadLetter
            ? ApprovalResolutionEventFailureDisposition.DeadLettered
            : ApprovalResolutionEventFailureDisposition.RetryScheduled;
    }

    public IReadOnlyList<ApprovalResolutionEventDeadLetter> ListResolutionEventDeadLetters(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id AS EventId, request_id AS RequestId, attempt_count AS AttemptCount,
                   last_error AS LastError, updated_at AS UpdatedAt
            FROM approval_resolution_events
            WHERE status = 'dead_letter'
            ORDER BY updated_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var results = new List<ApprovalResolutionEventDeadLetter>();
        while (reader.Read())
        {
            results.Add(new ApprovalResolutionEventDeadLetter(
                reader.GetString(0),
                reader.GetString(1),
                checked((int)reader.GetInt64(2)),
                ReadNullableString(reader, 3),
                reader.GetString(4)));
        }

        return results;
    }

    private static void InsertContinuation(SqliteConnection connection, SqliteTransaction transaction, ApprovalContinuationWork continuation, string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO approval_continuation_jobs (
                request_id, session_id, target_type, target_id, user_id, approved,
                resolution_source, reason, decided_by_user_id, stop_run,
                status, attempt_count, available_at, lease_owner, lease_expires_at,
                last_error, created_at, updated_at)
            VALUES (
                $RequestId, $SessionId, $TargetType, $TargetId, $UserId, $Approved,
                $ResolutionSource, $Reason, $DecidedByUserId, $StopRun,
                'pending', 0, $now, NULL, NULL, NULL, $now, $now)
            """;
        command.Parameters.AddWithValue("$RequestId", continuation.RequestId);
        command.Parameters.AddWithValue("$SessionId", continuation.SessionId);
        command.Parameters.AddWithValue("$TargetType", continuation.TargetType);
        command.Parameters.AddWithValue("$TargetId", continuation.TargetId);
        command.Parameters.AddWithValue("$UserId", (object?)continuation.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$Approved", continuation.Approved ? 1 : 0);
        command.Parameters.AddWithValue("$ResolutionSource", (int)continuation.Source);
        command.Parameters.AddWithValue("$Reason", (object?)continuation.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$DecidedByUserId", (object?)continuation.DecidedByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$StopRun", continuation.StopRun ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void StopPendingPackedSiblings(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApprovalResolutionEventWork? candidate,
        string currentRequestId,
        string decision,
        string? decidedBy,
        string now,
        string? reason,
        string resolutionSource)
    {
        if (!string.Equals(
            PackedToolApproval.NormalizeStorageDecision(decision),
            "stopped",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IReadOnlyList<string>? packedRequestIds = candidate?.PackedRequestIds;
        if (packedRequestIds is null || packedRequestIds.Count <= 1)
        {
            return;
        }

        foreach (string requestId in packedRequestIds)
        {
            if (string.Equals(requestId, currentRequestId, StringComparison.Ordinal))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE tool_approval_requests
                SET decision = 'stopped',
                    decided_by = $decidedBy,
                    decided_at = $now,
                    reason = $reason,
                    resolution_source = $resolutionSource
                WHERE id = $id AND decision IS NULL
                """;
            command.Parameters.AddWithValue("$decidedBy", (object?)decidedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("$resolutionSource", resolutionSource);
            command.Parameters.AddWithValue("$id", requestId);
            command.ExecuteNonQuery();
        }
    }

    private static void SuppressIncompletePackWork(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ApprovalResolutionEventWork? candidate,
        ref ApprovalContinuationWork? continuation,
        ref ApprovalResolutionEventWork? resolutionEvent)
    {
        if (!PackedToolApproval.PackHasPendingMembers(
            candidate?.PackedRequestIds,
            id => ReadDecision(connection, transaction, id)))
        {
            return;
        }

        continuation = null;
        resolutionEvent = null;
    }

    private static string? ReadDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT decision FROM tool_approval_requests WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", requestId);
        object? value = command.ExecuteScalar();
        return value is string decision ? decision : null;
    }

    private static void InsertResolutionEvent(SqliteConnection connection, SqliteTransaction transaction, ApprovalResolutionEventWork work, string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO approval_resolution_events (
                event_id, request_id, payload_json, agent_id, session_id, user_id, run_id, root_run_id,
                parent_run_id, parent_session_id, parent_tool_call_id, continuation_run_id, trace_id,
                tool_call_id, checkpoint_id, status, available_at, lease_owner, lease_expires_at,
                published_at, attempt_count, last_error, created_at, updated_at)
            VALUES ($EventId, $RequestId, $PayloadJson, $AgentId, $SessionId, $UserId, $RunId, $RootRunId,
                $ParentRunId, $ParentSessionId, $ParentToolCallId, $ContinuationRunId, $TraceId,
                $ToolCallId, $CheckpointId, 'pending', $now, NULL, NULL, NULL, 0, NULL, $now, $now)
            ON CONFLICT(request_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$EventId", work.EventId);
        command.Parameters.AddWithValue("$RequestId", work.RequestId);
        command.Parameters.AddWithValue("$PayloadJson", work.Payload.GetRawText());
        command.Parameters.AddWithValue("$AgentId", work.Context.AgentId);
        command.Parameters.AddWithValue("$SessionId", work.Context.SessionId);
        command.Parameters.AddWithValue("$UserId", work.Context.UserId);
        command.Parameters.AddWithValue("$RunId", work.Context.RunId);
        command.Parameters.AddWithValue("$RootRunId", work.Context.RootRunId);
        command.Parameters.AddWithValue("$ParentRunId", (object?)work.Context.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ParentSessionId", (object?)work.Context.ParentSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ParentToolCallId", (object?)work.Context.ParentToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ContinuationRunId", (object?)work.Context.ContinuationRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$TraceId", (object?)work.Context.TraceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ToolCallId", (object?)work.ToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$CheckpointId", (object?)work.CheckpointId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool HasSameImmutablePayload(
        GatewayToolApprovalRequest existing,
        GatewayToolApprovalRequest requested,
        string tetherMode)
        => existing.Id == requested.Id
           && existing.SessionId == requested.SessionId
           && existing.RunId == requested.RunId
           && existing.CheckpointId == requested.CheckpointId
           && existing.AgentId == requested.AgentId
           && existing.UserId == requested.UserId
           && existing.ToolCallId == requested.ToolCallId
           && existing.ToolName == requested.ToolName
           && existing.ToolType == requested.ToolType
           && existing.McpServerId == requested.McpServerId
           && existing.ArgumentsJson == requested.ArgumentsJson
           && existing.RequestedAt == requested.RequestedAt
           && existing.ExpiresAt == requested.ExpiresAt
           && existing.OuterSessionId == requested.OuterSessionId
           && existing.OuterAgentId == requested.OuterAgentId
           && existing.ResolutionSource == requested.ResolutionSource
           && existing.AgentDefinitionRevision == requested.AgentDefinitionRevision
           && existing.Kind == requested.Kind
           && existing.WorkflowId == requested.WorkflowId
           && existing.InstanceId == requested.InstanceId
           && existing.NodeId == requested.NodeId
           && string.Equals(existing.ChannelTetherMode, tetherMode, StringComparison.Ordinal);

    private sealed record ApprovalResolutionEventRow(
        string EventId, string RequestId, string PayloadJson, string AgentId, string SessionId,
        string UserId, string RunId, string RootRunId, string? ParentRunId, string? ParentSessionId,
        string? ParentToolCallId, string? ContinuationRunId, string? TraceId, string? ToolCallId,
        string? CheckpointId,
        long AttemptCount)
    {
        public ApprovalResolutionEventWork ToWork()
        {
            JsonElement payload = ParsePayload();
            bool stopRun = payload.TryGetProperty("decision", out JsonElement decision)
                && string.Equals(decision.GetString(), ApprovalActionCatalog.StopRun, StringComparison.Ordinal);
            return new(
                EventId,
                RequestId,
                payload,
                new NaradEventContext(
                    AgentId, SessionId, UserId, RunId, RootRunId, ParentRunId, ParentSessionId,
                    ParentToolCallId, ContinuationRunId, TraceId: TraceId),
                ToolCallId,
                CheckpointId,
                StopRun: stopRun,
                StopThreadId: ParentSessionId ?? SessionId);
        }

        private System.Text.Json.JsonElement ParsePayload()
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(PayloadJson);
            return document.RootElement.Clone();
        }
    }

    public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute(
            $"""
            UPDATE tool_approval_requests
            SET {channelTetherModeColumn} = $ChannelTetherMode
            WHERE id = $id
            """,
            new
            {
                id = requestId,
                ChannelTetherMode = mode.ToStorageValue(),
            });
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListDueForExpiry(DateTimeOffset nowUtc)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                   checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                   expires_at AS ExpiresAt,
                   {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
            FROM tool_approval_requests
            WHERE decision IS NULL
              AND ((expires_at IS NOT NULL AND expires_at <= $now AND tool_type <> 'autopilot_prerun')
                OR {obsoleteAutopilotApprovalSql})
            ORDER BY expires_at ASC
            """,
            new { now = nowUtc.ToString("O") }).AsList();
    }

    public bool TryScheduleHitlRetry(string requestId, DateTimeOffset newExpiresAtUtc, DateTimeOffset nowUtc)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Execute(
            """
            UPDATE tool_approval_requests
            SET expires_at = $expiresAt,
                reason = $retryMarker
            WHERE id = $id
              AND decision IS NULL
              AND expires_at IS NOT NULL
              AND expires_at <= $now
              AND (reason IS NULL OR reason <> $retryMarker)
            """,
            new
            {
                id = requestId,
                expiresAt = newExpiresAtUtc.ToString("O"),
                now = nowUtc.ToString("O"),
                retryMarker = Kautuka.Gateway.Domain.Runtime.HitlExpiryReasons.RetryMarker,
            }) > 0;
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        string now = DateTimeOffset.UtcNow.ToString("O");

        if (adminAll || userId is null)
        {
            return connection.Query<GatewayToolApprovalRequest>(
                $"""
                SELECT id,
                       session_id AS SessionId,
                       run_id AS RunId,
                      checkpoint_id AS CheckpointId,
                       agent_id AS AgentId,
                       user_id AS UserId,
                       tool_call_id AS ToolCallId,
                       tool_name AS ToolName,
                       tool_type AS ToolType,
                       mcp_server_id AS McpServerId,
                       arguments_json AS ArgumentsJson,
                       requested_at AS RequestedAt,
                       decision,
                       decided_by AS DecidedBy,
                       decided_at AS DecidedAt,
                       reason,
                      expires_at AS ExpiresAt,
                      {channelTetherModeColumn} AS ChannelTetherMode,
                      outer_session_id AS OuterSessionId,
                      outer_agent_id AS OuterAgentId,
                      resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
                FROM tool_approval_requests
                WHERE decision IS NULL AND (expires_at IS NULL OR expires_at > $now)
                ORDER BY requested_at ASC
                """,
                new { now }).AsList();
        }

        return connection.Query<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                     checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                     expires_at AS ExpiresAt,
                                         {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
            FROM tool_approval_requests
            WHERE decision IS NULL
              AND (expires_at IS NULL OR expires_at > $now)
              AND (user_id = $userId OR user_id IS NULL)
            ORDER BY requested_at ASC
            """,
            new { userId, now }).AsList();
    }

    public GatewayToolApprovalRequest? GetById(string requestId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                     checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                     expires_at AS ExpiresAt,
                                         {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId,
                   agent_definition_revision AS AgentDefinitionRevision
            FROM tool_approval_requests
            WHERE id = $id
            """,
            new { id = requestId });
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<GatewayToolApprovalRequest>(
            $"""
                SELECT id,
                 session_id AS SessionId,
                 run_id AS RunId,
                 checkpoint_id AS CheckpointId,
                 agent_id AS AgentId,
                 user_id AS UserId,
                 tool_call_id AS ToolCallId,
                 tool_name AS ToolName,
                 tool_type AS ToolType,
                 mcp_server_id AS McpServerId,
                 arguments_json AS ArgumentsJson,
                 requested_at AS RequestedAt,
                 decision,
                 decided_by AS DecidedBy,
                 decided_at AS DecidedAt,
                 reason,
                 expires_at AS ExpiresAt,
                 {channelTetherModeColumn} AS ChannelTetherMode,
                 outer_session_id AS OuterSessionId,
                 outer_agent_id AS OuterAgentId,
                 resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
            FROM tool_approval_requests
            WHERE session_id = $sessionId
            ORDER BY requested_at ASC
            """,
            new { sessionId }).AsList();
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string outerSessionId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.Query<GatewayToolApprovalRequest>(
            $"""
                SELECT id,
                 session_id AS SessionId,
                 run_id AS RunId,
                 checkpoint_id AS CheckpointId,
                 agent_id AS AgentId,
                 user_id AS UserId,
                 tool_call_id AS ToolCallId,
                 tool_name AS ToolName,
                 tool_type AS ToolType,
                 mcp_server_id AS McpServerId,
                 arguments_json AS ArgumentsJson,
                 requested_at AS RequestedAt,
                 decision,
                 decided_by AS DecidedBy,
                 decided_at AS DecidedAt,
                 reason,
                 expires_at AS ExpiresAt,
                 {channelTetherModeColumn} AS ChannelTetherMode,
                 outer_session_id AS OuterSessionId,
                 outer_agent_id AS OuterAgentId,
                 resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
                FROM tool_approval_requests
                WHERE outer_session_id = $outerSessionId
                ORDER BY requested_at ASC
                """,
            new { outerSessionId }).AsList();
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        if (adminAll || userId is null)
        {
            return connection.Query<GatewayToolApprovalRequest>(
                $"""
                SELECT id,
                       session_id AS SessionId,
                       run_id AS RunId,
                      checkpoint_id AS CheckpointId,
                       agent_id AS AgentId,
                       user_id AS UserId,
                       tool_call_id AS ToolCallId,
                       tool_name AS ToolName,
                       tool_type AS ToolType,
                       mcp_server_id AS McpServerId,
                       arguments_json AS ArgumentsJson,
                       requested_at AS RequestedAt,
                       decision,
                       decided_by AS DecidedBy,
                       decided_at AS DecidedAt,
                       reason,
                      expires_at AS ExpiresAt,
                      {channelTetherModeColumn} AS ChannelTetherMode,
                      outer_session_id AS OuterSessionId,
                      outer_agent_id AS OuterAgentId,
                      resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
                FROM tool_approval_requests
                WHERE decision IS NOT NULL
                ORDER BY decided_at DESC
                LIMIT $limit OFFSET $offset
                """,
                new { limit, offset }).AsList();
        }

        return connection.Query<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                     checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                     expires_at AS ExpiresAt,
                                         {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
            FROM tool_approval_requests
            WHERE decision IS NOT NULL AND (user_id = $userId OR user_id IS NULL)
            ORDER BY decided_at DESC
            LIMIT $limit OFFSET $offset
            """,
            new { userId, limit, offset }).AsList();
    }

    public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                     checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                   expires_at AS ExpiresAt,
                   {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId
            FROM tool_approval_requests
            WHERE run_id = $runId AND tool_call_id = $toolCallId AND tool_name = $toolName
              AND decision IS NOT NULL
            ORDER BY decided_at DESC
            LIMIT 1
            """,
            new { runId, toolCallId, toolName });
    }

    public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<GatewayToolApprovalRequest>(
            $"""
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                     checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                   expires_at AS ExpiresAt,
                   {channelTetherModeColumn} AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId
            FROM tool_approval_requests
            WHERE run_id = $runId AND tool_name = $toolName AND arguments_json IS $argumentsJson
              AND decision IS NOT NULL
            ORDER BY decided_at DESC
            LIMIT 1
            """,
            new { runId, toolName, argumentsJson });
    }

    public IReadOnlyList<HitlApprovalStatRow> ListHitlStats(HitlApprovalStatsQuery query)
    {
        // Manual ADO.NET: Dapper.AOT does not intercept Query<HitlApprovalStatRow>, and
        // Native AOT forbids Dapper's runtime IL fallback (PlatformNotSupportedException).
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        busy.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(query.AgentId))
        {
            command.CommandText =
                """
                SELECT
                    CASE
                        WHEN tool_type = 'skill'
                            THEN COALESCE(json_extract(arguments_json, '$.skillId'), tool_name)
                        ELSE tool_name
                    END AS ToolName,
                    tool_type AS ToolType,
                    mcp_server_id AS McpServerId,
                    CASE
                        WHEN tool_type = 'skill' THEN json_extract(arguments_json, '$.skillId')
                        ELSE NULL
                    END AS SkillId,
                    SUM(CASE WHEN decision = 'approved' THEN 1 ELSE 0 END) AS Approved,
                    SUM(CASE WHEN decision = 'denied' THEN 1 ELSE 0 END) AS Denied,
                    MAX(CASE WHEN decision = 'denied' THEN decided_at END) AS LastDeniedAt,
                    COUNT(DISTINCT agent_id) AS AgentCount,
                    agent_definition_revision AS AgentDefinitionRevision
                FROM tool_approval_requests
                WHERE decision IN ('approved', 'denied')
                  AND decided_at IS NOT NULL
                  AND tool_type <> 'autopilot_prerun'
                  AND ($since IS NULL OR decided_at >= $since)
                  AND ($adminAll = 1 OR user_id = $userId OR user_id IS NULL)
                  AND ($agentId IS NULL OR agent_id = $agentId OR outer_agent_id = $agentId)
                  AND ($mcpServerId IS NULL
                       OR mcp_server_id = $mcpServerId
                       OR (
                            mcp_server_id IS NULL
                            AND $connectorToolNamesJson IS NOT NULL
                            AND tool_name IN (SELECT value FROM json_each($connectorToolNamesJson))
                          ))
                  AND ($skillId IS NULL OR (tool_type = 'skill' AND json_extract(arguments_json, '$.skillId') = $skillId))
                GROUP BY
                    CASE
                        WHEN tool_type = 'skill'
                            THEN COALESCE(json_extract(arguments_json, '$.skillId'), tool_name)
                        ELSE tool_name
                    END,
                    tool_type,
                    mcp_server_id,
                    CASE
                        WHEN tool_type = 'skill' THEN json_extract(arguments_json, '$.skillId')
                        ELSE NULL
                    END,
                    agent_definition_revision
                ORDER BY
                    CASE WHEN agent_definition_revision IS NULL THEN 1 ELSE 0 END,
                    agent_definition_revision DESC,
                    (Approved + Denied) DESC,
                    ToolName ASC
                """;
        }
        else
        {
            command.CommandText =
                """
                SELECT
                    CASE
                        WHEN tool_type = 'skill'
                            THEN COALESCE(json_extract(arguments_json, '$.skillId'), tool_name)
                        ELSE tool_name
                    END AS ToolName,
                    tool_type AS ToolType,
                    mcp_server_id AS McpServerId,
                    CASE
                        WHEN tool_type = 'skill' THEN json_extract(arguments_json, '$.skillId')
                        ELSE NULL
                    END AS SkillId,
                    SUM(CASE WHEN decision = 'approved' THEN 1 ELSE 0 END) AS Approved,
                    SUM(CASE WHEN decision = 'denied' THEN 1 ELSE 0 END) AS Denied,
                    MAX(CASE WHEN decision = 'denied' THEN decided_at END) AS LastDeniedAt,
                    COUNT(DISTINCT agent_id) AS AgentCount,
                    NULL AS AgentDefinitionRevision
                FROM tool_approval_requests
                WHERE decision IN ('approved', 'denied')
                  AND decided_at IS NOT NULL
                  AND tool_type <> 'autopilot_prerun'
                  AND ($since IS NULL OR decided_at >= $since)
                  AND ($adminAll = 1 OR user_id = $userId OR user_id IS NULL)
                  AND ($agentId IS NULL OR agent_id = $agentId OR outer_agent_id = $agentId)
                  AND ($mcpServerId IS NULL
                       OR mcp_server_id = $mcpServerId
                       OR (
                            mcp_server_id IS NULL
                            AND $connectorToolNamesJson IS NOT NULL
                            AND tool_name IN (SELECT value FROM json_each($connectorToolNamesJson))
                          ))
                  AND ($skillId IS NULL OR (tool_type = 'skill' AND json_extract(arguments_json, '$.skillId') = $skillId))
                GROUP BY
                    CASE
                        WHEN tool_type = 'skill'
                            THEN COALESCE(json_extract(arguments_json, '$.skillId'), tool_name)
                        ELSE tool_name
                    END,
                    tool_type,
                    mcp_server_id,
                    CASE
                        WHEN tool_type = 'skill' THEN json_extract(arguments_json, '$.skillId')
                        ELSE NULL
                    END
                ORDER BY (Approved + Denied) DESC, ToolName ASC
                """;
        }

        BindHitlStatsFilter(command, query);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<HitlApprovalStatRow>();
        while (reader.Read())
        {
            rows.Add(new HitlApprovalStatRow
            {
                ToolName = reader.GetString(0),
                ToolType = reader.GetString(1),
                McpServerId = ReadNullableString(reader, 2),
                SkillId = ReadNullableString(reader, 3),
                Approved = reader.GetInt64(4),
                Denied = reader.GetInt64(5),
                LastDeniedAt = ReadNullableString(reader, 6),
                AgentCount = reader.GetInt64(7),
                AgentDefinitionRevision = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            });
        }

        return rows;
    }

    public IReadOnlyList<GatewayToolApprovalRequest> ListDeniedHistory(HitlApprovalStatsQuery query, int limit)
    {
        // Same AOT constraint as ListHitlStats: keep this path free of Dapper Query<T>.
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000;";
        busy.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   session_id AS SessionId,
                   run_id AS RunId,
                   checkpoint_id AS CheckpointId,
                   agent_id AS AgentId,
                   user_id AS UserId,
                   tool_call_id AS ToolCallId,
                   tool_name AS ToolName,
                   tool_type AS ToolType,
                   mcp_server_id AS McpServerId,
                   arguments_json AS ArgumentsJson,
                   requested_at AS RequestedAt,
                   decision,
                   decided_by AS DecidedBy,
                   decided_at AS DecidedAt,
                   reason,
                   expires_at AS ExpiresAt,
                   channel_tether_mode AS ChannelTetherMode,
                   outer_session_id AS OuterSessionId,
                   outer_agent_id AS OuterAgentId,
                   resolution_source AS ResolutionSource,
                   kind AS Kind,
                   workflow_id AS WorkflowId,
                   instance_id AS InstanceId,
                   node_id AS NodeId,
                   agent_definition_revision AS AgentDefinitionRevision
            FROM tool_approval_requests
            WHERE decision IN ('approved', 'denied')
              AND decided_at IS NOT NULL
              AND tool_type <> 'autopilot_prerun'
              AND ($since IS NULL OR decided_at >= $since)
              AND ($adminAll = 1 OR user_id = $userId OR user_id IS NULL)
              AND ($agentId IS NULL OR agent_id = $agentId OR outer_agent_id = $agentId)
              AND ($mcpServerId IS NULL
                   OR mcp_server_id = $mcpServerId
                   OR (
                        mcp_server_id IS NULL
                        AND $connectorToolNamesJson IS NOT NULL
                        AND tool_name IN (SELECT value FROM json_each($connectorToolNamesJson))
                      ))
              AND ($skillId IS NULL OR (tool_type = 'skill' AND json_extract(arguments_json, '$.skillId') = $skillId))
              AND decision = 'denied'
              AND ($toolName IS NULL OR tool_name = $toolName)
              AND (
                    ($ungroupedRevision = 0 AND $agentDefinitionRevision IS NULL)
                    OR ($ungroupedRevision = 1 AND agent_definition_revision IS NULL)
                    OR ($ungroupedRevision = 0 AND agent_definition_revision = $agentDefinitionRevision)
                  )
            ORDER BY decided_at DESC
            LIMIT $limit
            """;
        BindHitlStatsFilter(command, query);
        command.Parameters.AddWithValue("$toolName", (object?)query.ToolName ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<GatewayToolApprovalRequest>();
        while (reader.Read())
        {
            rows.Add(new GatewayToolApprovalRequest(
                Id: reader.GetString(0),
                SessionId: ReadNullableString(reader, 1),
                RunId: ReadNullableString(reader, 2),
                CheckpointId: ReadNullableString(reader, 3),
                AgentId: ReadNullableString(reader, 4),
                UserId: ReadNullableString(reader, 5),
                ToolCallId: reader.GetString(6),
                ToolName: reader.GetString(7),
                ToolType: reader.GetString(8),
                McpServerId: ReadNullableString(reader, 9),
                ArgumentsJson: ReadNullableString(reader, 10),
                RequestedAt: reader.GetString(11),
                Decision: ReadNullableString(reader, 12),
                DecidedBy: ReadNullableString(reader, 13),
                DecidedAt: ReadNullableString(reader, 14),
                Reason: ReadNullableString(reader, 15),
                ExpiresAt: ReadNullableString(reader, 16),
                ChannelTetherMode: ReadNullableString(reader, 17),
                OuterSessionId: ReadNullableString(reader, 18),
                OuterAgentId: ReadNullableString(reader, 19),
                ResolutionSource: ReadNullableString(reader, 20),
                AgentDefinitionRevision: reader.IsDBNull(25) ? null : reader.GetInt64(25),
                Kind: ReadNullableString(reader, 21) ?? "tool",
                WorkflowId: ReadNullableString(reader, 22),
                InstanceId: ReadNullableString(reader, 23),
                NodeId: ReadNullableString(reader, 24)));
        }

        return rows;
    }

    private static void BindHitlStatsFilter(SqliteCommand command, HitlApprovalStatsQuery query)
    {
        command.Parameters.AddWithValue("$userId", (object?)query.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$adminAll", query.AdminAll ? 1 : 0);
        command.Parameters.AddWithValue("$since", (object?)query.Since ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentId", (object?)query.AgentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mcpServerId", (object?)query.McpServerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$skillId", (object?)query.SkillId ?? DBNull.Value);
        string? connectorToolNamesJson = query.ConnectorToolNames is { Count: > 0 }
            ? JsonSerializer.Serialize(query.ConnectorToolNames.ToList(), KautukaJsonContext.Default.ListString)
            : null;
        command.Parameters.AddWithValue(
            "$connectorToolNamesJson",
            (object?)connectorToolNamesJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$agentDefinitionRevision",
            query.AgentDefinitionRevision.HasValue
                ? query.AgentDefinitionRevision.Value
                : DBNull.Value);
        command.Parameters.AddWithValue("$ungroupedRevision", query.UngroupedRevisionOnly ? 1 : 0);
    }
}
