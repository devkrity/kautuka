using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed class ApprovalContinuationQueue(KautukaDatabase database, TimeProvider timeProvider) : IApprovalContinuationQueue
{
    private static readonly TimeSpan leaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan idlePollInterval = TimeSpan.FromMilliseconds(250);
    private const int maxAttempts = 8;

    public async ValueTask<ApprovalContinuationEnqueueResult> EnqueueAsync(ApprovalContinuationWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string now = timeProvider.GetUtcNow().ToString("O");
        using var connection = OpenConnection();
        using var existingCommand = connection.CreateCommand();
        existingCommand.CommandText =
            """
            SELECT status, lease_owner
            FROM approval_continuation_jobs
            WHERE request_id = $RequestId
            LIMIT 1;
            """;
        existingCommand.Parameters.AddWithValue("$RequestId", work.RequestId);
        using SqliteDataReader existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        bool existed = false;
        string? priorStatus = null;
        try
        {
            if (await existingReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existed = true;
                priorStatus = existingReader.GetString(0);
                string? leaseOwner = await existingReader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                    ? null
                    : existingReader.GetString(1);
                if (string.Equals(priorStatus, "leased", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(leaseOwner))
                {
                    return ApprovalContinuationEnqueueResult.AlreadyLeased;
                }

                if (string.Equals(priorStatus, "pending", StringComparison.Ordinal))
                {
                    return ApprovalContinuationEnqueueResult.AlreadyPending;
                }
            }
        }
        finally
        {
            await existingReader.DisposeAsync().ConfigureAwait(false);
        }

        using var command = connection.CreateCommand();
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
            ON CONFLICT(request_id) DO UPDATE SET
                status = CASE WHEN approval_continuation_jobs.status = 'leased' THEN approval_continuation_jobs.status ELSE 'pending' END,
                available_at = CASE WHEN approval_continuation_jobs.status = 'leased' THEN approval_continuation_jobs.available_at ELSE $now END,
                approved = excluded.approved,
                resolution_source = excluded.resolution_source,
                reason = excluded.reason,
                decided_by_user_id = excluded.decided_by_user_id,
                stop_run = excluded.stop_run,
                updated_at = $now;
            """;
        command.Parameters.AddWithValue("$RequestId", work.RequestId);
        command.Parameters.AddWithValue("$SessionId", work.SessionId);
        command.Parameters.AddWithValue("$TargetType", work.TargetType);
        command.Parameters.AddWithValue("$TargetId", work.TargetId);
        command.Parameters.AddWithValue("$UserId", (object?)work.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$Approved", work.Approved ? 1 : 0);
        command.Parameters.AddWithValue("$ResolutionSource", (int)work.Source);
        command.Parameters.AddWithValue("$Reason", (object?)work.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$DecidedByUserId", (object?)work.DecidedByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$StopRun", work.StopRun ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var statusCommand = connection.CreateCommand();
        statusCommand.CommandText = "SELECT status, lease_owner FROM approval_continuation_jobs WHERE request_id = $RequestId LIMIT 1;";
        statusCommand.Parameters.AddWithValue("$RequestId", work.RequestId);
        SqliteDataReader reader = await statusCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ApprovalContinuationEnqueueResult.Inserted;
            }

            string finalStatus = reader.GetString(0);
            string? finalLeaseOwner = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null
                : reader.GetString(1);
            if (string.Equals(finalStatus, "leased", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(finalLeaseOwner))
            {
                return ApprovalContinuationEnqueueResult.AlreadyLeased;
            }

            if (existed && priorStatus is "completed" or "dead_letter")
            {
                return ApprovalContinuationEnqueueResult.Rescheduled;
            }

            return changed == 1
                ? ApprovalContinuationEnqueueResult.Inserted
                : ApprovalContinuationEnqueueResult.Rescheduled;
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ApprovalContinuationLease> LeaseAllAsync(
        string leaseOwner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        using var timer = new PeriodicTimer(idlePollInterval, timeProvider);
        while (!cancellationToken.IsCancellationRequested)
        {
            ApprovalContinuationLease? lease = TryClaim(leaseOwner);
            if (lease is not null)
            {
                yield return lease;
                continue;
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    public async ValueTask CompleteAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE approval_continuation_jobs
            SET status = 'completed', lease_owner = NULL, lease_expires_at = NULL, updated_at = $now
            WHERE request_id = $requestId AND status = 'leased' AND lease_owner = $leaseOwner
            """;
        command.Parameters.AddWithValue("$requestId", lease.Work.RequestId);
        command.Parameters.AddWithValue("$leaseOwner", lease.LeaseOwner);
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O"));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Continuation job '{lease.Work.RequestId}' lease is no longer owned by '{lease.LeaseOwner}'.");
        }

    }

    public async ValueTask RenewAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE approval_continuation_jobs
            SET lease_expires_at = $leaseExpiresAt, updated_at = $now
            WHERE request_id = $requestId AND status = 'leased' AND lease_owner = $leaseOwner
            """;
        command.Parameters.AddWithValue("$requestId", lease.Work.RequestId);
        command.Parameters.AddWithValue("$leaseOwner", lease.LeaseOwner);
        command.Parameters.AddWithValue("$leaseExpiresAt", timeProvider.GetUtcNow().Add(leaseDuration).ToString("O"));
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O"));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Continuation job '{lease.Work.RequestId}' lease was lost.");
        }
    }

    public async ValueTask<ApprovalContinuationFailureDisposition> FailAsync(
        ApprovalContinuationLease lease,
        string safeError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool deadLetter = lease.AttemptCount >= maxAttempts;
        DateTimeOffset nextAttempt = now.Add(ComputeBackoff(lease.AttemptCount));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE approval_continuation_jobs
            SET status = $status,
                available_at = $availableAt,
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = $safeError,
                updated_at = $now
            WHERE request_id = $requestId AND status = 'leased' AND lease_owner = $leaseOwner
            """;
        command.Parameters.AddWithValue("$status", deadLetter ? "dead_letter" : "pending");
        command.Parameters.AddWithValue("$availableAt", nextAttempt.ToString("O"));
        command.Parameters.AddWithValue("$safeError", SanitizeError(safeError));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$requestId", lease.Work.RequestId);
        command.Parameters.AddWithValue("$leaseOwner", lease.LeaseOwner);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException($"Continuation job '{lease.Work.RequestId}' lease is no longer owned by '{lease.LeaseOwner}'.");
        }

        return deadLetter
            ? ApprovalContinuationFailureDisposition.DeadLettered
            : ApprovalContinuationFailureDisposition.RetryScheduled;
    }

    private ApprovalContinuationLease? TryClaim(string leaseOwner)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        string nowText = now.ToString("O");
        string leaseExpiresAt = now.Add(leaseDuration).ToString("O");
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT request_id AS RequestId,
                   session_id AS SessionId,
                   target_type AS TargetType,
                   target_id AS TargetId,
                   user_id AS UserId,
                   approved AS Approved,
                   resolution_source AS ResolutionSource,
                   reason AS Reason,
                   decided_by_user_id AS DecidedByUserId,
                   stop_run AS StopRun,
                   attempt_count AS AttemptCount
            FROM approval_continuation_jobs
            WHERE ((status = 'pending' AND available_at <= $now)
               OR (status = 'leased' AND lease_expires_at <= $now))
              AND NOT EXISTS (
                SELECT 1
                FROM approval_resolution_events resolution
                WHERE resolution.request_id = approval_continuation_jobs.request_id
                  AND resolution.published_at IS NULL
              )
            ORDER BY available_at, created_at
            LIMIT 1
            """;
        select.Parameters.AddWithValue("$now", nowText);
        using SqliteDataReader reader = select.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return null;
        }

        var row = new ApprovalContinuationJobRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadNullableString(reader, 4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            reader.GetInt64(9),
            reader.GetInt64(10));
        reader.Close();

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE approval_continuation_jobs
            SET status = 'leased',
                attempt_count = attempt_count + 1,
                lease_owner = $leaseOwner,
                lease_expires_at = $leaseExpiresAt,
                updated_at = $now
            WHERE request_id = $requestId
              AND ((status = 'pending' AND available_at <= $now)
                OR (status = 'leased' AND lease_expires_at <= $now))
            """;
        update.Parameters.AddWithValue("$leaseOwner", leaseOwner);
        update.Parameters.AddWithValue("$leaseExpiresAt", leaseExpiresAt);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$requestId", row.RequestId);
        int changed = update.ExecuteNonQuery();
        if (changed != 1)
        {
            transaction.Rollback();
            return null;
        }

        transaction.Commit();
        return new ApprovalContinuationLease(
            new ApprovalContinuationWork(
                row.RequestId,
                row.SessionId,
                row.TargetType,
                row.TargetId,
                row.UserId,
                row.Approved != 0,
                (HitlResolutionSource)checked((int)row.ResolutionSource),
                row.Reason,
                row.DecidedByUserId,
                row.StopRun != 0),
            leaseOwner,
            checked((int)row.AttemptCount + 1));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static TimeSpan ComputeBackoff(int attemptCount)
        => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Clamp(attemptCount, 1, 6))));

    private static string SanitizeError(string error)
    {
        string normalized = string.IsNullOrWhiteSpace(error) ? "continuation_failed" : error.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    private sealed record ApprovalContinuationJobRow(
        string RequestId,
        string SessionId,
        string TargetType,
        string TargetId,
        string? UserId,
        long Approved,
        long ResolutionSource,
        string? Reason,
        string? DecidedByUserId,
        long StopRun,
        long AttemptCount);
}
