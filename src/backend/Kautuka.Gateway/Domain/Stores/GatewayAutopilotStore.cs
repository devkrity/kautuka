using System.Text.Json;

using Dapper;

using Kautuka.Gateway.Domain.Autopilot;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayAutopilotStore(KautukaDatabase database) : IGatewayAutopilotStore
{
    public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule)
    {
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO autopilot_schedules
                (id, owner_user_id, target_type, target_id, cron_expression, timezone_id,
                 mode, requires_pre_run_approval, schedule_prompt_context, workspace_file_paths,
                 notify_ui_inbox, notify_channels, pre_run_approval_expiry_policy,
                 pre_run_approval_timeout_seconds, overlap_policy, is_paused,
                 created_at, updated_at, next_due_at, last_evaluated_at,
                 lease_owner, lease_expires_at)
            VALUES
                (@id, @ownerUserId, @targetType, @targetId, @cronExpression, @timeZoneId,
                 @mode, @requiresPreRunApproval, @schedulePromptContext, @workspaceFilePaths,
                 @notifyUiInbox, @notifyChannels, @preRunApprovalExpiryPolicy,
                 @preRunApprovalTimeoutSeconds, @overlapPolicy, @isPaused,
                 @createdAt, @updatedAt, @nextDueAt, @lastEvaluatedAt,
                 @leaseOwner, @leaseExpiresAt)
            """,
            new
            {
                id = schedule.Id,
                ownerUserId = schedule.OwnerUserId,
                targetType = schedule.TargetType,
                targetId = schedule.TargetId,
                cronExpression = schedule.CronExpression,
                timeZoneId = schedule.TimeZoneId,
                mode = schedule.Mode,
                requiresPreRunApproval = schedule.RequiresPreRunApproval,
                schedulePromptContext = schedule.SchedulePromptContext,
                workspaceFilePaths = SerializeWorkspaceFilePaths(schedule.WorkspaceFilePaths),
                notifyUiInbox = schedule.NotifyUiInbox,
                notifyChannels = schedule.NotifyChannels,
                preRunApprovalExpiryPolicy = schedule.PreRunApprovalExpiryPolicy,
                preRunApprovalTimeoutSeconds = schedule.PreRunApprovalTimeoutSeconds,
                overlapPolicy = schedule.OverlapPolicy,
                isPaused = schedule.IsPaused,
                createdAt = schedule.CreatedAt,
                updatedAt = schedule.UpdatedAt,
                nextDueAt = schedule.NextDueAt,
                lastEvaluatedAt = schedule.LastEvaluatedAt,
                leaseOwner = schedule.LeaseOwner,
                leaseExpiresAt = schedule.LeaseExpiresAt,
            });

        return schedule;
    }

    public bool UpdateSchedule(GatewayAutopilotSchedule schedule)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE autopilot_schedules
            SET target_type = @targetType,
                target_id = @targetId,
                cron_expression = @cronExpression,
                timezone_id = @timeZoneId,
                mode = @mode,
                requires_pre_run_approval = @requiresPreRunApproval,
                schedule_prompt_context = @schedulePromptContext,
                workspace_file_paths = @workspaceFilePaths,
                notify_ui_inbox = @notifyUiInbox,
                notify_channels = @notifyChannels,
                pre_run_approval_expiry_policy = @preRunApprovalExpiryPolicy,
                pre_run_approval_timeout_seconds = @preRunApprovalTimeoutSeconds,
                overlap_policy = @overlapPolicy,
                is_paused = @isPaused,
                updated_at = @updatedAt,
                next_due_at = @nextDueAt,
                last_evaluated_at = @lastEvaluatedAt,
                lease_owner = @leaseOwner,
                lease_expires_at = @leaseExpiresAt
            WHERE id = @id AND owner_user_id = @ownerUserId
            """,
            new
            {
                id = schedule.Id,
                ownerUserId = schedule.OwnerUserId,
                targetType = schedule.TargetType,
                targetId = schedule.TargetId,
                cronExpression = schedule.CronExpression,
                timeZoneId = schedule.TimeZoneId,
                mode = schedule.Mode,
                requiresPreRunApproval = schedule.RequiresPreRunApproval,
                schedulePromptContext = schedule.SchedulePromptContext,
                workspaceFilePaths = SerializeWorkspaceFilePaths(schedule.WorkspaceFilePaths),
                notifyUiInbox = schedule.NotifyUiInbox,
                notifyChannels = schedule.NotifyChannels,
                preRunApprovalExpiryPolicy = schedule.PreRunApprovalExpiryPolicy,
                preRunApprovalTimeoutSeconds = schedule.PreRunApprovalTimeoutSeconds,
                overlapPolicy = schedule.OverlapPolicy,
                isPaused = schedule.IsPaused,
                updatedAt = schedule.UpdatedAt,
                nextDueAt = schedule.NextDueAt,
                lastEvaluatedAt = schedule.LastEvaluatedAt,
                leaseOwner = schedule.LeaseOwner,
                leaseExpiresAt = schedule.LeaseExpiresAt,
            }) > 0;
    }

    public bool DeleteSchedule(string scheduleId, string ownerUserId)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            """
            DELETE FROM autopilot_schedules
            WHERE id = @id AND owner_user_id = @ownerUserId
            """,
            new { id = scheduleId, ownerUserId }) > 0;
    }

    public GatewayAutopilotSchedule? GetSchedule(string scheduleId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<AutopilotScheduleRow>(
            selectSchedulesSql + " WHERE id = @id",
            new { id = scheduleId }) is { } row
            ? MapSchedule(row)
            : null;
    }

    public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId)
    {
        using var connection = OpenConnection();
        return [.. connection.Query<AutopilotScheduleRow>(
            selectSchedulesSql + " WHERE owner_user_id = @ownerUserId ORDER BY created_at DESC",
            new { ownerUserId }).Select(MapSchedule)];
    }

    public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset)
    {
        int boundedLimit = Math.Clamp(limit, 1, 500);
        int boundedOffset = Math.Max(0, offset);
        using var connection = OpenConnection();
        return [.. connection.Query<AutopilotScheduleRow>(
            selectSchedulesSql + " ORDER BY created_at DESC LIMIT @boundedLimit OFFSET @boundedOffset",
            new { boundedLimit, boundedOffset }).Select(MapSchedule)];
    }

    public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit)
    {
        string now = nowUtc.ToString("O");
        string leaseUntil = nowUtc.Add(leaseDuration).ToString("O");
        int boundedLimit = Math.Clamp(limit, 1, 100);

        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        IReadOnlyList<string> candidateIds = connection.Query<string>(
            """
            SELECT id
            FROM autopilot_schedules
            WHERE is_paused = 0
                            AND mode != @offMode
              AND next_due_at IS NOT NULL
                            AND next_due_at <= @now
                            AND (lease_expires_at IS NULL OR lease_expires_at <= @now)
            ORDER BY next_due_at ASC
                        LIMIT @limit
            """,
            new
            {
                now,
                limit = boundedLimit,
                offMode = AutopilotScheduleModes.Off,
            },
            transaction: transaction).AsList();

        List<GatewayAutopilotSchedule> leased = [];
        foreach (string id in candidateIds)
        {
            int updated = connection.Execute(
                """
                UPDATE autopilot_schedules
                                SET lease_owner = @leaseOwner,
                                        lease_expires_at = @leaseUntil,
                                        updated_at = @now
                                WHERE id = @id
                                    AND (lease_expires_at IS NULL OR lease_expires_at <= @now)
                """,
                new
                {
                    id,
                    leaseOwner,
                    leaseUntil,
                    now,
                },
                transaction: transaction);

            if (updated <= 0)
            {
                continue;
            }

            GatewayAutopilotSchedule? schedule = connection.QueryFirstOrDefault<AutopilotScheduleRow>(
                selectSchedulesSql + " WHERE id = @id",
                new { id },
                transaction: transaction) is { } row
                ? MapSchedule(row)
                : null;

            if (schedule is not null)
            {
                leased.Add(schedule);
            }
        }

        transaction.Commit();
        return leased;
    }

    public GatewayScheduledRun InsertRun(GatewayScheduledRun run)
    {
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO autopilot_runs
                (id, schedule_id, owner_user_id, target_type, target_id,
                 status, trigger_type, scheduled_for,
                 session_id, runtime_run_id, pre_run_approval_request_id,
                 input_json, output_json, error,
                 skip_reason, blocking_run_id, blocking_run_status_snapshot, blocking_run_state_category, blocked_at,
                 created_at, updated_at, started_at, ended_at)
            VALUES
                (@id, @scheduleId, @ownerUserId, @targetType, @targetId,
                 @status, @triggerType, @scheduledFor,
                 @sessionId, @runtimeRunId, @preRunApprovalRequestId,
                 @inputJson, @outputJson, @error,
                 @skipReason, @blockingRunId, @blockingRunStatusSnapshot, @blockingRunStateCategory, @blockedAt,
                 @createdAt, @updatedAt, @startedAt, @endedAt)
            """,
            new
            {
                id = run.Id,
                scheduleId = run.ScheduleId,
                ownerUserId = run.OwnerUserId,
                targetType = run.TargetType,
                targetId = run.TargetId,
                status = run.Status,
                triggerType = run.TriggerType,
                scheduledFor = run.ScheduledFor,
                sessionId = run.SessionId,
                runtimeRunId = run.RuntimeRunId,
                preRunApprovalRequestId = run.PreRunApprovalRequestId,
                inputJson = run.InputJson,
                outputJson = run.OutputJson,
                error = run.Error,
                skipReason = run.SkipReason,
                blockingRunId = run.BlockingRunId,
                blockingRunStatusSnapshot = run.BlockingRunStatusSnapshot,
                blockingRunStateCategory = run.BlockingRunStateCategory,
                blockedAt = run.BlockedAt,
                createdAt = run.CreatedAt,
                updatedAt = run.UpdatedAt,
                startedAt = run.StartedAt,
                endedAt = run.EndedAt,
            });

        return run;
    }

    public bool TryInsertRunUnlessNonTerminal(GatewayScheduledRun run, out GatewayScheduledRun? blockingRun)
    {
        try
        {
            InsertRun(run);
            blockingRun = null;
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            blockingRun = GetLatestNonTerminalRunBySchedule(run.ScheduleId);
            return false;
        }
    }

    public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<GatewayScheduledRun>(
            selectRunsSql + " WHERE schedule_id = @scheduleId ORDER BY created_at DESC LIMIT 1",
            new { scheduleId });
    }

    public GatewayScheduledRun? GetLatestNonTerminalRunBySchedule(string scheduleId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<GatewayScheduledRun>(
            selectRunsSql + " WHERE schedule_id = @scheduleId AND " + nonTerminalStatusSql + " ORDER BY created_at DESC LIMIT 1",
            new
            {
                scheduleId,
                completed = AutopilotRunStatuses.Completed,
                noOp = AutopilotRunStatuses.NoOp,
                failed = AutopilotRunStatuses.Failed,
                rejected = AutopilotRunStatuses.Rejected,
                timedOut = AutopilotRunStatuses.TimedOut,
                skipped = AutopilotRunStatuses.Skipped,
                stale = AutopilotRunStatuses.Stale,
            });
    }

    public GatewayScheduledRun? GetRunById(string runId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<GatewayScheduledRun>(
            selectRunsSql + " WHERE id = @runId LIMIT 1",
            new { runId });
    }

    public GatewayScheduledRun? GetRunByRuntimeRunId(string runtimeRunId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<GatewayScheduledRun>(
            selectRunsSql + " WHERE runtime_run_id = @runtimeRunId LIMIT 1",
            new { runtimeRunId });
    }

    public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<GatewayScheduledRun>(
            selectRunsSql + " WHERE session_id = @sessionId AND " + nonTerminalStatusSql + " ORDER BY created_at DESC LIMIT 1",
            new
            {
                sessionId,
                completed = AutopilotRunStatuses.Completed,
                noOp = AutopilotRunStatuses.NoOp,
                failed = AutopilotRunStatuses.Failed,
                rejected = AutopilotRunStatuses.Rejected,
                timedOut = AutopilotRunStatuses.TimedOut,
                skipped = AutopilotRunStatuses.Skipped,
                stale = AutopilotRunStatuses.Stale,
            });
    }

    public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit)
    {
        int boundedLimit = Math.Clamp(limit, 1, 500);
        using var connection = OpenConnection();
        return connection.Query<GatewayScheduledRun>(
            selectRunsSql + " WHERE status = @status ORDER BY created_at ASC LIMIT @boundedLimit",
            new { status, boundedLimit }).AsList();
    }

    public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        using var connection = OpenConnection();
        return connection.Query<GatewayScheduledRun>(
            selectRunsSql + " WHERE " + nonTerminalStatusSql + " ORDER BY created_at ASC LIMIT @boundedLimit",
            new
            {
                boundedLimit,
                completed = AutopilotRunStatuses.Completed,
                noOp = AutopilotRunStatuses.NoOp,
                failed = AutopilotRunStatuses.Failed,
                rejected = AutopilotRunStatuses.Rejected,
                timedOut = AutopilotRunStatuses.TimedOut,
                skipped = AutopilotRunStatuses.Skipped,
                stale = AutopilotRunStatuses.Stale,
            }).AsList();
    }

    public bool TransitionRunState(
        string runId,
        string expectedCurrentStatus,
        string nextStatus,
        string? error,
        string? outputJson,
        string? sessionId,
        string? runtimeRunId,
        string? preRunApprovalRequestId,
        DateTimeOffset nowUtc,
        bool markStarted,
        bool markEnded)
    {
        string now = nowUtc.ToString("O");
        string? startedAt = markStarted ? now : null;
        string? endedAt = markEnded ? now : null;

        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE autopilot_runs
            SET status = @nextStatus,
                error = COALESCE(@error, error),
                output_json = COALESCE(@outputJson, output_json),
                session_id = COALESCE(@sessionId, session_id),
                runtime_run_id = COALESCE(@runtimeRunId, runtime_run_id),
                pre_run_approval_request_id = COALESCE(@preRunApprovalRequestId, pre_run_approval_request_id),
                started_at = COALESCE(@startedAt, started_at),
                ended_at = COALESCE(@endedAt, ended_at),
                updated_at = @now
            WHERE id = @runId AND status = @expectedCurrentStatus
            """,
            new
            {
                runId,
                expectedCurrentStatus,
                nextStatus,
                error,
                outputJson,
                sessionId,
                runtimeRunId,
                preRunApprovalRequestId,
                startedAt,
                endedAt,
                now,
            }) > 0;
    }

    public bool UpdateRunMetadata(
        string runId,
        string? sessionId,
        string? runtimeRunId,
        string? preRunApprovalRequestId,
        string? outputJson,
        string? error,
        DateTimeOffset nowUtc)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE autopilot_runs
            SET session_id = COALESCE(@sessionId, session_id),
                runtime_run_id = COALESCE(@runtimeRunId, runtime_run_id),
                pre_run_approval_request_id = COALESCE(@preRunApprovalRequestId, pre_run_approval_request_id),
                output_json = COALESCE(@outputJson, output_json),
                error = COALESCE(@error, error),
                updated_at = @updatedAt
            WHERE id = @runId
            """,
            new
            {
                runId,
                sessionId,
                runtimeRunId,
                preRunApprovalRequestId,
                outputJson,
                error,
                updatedAt = nowUtc.ToString("O"),
            }) > 0;
    }

    public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            "UPDATE autopilot_runs SET skip_reason = @reason, updated_at = @updatedAt WHERE id = @runId",
            new { runId, reason, updatedAt = nowUtc.ToString("O") }) > 0;
    }

    public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset)
    {
        int boundedLimit = Math.Clamp(limit, 1, 250);
        int boundedOffset = Math.Max(0, offset);

        using var connection = OpenConnection();
        if (!string.IsNullOrWhiteSpace(scheduleId))
        {
            return connection.Query<GatewayScheduledRun>(
                selectRunsSql + " WHERE owner_user_id = @ownerUserId AND schedule_id = @scheduleId ORDER BY created_at DESC LIMIT @limit OFFSET @offset",
                new
                {
                    ownerUserId,
                    scheduleId,
                    limit = boundedLimit,
                    offset = boundedOffset,
                }).AsList();
        }

        return connection.Query<GatewayScheduledRun>(
            selectRunsSql + " WHERE owner_user_id = @ownerUserId ORDER BY created_at DESC LIMIT @limit OFFSET @offset",
            new
            {
                ownerUserId,
                limit = boundedLimit,
                offset = boundedOffset,
            }).AsList();
    }

    public void RecordSuppression(GatewayAutopilotSuppression suppression)
    {
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO autopilot_suppressions
                (id, schedule_id, suppressed_at, reason, existing_run_id, note)
            VALUES
                (@id, @scheduleId, @suppressedAt, @reason, @existingRunId, @note)
            """,
            new
            {
                id = suppression.Id,
                scheduleId = suppression.ScheduleId,
                suppressedAt = suppression.SuppressedAt,
                reason = suppression.Reason,
                existingRunId = suppression.ExistingRunId,
                note = suppression.Note,
            });
    }

    public void RecordNotification(GatewayAutopilotNotification notification)
    {
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO autopilot_notifications
                (id, scheduled_run_id, owner_user_id, channel_type, destination, status,
                 payload_json, created_at, sent_at, error, is_read, read_at)
            VALUES
                (@id, @scheduledRunId, @ownerUserId, @channelType, @destination, @status,
                 @payloadJson, @createdAt, @sentAt, @error, @isRead, @readAt)
            """,
            new
            {
                id = notification.Id,
                scheduledRunId = notification.ScheduledRunId,
                ownerUserId = notification.OwnerUserId,
                channelType = notification.ChannelType,
                destination = notification.Destination,
                status = notification.Status,
                payloadJson = notification.PayloadJson,
                createdAt = notification.CreatedAt,
                sentAt = notification.SentAt,
                error = notification.Error,
                isRead = notification.IsRead,
                readAt = notification.ReadAt,
            });
    }

    public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly)
    {
        int boundedLimit = Math.Clamp(limit, 1, 250);
        int boundedOffset = Math.Max(0, offset);
        using var connection = OpenConnection();

        if (unreadOnly)
        {
            return connection.Query<GatewayAutopilotNotification>(
                selectNotificationsSql + " WHERE owner_user_id = @ownerUserId AND is_read = 0 ORDER BY created_at DESC LIMIT @boundedLimit OFFSET @boundedOffset",
                new { ownerUserId, boundedLimit, boundedOffset }).AsList();
        }

        return connection.Query<GatewayAutopilotNotification>(
            selectNotificationsSql + " WHERE owner_user_id = @ownerUserId ORDER BY created_at DESC LIMIT @boundedLimit OFFSET @boundedOffset",
            new { ownerUserId, boundedLimit, boundedOffset }).AsList();
    }

    public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE autopilot_notifications
            SET is_read = 1,
                read_at = @readAt
            WHERE id = @notificationId AND owner_user_id = @ownerUserId
            """,
            new
            {
                ownerUserId,
                notificationId,
                readAt = nowUtc.ToString("O"),
            }) > 0;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection;
    }

    private const string selectSchedulesSql =
        """
        SELECT id,
               owner_user_id AS OwnerUserId,
               target_type AS TargetType,
               target_id AS TargetId,
               cron_expression AS CronExpression,
               timezone_id AS TimeZoneId,
               mode AS Mode,
               requires_pre_run_approval AS RequiresPreRunApproval,
               schedule_prompt_context AS SchedulePromptContext,
               workspace_file_paths AS WorkspaceFilePathsJson,
               notify_ui_inbox AS NotifyUiInbox,
               notify_channels AS NotifyChannels,
               pre_run_approval_expiry_policy AS PreRunApprovalExpiryPolicy,
               pre_run_approval_timeout_seconds AS PreRunApprovalTimeoutSeconds,
               overlap_policy AS OverlapPolicy,
               is_paused AS IsPaused,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt,
               next_due_at AS NextDueAt,
               last_evaluated_at AS LastEvaluatedAt,
               lease_owner AS LeaseOwner,
               lease_expires_at AS LeaseExpiresAt
        FROM autopilot_schedules
        """;

    private const string selectRunsSql =
        """
        SELECT id,
               schedule_id AS ScheduleId,
               owner_user_id AS OwnerUserId,
               target_type AS TargetType,
               target_id AS TargetId,
               status AS Status,
               trigger_type AS TriggerType,
               scheduled_for AS ScheduledFor,
               session_id AS SessionId,
               runtime_run_id AS RuntimeRunId,
               pre_run_approval_request_id AS PreRunApprovalRequestId,
               input_json AS InputJson,
               output_json AS OutputJson,
               error AS Error,
               CASE
                   WHEN status IN ('awaiting_pre_run_approval', 'waiting_for_in_run_approval') THEN 'pending_approval'
                   WHEN status IN ('scheduled', 'running') THEN 'executing'
                   WHEN status IN ('completed', 'no_op', 'failed', 'rejected', 'timed_out', 'skipped', 'stale') THEN 'terminal'
                   ELSE 'unknown'
               END AS StateCategory,
               skip_reason AS SkipReason,
               blocking_run_id AS BlockingRunId,
               blocking_run_status_snapshot AS BlockingRunStatusSnapshot,
               blocking_run_state_category AS BlockingRunStateCategory,
               blocked_at AS BlockedAt,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt,
               started_at AS StartedAt,
               ended_at AS EndedAt
        FROM autopilot_runs
        """;

    private const string selectNotificationsSql =
        """
        SELECT id,
               scheduled_run_id AS ScheduledRunId,
               owner_user_id AS OwnerUserId,
               channel_type AS ChannelType,
               destination AS Destination,
               status AS Status,
               payload_json AS PayloadJson,
               created_at AS CreatedAt,
               sent_at AS SentAt,
               error AS Error,
               is_read AS IsRead,
               read_at AS ReadAt
        FROM autopilot_notifications
        """;

    private const string nonTerminalStatusSql =
        "status <> @completed AND status <> @noOp AND status <> @failed AND status <> @rejected AND status <> @timedOut AND status <> @skipped AND status <> @stale";

    private static GatewayAutopilotSchedule MapSchedule(AutopilotScheduleRow row)
        => new(
            row.Id,
            row.OwnerUserId,
            row.TargetType,
            row.TargetId,
            row.CronExpression,
            row.TimeZoneId,
            row.Mode,
            row.RequiresPreRunApproval != 0,
            row.SchedulePromptContext,
            DeserializeWorkspaceFilePaths(row.WorkspaceFilePathsJson),
            row.NotifyUiInbox != 0,
            row.NotifyChannels != 0,
            row.PreRunApprovalExpiryPolicy,
            row.PreRunApprovalTimeoutSeconds,
            row.OverlapPolicy,
            row.IsPaused != 0,
            row.CreatedAt,
            row.UpdatedAt,
            row.NextDueAt,
            row.LastEvaluatedAt,
            row.LeaseOwner,
            row.LeaseExpiresAt);

    private static string SerializeWorkspaceFilePaths(string[] workspaceFilePaths)
        => JsonSerializer.Serialize(workspaceFilePaths, KautukaJsonContext.Default.StringArray);

    private static string[] DeserializeWorkspaceFilePaths(string? workspaceFilePathsJson)
        => string.IsNullOrWhiteSpace(workspaceFilePathsJson)
            ? []
            : JsonSerializer.Deserialize(workspaceFilePathsJson, KautukaJsonContext.Default.StringArray) ?? [];

}

internal sealed class AutopilotScheduleRow
{
    public string Id { get; init; } = string.Empty;
    public string OwnerUserId { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string CronExpression { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public long RequiresPreRunApproval { get; init; }
    public string SchedulePromptContext { get; init; } = string.Empty;
    public string WorkspaceFilePathsJson { get; init; } = "[]";
    public long NotifyUiInbox { get; init; }
    public long NotifyChannels { get; init; }
    public string PreRunApprovalExpiryPolicy { get; init; } = AutopilotPreRunApprovalExpiryPolicies.NextOccurrence;
    public int? PreRunApprovalTimeoutSeconds { get; init; }
    public string OverlapPolicy { get; init; } = AutopilotOverlapPolicies.SkipAndNotify;
    public long IsPaused { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string? NextDueAt { get; init; }
    public string? LastEvaluatedAt { get; init; }
    public string? LeaseOwner { get; init; }
    public string? LeaseExpiresAt { get; init; }
}
