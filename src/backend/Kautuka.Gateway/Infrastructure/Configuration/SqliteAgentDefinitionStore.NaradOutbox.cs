using System.Globalization;

using Microsoft.Data.Sqlite;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed partial class SqliteAgentDefinitionStore
{
    private const int runtimeEventOutboxMaxAttempts = 8;

    public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(
        string rootStorageLocatorKey,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string databasePath = GetDatabasePath(rootStorageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(rootStorageLocatorKey);
        using (SqliteCommand markRootMirrors = connection.CreateCommand())
        {
            markRootMirrors.CommandText = """
                UPDATE runtime_event_outbox
                SET mirrored_at = COALESCE(mirrored_at, available_at)
                WHERE source_storage_locator_key = $rootStorageLocatorKey
                  AND mirrored_at IS NULL;
                """;
            markRootMirrors.Parameters.AddWithValue("$rootStorageLocatorKey", rootStorageLocatorKey);
            markRootMirrors.ExecuteNonQuery();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.event_id, o.source_storage_locator_key, o.published_at, o.mirrored_at,
                   o.attempt_count, o.last_error, COALESCE(o.lease_owner, ''), COALESCE(o.lease_expires_at, ''),
                   j.protocol_version, j.event_type, j.session_id, j.session_sequence,
                   j.root_run_id, j.run_id, j.sequence, j.workflow_id, j.workflow_sequence,
                   j.record_class, j.event_json, j.occurred_at
            FROM runtime_event_outbox o
            JOIN narad_runtime_journal j ON j.event_id = o.event_id
            WHERE (o.published_at IS NULL OR o.mirrored_at IS NULL)
              AND o.dead_lettered_at IS NULL
              AND o.dismissed_at IS NULL
              AND o.available_at <= $availableAt
            ORDER BY j.session_sequence ASC, COALESCE(j.sequence, j.session_sequence) ASC, j.event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$availableAt", DateTimeOffset.UtcNow.ToString("O"));
        return ReadNaradOutboxEntries(command);
    }

    public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(
        string rootStorageLocatorKey,
        string leaseOwner,
        TimeSpan leaseDuration,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string databasePath = GetDatabasePath(rootStorageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string nowValue = now.ToString("O");
        string leaseExpiresAt = now.Add(leaseDuration).ToString("O");
        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        string? streamKey;
        using (SqliteCommand streamCommand = connection.CreateCommand())
        {
            streamCommand.Transaction = transaction;
            streamCommand.CommandText = """
                WITH pending AS (
                    SELECT o.event_id, o.available_at, o.lease_expires_at,
                           COALESCE(j.root_run_id, j.session_id) AS stream_key,
                           COALESCE(j.sequence, j.session_sequence) AS stream_sequence,
                           j.session_sequence
                    FROM runtime_event_outbox o
                    JOIN narad_runtime_journal j ON j.event_id = o.event_id
                    WHERE (o.published_at IS NULL OR o.mirrored_at IS NULL)
                      AND o.dead_lettered_at IS NULL
                      AND o.dismissed_at IS NULL
                )
                SELECT candidate.stream_key
                FROM pending candidate
                WHERE candidate.stream_sequence = (
                    SELECT MIN(predecessor.stream_sequence)
                    FROM pending predecessor
                    WHERE predecessor.stream_key = candidate.stream_key)
                  AND candidate.session_sequence = (
                    SELECT MIN(predecessor.session_sequence)
                    FROM pending predecessor
                    WHERE predecessor.stream_key = candidate.stream_key
                      AND predecessor.stream_sequence = candidate.stream_sequence)
                  AND candidate.available_at <= $now
                  AND (candidate.lease_expires_at IS NULL OR candidate.lease_expires_at <= $now)
                ORDER BY candidate.stream_sequence, candidate.session_sequence, candidate.event_id
                LIMIT 1;
                """;
            streamCommand.Parameters.AddWithValue("$now", nowValue);
            streamKey = Convert.ToString(streamCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(streamKey))
        {
            transaction.Commit();
            return [];
        }

        var candidates = new List<NaradRuntimeEventOutboxEntry>(limit);
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT o.event_id, o.source_storage_locator_key, o.published_at, o.mirrored_at,
                       o.attempt_count, o.last_error, o.available_at, o.lease_expires_at,
                       j.protocol_version, j.event_type, j.session_id, j.session_sequence,
                       j.root_run_id, j.run_id, j.sequence, j.workflow_id, j.workflow_sequence,
                       j.record_class, j.event_json, j.occurred_at
                FROM runtime_event_outbox o
                JOIN narad_runtime_journal j ON j.event_id = o.event_id
                WHERE COALESCE(j.root_run_id, j.session_id) = $streamKey
                  AND (o.published_at IS NULL OR o.mirrored_at IS NULL)
                  AND o.dead_lettered_at IS NULL
                  AND o.dismissed_at IS NULL
                ORDER BY COALESCE(j.sequence, j.session_sequence) ASC, j.session_sequence ASC, j.event_id ASC
                LIMIT $limit;
                """;
            read.Parameters.AddWithValue("$streamKey", streamKey);
            read.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                string availableAt = reader.GetString(6);
                string? existingLeaseExpiry = reader.IsDBNull(7) ? null : reader.GetString(7);
                if (string.CompareOrdinal(availableAt, nowValue) > 0
                    || (!string.IsNullOrWhiteSpace(existingLeaseExpiry)
                        && string.CompareOrdinal(existingLeaseExpiry, nowValue) > 0))
                {
                    break;
                }

                candidates.Add(ReadNaradOutboxEntry(reader, leaseOwner, leaseExpiresAt));
            }
        }

        foreach (NaradRuntimeEventOutboxEntry entry in candidates)
        {
            using SqliteCommand claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE runtime_event_outbox
                SET lease_owner = $leaseOwner,
                    lease_expires_at = $leaseExpiresAt
                WHERE event_id = $eventId
                  AND dead_lettered_at IS NULL
                  AND dismissed_at IS NULL
                  AND (lease_expires_at IS NULL OR lease_expires_at <= $now);
                """;
            claim.Parameters.AddWithValue("$eventId", entry.Event.EventId);
            claim.Parameters.AddWithValue("$leaseOwner", leaseOwner);
            claim.Parameters.AddWithValue("$leaseExpiresAt", leaseExpiresAt);
            claim.Parameters.AddWithValue("$now", nowValue);
            if (claim.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Narad outbox entry '{entry.Event.EventId}' could not be leased.");
            }
        }

        transaction.Commit();
        return candidates;
    }

    public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner)
        => MarkNaradEventOutboxState(rootStorageLocatorKey, eventId, leaseOwner, published: true);

    public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner)
        => MarkNaradEventOutboxState(rootStorageLocatorKey, eventId, leaseOwner, published: false);

    public void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeError);

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runtime_event_outbox
            SET attempt_count = attempt_count + 1,
                last_error = $error,
                available_at = $availableAt,
                dead_lettered_at = CASE WHEN attempt_count + 1 >= $maxAttempts THEN $failedAt ELSE NULL END,
                lease_owner = NULL,
                lease_expires_at = NULL
            WHERE event_id = $eventId
              AND lease_owner = $leaseOwner;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("$error", safeError[..Math.Min(safeError.Length, 256)]);
        command.Parameters.AddWithValue("$maxAttempts", runtimeEventOutboxMaxAttempts);
        command.Parameters.AddWithValue("$failedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$availableAt", DateTimeOffset.UtcNow.AddSeconds(
            Math.Min(Math.Pow(2, GetNaradEventOutboxAttemptCount(connection, eventId)), 300)).ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Narad outbox entry '{eventId}' is not leased by '{leaseOwner}'.");
        }
    }

    public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(
        string rootStorageLocatorKey,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string databasePath = GetDatabasePath(rootStorageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.event_id, o.source_storage_locator_key, o.attempt_count, o.last_error, o.dead_lettered_at,
                   j.protocol_version, j.event_type, j.session_id, j.session_sequence,
                   j.root_run_id, j.run_id, j.sequence, j.workflow_id, j.workflow_sequence,
                   j.record_class, j.event_json, j.occurred_at
            FROM runtime_event_outbox o
            JOIN narad_runtime_journal j ON j.event_id = o.event_id
            WHERE o.dead_lettered_at IS NOT NULL
              AND o.dismissed_at IS NULL
            ORDER BY o.dead_lettered_at, j.session_sequence ASC, COALESCE(j.sequence, j.session_sequence) ASC, j.event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<NaradRuntimeEventOutboxDeadLetter>();
        while (reader.Read())
        {
            entries.Add(new NaradRuntimeEventOutboxDeadLetter(
                ReadNaradPersistedEventFromOutboxJoin(reader, columnOffset: 5),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return entries;
    }

    public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId)
        => TransitionNaradEventOutboxDeadLetter(rootStorageLocatorKey, eventId, dismiss: false);

    public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId)
        => TransitionNaradEventOutboxDeadLetter(rootStorageLocatorKey, eventId, dismiss: true);

    public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys()
        => ListNaradEventOutboxStorageLocatorKeys(excludedStorageLocatorKeys: new HashSet<string>(StringComparer.Ordinal));

    public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys(IReadOnlySet<string> excludedStorageLocatorKeys)
    {
        ArgumentNullException.ThrowIfNull(excludedStorageLocatorKeys);

        var pendingStorageLocatorKeys = new List<string>();
        foreach (string storageLocatorKey in ListRegisteredStorageLocatorKeys())
        {
            if (excludedStorageLocatorKeys.Contains(storageLocatorKey))
            {
                continue;
            }

            if (!File.Exists(GetDatabasePath(storageLocatorKey)))
            {
                continue;
            }

            using var connection = OpenConnection(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM runtime_event_outbox
                WHERE (published_at IS NULL OR mirrored_at IS NULL)
                  AND dead_lettered_at IS NULL
                  AND dismissed_at IS NULL
                LIMIT 1;
                """;
            if (command.ExecuteScalar() is not null)
            {
                pendingStorageLocatorKeys.Add(storageLocatorKey);
            }
        }

        return pendingStorageLocatorKeys;
    }

    private void MarkNaradEventOutboxState(string rootStorageLocatorKey, string eventId, string leaseOwner, bool published)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        if (published)
        {
            command.CommandText = """
                UPDATE runtime_event_outbox
                SET published_at = COALESCE(published_at, $at),
                    last_error = NULL,
                    lease_owner = CASE WHEN mirrored_at IS NOT NULL THEN NULL ELSE lease_owner END,
                    lease_expires_at = CASE WHEN mirrored_at IS NOT NULL THEN NULL ELSE lease_expires_at END
                WHERE event_id = $eventId AND lease_owner = $leaseOwner;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE runtime_event_outbox
                SET mirrored_at = COALESCE(mirrored_at, $at),
                    last_error = NULL,
                    lease_owner = CASE WHEN published_at IS NOT NULL THEN NULL ELSE lease_owner END,
                    lease_expires_at = CASE WHEN published_at IS NOT NULL THEN NULL ELSE lease_expires_at END
                WHERE event_id = $eventId AND lease_owner = $leaseOwner;
                """;
        }

        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Narad outbox entry '{eventId}' is not leased by '{leaseOwner}'.");
        }
    }

    private static int GetNaradEventOutboxAttemptCount(SqliteConnection connection, string eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_count FROM runtime_event_outbox WHERE event_id = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId);
        object? value = command.ExecuteScalar();
        return value is null
            ? throw new InvalidDataException($"Narad outbox entry '{eventId}' does not exist.")
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private bool TransitionNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId, bool dismiss)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        if (dismiss)
        {
            command.CommandText = """
                UPDATE runtime_event_outbox
                SET dismissed_at = $now, lease_owner = NULL, lease_expires_at = NULL
                WHERE event_id = $eventId AND dead_lettered_at IS NOT NULL AND dismissed_at IS NULL;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE runtime_event_outbox
                SET attempt_count = 0, last_error = NULL, available_at = $now,
                    dead_lettered_at = NULL, lease_owner = NULL, lease_expires_at = NULL
                WHERE event_id = $eventId AND dead_lettered_at IS NOT NULL AND dismissed_at IS NULL;
                """;
        }

        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    private static List<NaradRuntimeEventOutboxEntry> ReadNaradOutboxEntries(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<NaradRuntimeEventOutboxEntry>();
        while (reader.Read())
        {
            entries.Add(ReadNaradOutboxEntry(reader));
        }

        return entries;
    }

    private static NaradRuntimeEventOutboxEntry ReadNaradOutboxEntry(
        SqliteDataReader reader,
        string? leaseOwnerOverride = null,
        string? leaseExpiresAtOverride = null)
        => new(
            ReadNaradPersistedEventFromOutboxJoin(reader, columnOffset: 8),
            reader.GetString(1),
            !reader.IsDBNull(2),
            !reader.IsDBNull(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            leaseOwnerOverride ?? reader.GetString(6),
            leaseExpiresAtOverride ?? reader.GetString(7));

    private static NaradPersistedEvent ReadNaradPersistedEventFromOutboxJoin(SqliteDataReader reader, int columnOffset)
    {
        long? sequence = reader.IsDBNull(columnOffset + 6) ? null : reader.GetInt64(columnOffset + 6);
        long? workflowSequence = reader.IsDBNull(columnOffset + 8) ? null : reader.GetInt64(columnOffset + 8);
        var persistedEvent = new NaradPersistedEvent(
            reader.GetString(0),
            reader.GetString(columnOffset),
            reader.GetString(columnOffset + 1),
            reader.GetString(columnOffset + 2),
            reader.GetInt64(columnOffset + 3),
            reader.GetString(columnOffset + 9),
            reader.GetString(columnOffset + 10),
            reader.GetString(columnOffset + 11),
            reader.IsDBNull(columnOffset + 4) ? null : reader.GetString(columnOffset + 4),
            reader.IsDBNull(columnOffset + 5) ? null : reader.GetString(columnOffset + 5),
            sequence,
            reader.IsDBNull(columnOffset + 7) ? null : reader.GetString(columnOffset + 7),
            workflowSequence);
        NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);
        return persistedEvent;
    }
}
