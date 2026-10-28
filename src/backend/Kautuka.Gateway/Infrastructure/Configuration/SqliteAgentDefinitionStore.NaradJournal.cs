using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed partial class SqliteAgentDefinitionStore
{
    private const string naradSessionSequenceScope = "session";
    private const string naradRootSequenceScope = "root";
    private const string naradWorkflowSequenceScope = "workflow";

    public NaradPersistedEvent AppendNaradAuthorityEvent(
        string rootStorageLocatorKey,
        string sourceStorageLocatorKey,
        NaradAuthorityEventDraft draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStorageLocatorKey);
        ArgumentNullException.ThrowIfNull(draft);
        NaradJournalValidation.ValidateAuthorityDraft(draft);

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();

        NaradPersistedEvent? existing = ReadNaradEventById(connection, transaction, draft.EventId);
        if (existing is not null)
        {
            ValidateNaradDraftMatchesPersisted(existing, draft);
            EnsureNaradEventOutbox(connection, transaction, existing, sourceStorageLocatorKey, rootStorageLocatorKey);
            transaction.Commit();
            return existing;
        }

        long sessionSequence = AllocateNaradSequence(connection, transaction, naradSessionSequenceScope, draft.SessionId);
        long? workflowSequence = string.IsNullOrWhiteSpace(draft.WorkflowId)
            ? null
            : AllocateNaradSequence(connection, transaction, naradWorkflowSequenceScope, draft.WorkflowId);
        long? rootSequence = string.IsNullOrWhiteSpace(draft.RootRunId) || string.IsNullOrWhiteSpace(draft.RunId)
            ? null
            : AllocateNaradSequence(connection, transaction, naradRootSequenceScope, draft.RootRunId!);

        string eventJson = BuildCanonicalNaradEventJson(draft, sessionSequence, rootSequence, workflowSequence);
        var persisted = new NaradPersistedEvent(
            draft.EventId,
            draft.ProtocolVersion,
            draft.EventType,
            draft.SessionId,
            sessionSequence,
            draft.RecordClass,
            eventJson,
            draft.OccurredAt,
            draft.RootRunId,
            draft.RunId,
            rootSequence,
            draft.WorkflowId,
            workflowSequence);

        InsertNaradJournalRow(connection, transaction, persisted);
        EnsureNaradEventOutbox(connection, transaction, persisted, sourceStorageLocatorKey, rootStorageLocatorKey);
        transaction.Commit();
        return persisted;
    }

    public NaradPersistedEvent AppendNaradParentSessionProjection(
        string rootStorageLocatorKey,
        NaradPersistedEvent sourceEvent,
        string parentSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootStorageLocatorKey);
        ArgumentNullException.ThrowIfNull(sourceEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        if (string.Equals(parentSessionId, sourceEvent.SessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Parent session projection requires a different parentSessionId.", nameof(parentSessionId));
        }

        NaradJournalValidation.ValidateAuthorityPersistedEvent(sourceEvent);
        string projectionEventId = BuildParentSessionProjectionEventId(sourceEvent.EventId);

        using var connection = OpenConnection(rootStorageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();

        NaradPersistedEvent? existing = ReadNaradEventById(connection, transaction, projectionEventId);
        if (existing is not null)
        {
            EnsureNaradEventOutbox(connection, transaction, existing, rootStorageLocatorKey, rootStorageLocatorKey);
            transaction.Commit();
            return existing;
        }

        long parentSessionSequence = AllocateNaradSequence(connection, transaction, naradSessionSequenceScope, parentSessionId);
        string projectionJson = BuildParentSessionProjectionEventJson(sourceEvent, projectionEventId, parentSessionId, parentSessionSequence);
        // Session-lane only: lineage stays in event_json (runId/rootRunId/parent*), journal
        // root/run/sequence columns stay null so this is not run-tree authority.
        var projection = new NaradPersistedEvent(
            projectionEventId,
            sourceEvent.ProtocolVersion,
            sourceEvent.EventType,
            parentSessionId,
            parentSessionSequence,
            sourceEvent.RecordClass,
            projectionJson,
            sourceEvent.OccurredAt,
            RootRunId: null,
            RunId: null,
            Sequence: null,
            WorkflowId: null,
            WorkflowSequence: null);

        NaradJournalValidation.ValidateAuthorityPersistedEvent(projection);
        InsertNaradJournalRow(connection, transaction, projection);
        // Source == root: conversation projection stays on the parent/root store only.
        EnsureNaradEventOutbox(connection, transaction, projection, rootStorageLocatorKey, rootStorageLocatorKey);
        transaction.Commit();
        return projection;
    }

    public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStorageLocatorKey);
        ArgumentNullException.ThrowIfNull(persistedEvent);
        NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);

        using var connection = OpenConnection(sourceStorageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();
        NaradPersistedEvent? existing = ReadNaradEventById(connection, transaction, persistedEvent.EventId);
        if (existing is not null)
        {
            ValidateNaradPersistedEnvelope(existing, persistedEvent);
            AdvanceNaradMirrorSequenceCounters(connection, transaction, persistedEvent);
            transaction.Commit();
            return;
        }

        InsertNaradJournalRow(connection, transaction, persistedEvent);
        AdvanceNaradMirrorSequenceCounters(connection, transaction, persistedEvent);
        transaction.Commit();
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(
        string storageLocatorKey,
        string sessionId,
        long afterSessionSequence,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (afterSessionSequence < 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(afterSessionSequence);
        }

        NaradJournalValidation.ValidateHistoryLimit(limit);
        return ReadNaradSessionHistoryWindow(storageLocatorKey, sessionId, afterSessionSequence, limit);
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return ReadNaradSessionHistoryWindow(storageLocatorKey, sessionId, afterSessionSequence: 0, limit: NaradJournalValidation.MaxHistoryLimit, unbounded: true);
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(
        string storageLocatorKey,
        string rootRunId,
        long afterRootSequence,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
        if (afterRootSequence < 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(afterRootSequence);
        }

        NaradJournalValidation.ValidateHistoryLimit(limit);
        return ReadNaradRootHistoryWindow(storageLocatorKey, rootRunId, afterRootSequence, limit);
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
        return ReadNaradRootHistoryWindow(storageLocatorKey, rootRunId, afterRootSequence: 0, limit: NaradJournalValidation.MaxHistoryLimit, unbounded: true);
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId)
        => ListNaradSessionHistory(storageLocatorKey, sessionId, afterSessionSequence: 0, limit: NaradJournalValidation.MaxHistoryLimit);

    public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId)
        => ListNaradRootHistory(storageLocatorKey, rootRunId, afterRootSequence: 0, limit: NaradJournalValidation.MaxHistoryLimit);

    public long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(session_sequence), 0)
            FROM narad_runtime_journal
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return false;
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_type
            FROM narad_runtime_journal
            WHERE root_run_id = $rootRunId
              AND run_id = $rootRunId
            ORDER BY sequence DESC, event_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$rootRunId", rootRunId);
        object? latest = command.ExecuteScalar();
        return latest is string eventType && NaradEventTypes.IsRootRunTerminalEventType(eventType);
    }

    public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_run_id
            FROM narad_runtime_journal
            WHERE session_id = $sessionId
              AND root_run_id IS NOT NULL
              AND run_id = root_run_id
            GROUP BY root_run_id
            ORDER BY MIN(session_sequence) ASC;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        using SqliteDataReader reader = command.ExecuteReader();
        var rootRunIds = new List<string>();
        while (reader.Read())
        {
            rootRunIds.Add(reader.GetString(0));
        }

        return rootRunIds;
    }

    public string? FindNaradRootStorageLocatorKey(string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
        foreach (string storageLocatorKey in ListRegisteredStorageLocatorKeys())
        {
            string databasePath = GetDatabasePath(storageLocatorKey);
            if (!File.Exists(databasePath))
            {
                continue;
            }

            using var connection = OpenConnection(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM narad_runtime_journal
                WHERE root_run_id = $rootRunId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$rootRunId", rootRunId);
            if (command.ExecuteScalar() is not null)
            {
                return storageLocatorKey;
            }
        }

        return null;
    }

    public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return null;
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_run_id
            FROM narad_runtime_journal
            WHERE run_id = $runId
              AND root_run_id IS NOT NULL
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        object? scalar = command.ExecuteScalar();
        return scalar is string rootRunId && !string.IsNullOrWhiteSpace(rootRunId)
            ? rootRunId
            : null;
    }

    public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startedBefore);
        var rootRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string storageLocatorKey in ListRegisteredStorageLocatorKeys())
        {
            string databasePath = GetDatabasePath(storageLocatorKey);
            if (!File.Exists(databasePath))
            {
                continue;
            }

            using var connection = OpenConnection(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT started.root_run_id
                FROM narad_runtime_journal started
                WHERE started.event_type = $runStarted
                  AND started.root_run_id IS NOT NULL
                  AND started.occurred_at < $startedBefore
                  AND NOT EXISTS (
                    SELECT 1
                    FROM narad_runtime_journal terminal
                    WHERE terminal.root_run_id = started.root_run_id
                      AND terminal.run_id = terminal.root_run_id
                      AND terminal.event_type IN ($runFinished, $runError, $runCancelled)
                  );
                """;
            command.Parameters.AddWithValue("$runStarted", NaradEventTypes.RunStarted);
            command.Parameters.AddWithValue("$startedBefore", startedBefore);
            command.Parameters.AddWithValue("$runFinished", NaradEventTypes.RunFinished);
            command.Parameters.AddWithValue("$runError", NaradEventTypes.RunError);
            command.Parameters.AddWithValue("$runCancelled", NaradEventTypes.RunCancelled);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rootRunIds.Add(reader.GetString(0));
            }
        }

        return [.. rootRunIds];
    }

    private List<NaradPersistedEvent> ReadNaradSessionHistoryWindow(
        string storageLocatorKey,
        string sessionId,
        long afterSessionSequence,
        int limit,
        bool unbounded = false)
    {
        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, protocol_version, event_type, session_id, session_sequence,
                   root_run_id, run_id, sequence, workflow_id, workflow_sequence,
                   record_class, event_json, occurred_at
            FROM narad_runtime_journal
            WHERE session_id = $sessionId
              AND session_sequence > $afterSessionSequence
            ORDER BY session_sequence ASC, event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$afterSessionSequence", afterSessionSequence);
        command.Parameters.AddWithValue("$limit", unbounded ? int.MaxValue : limit);

        var events = ReadNaradPersistedEvents(command);
        ValidateNaradSessionHistoryWindow(events, sessionId, afterSessionSequence, limit, unbounded);
        return events;
    }

    private List<NaradPersistedEvent> ReadNaradRootHistoryWindow(
        string storageLocatorKey,
        string rootRunId,
        long afterRootSequence,
        int limit,
        bool unbounded = false)
    {
        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, protocol_version, event_type, session_id, session_sequence,
                   root_run_id, run_id, sequence, workflow_id, workflow_sequence,
                   record_class, event_json, occurred_at
            FROM narad_runtime_journal
            WHERE root_run_id = $rootRunId
              AND sequence IS NOT NULL
              AND sequence > $afterRootSequence
              AND json_extract(event_json, '$.sourceEventId') IS NULL
            ORDER BY sequence ASC, event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$rootRunId", rootRunId);
        command.Parameters.AddWithValue("$afterRootSequence", afterRootSequence);
        command.Parameters.AddWithValue("$limit", unbounded ? int.MaxValue : limit);

        var events = ReadNaradPersistedEvents(command);
        ValidateNaradRootHistoryWindow(events, rootRunId, afterRootSequence, limit, unbounded);
        return events;
    }

    private static void ValidateNaradDraftMatchesPersisted(NaradPersistedEvent existing, NaradAuthorityEventDraft draft)
    {
        NaradJournalValidation.ValidateAuthorityPersistedEvent(existing);
        string expectedJson = BuildCanonicalNaradEventJson(
            draft with { OccurredAt = existing.OccurredAt },
            existing.SessionSequence,
            existing.Sequence,
            existing.WorkflowSequence);
        if (!string.Equals(existing.EventJson, expectedJson, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Narad event '{draft.EventId}' was replayed with different canonical data.");
        }
    }

    private static void ValidateNaradPersistedEnvelope(NaradPersistedEvent existing, NaradPersistedEvent expected)
    {
        NaradJournalValidation.ValidateAuthorityPersistedEvent(existing);
        NaradJournalValidation.ValidateAuthorityPersistedEvent(expected);
        if (!string.Equals(existing.EventJson, expected.EventJson, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Narad event '{expected.EventId}' was mirrored with a different envelope.");
        }
    }

    private static void ValidateNaradSessionHistoryWindow(
        List<NaradPersistedEvent> events,
        string sessionId,
        long afterSessionSequence,
        int limit,
        bool unbounded)
    {
        if (events.Count == 0)
        {
            return;
        }

        long expectedSessionSequence = afterSessionSequence + 1;
        var lastWorkflowSequence = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (NaradPersistedEvent persistedEvent in events)
        {
            NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);
            if (persistedEvent.SessionSequence != expectedSessionSequence)
            {
                throw new InvalidDataException(
                    $"Narad session history for '{sessionId}' has a gap or duplicate at sessionSequence {persistedEvent.SessionSequence}; expected {expectedSessionSequence}.");
            }

            expectedSessionSequence++;
            if (!string.IsNullOrWhiteSpace(persistedEvent.WorkflowId) && persistedEvent.WorkflowSequence.HasValue)
            {
                if (lastWorkflowSequence.TryGetValue(persistedEvent.WorkflowId, out long previousWorkflowSequence)
                    && persistedEvent.WorkflowSequence.Value != checked(previousWorkflowSequence + 1))
                {
                    throw new InvalidDataException(
                        $"Narad session history for workflow '{persistedEvent.WorkflowId}' has a gap or duplicate at workflowSequence {persistedEvent.WorkflowSequence.Value}; expected {checked(previousWorkflowSequence + 1)}.");
                }

                lastWorkflowSequence[persistedEvent.WorkflowId] = persistedEvent.WorkflowSequence.Value;
            }
        }

        if (!unbounded && events.Count > limit)
        {
            throw new InvalidDataException("Narad session history query returned more rows than the requested limit.");
        }
    }

    private static void ValidateNaradRootHistoryWindow(
        List<NaradPersistedEvent> events,
        string rootRunId,
        long afterRootSequence,
        int limit,
        bool unbounded)
    {
        if (events.Count == 0)
        {
            return;
        }

        long expected = afterRootSequence + 1;
        foreach (NaradPersistedEvent persistedEvent in events)
        {
            NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);
            if (!persistedEvent.Sequence.HasValue)
            {
                throw new InvalidDataException($"Narad root history event '{persistedEvent.EventId}' is missing root sequence.");
            }

            if (persistedEvent.Sequence.Value != expected)
            {
                throw new InvalidDataException(
                    $"Narad root history for '{rootRunId}' has a gap or duplicate at sequence {persistedEvent.Sequence.Value}; expected {expected}.");
            }

            expected++;
        }

        if (!unbounded && events.Count > limit)
        {
            throw new InvalidDataException("Narad root history query returned more rows than the requested limit.");
        }
    }

    private static long AllocateNaradSequence(SqliteConnection connection, SqliteTransaction transaction, string scope, string key)
    {
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO runtime_event_sequences (sequence_scope, sequence_key, next_sequence)
                VALUES ($scope, $key, 1)
                ON CONFLICT(sequence_scope, sequence_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$key", key);
            insert.ExecuteNonQuery();
        }

        long next;
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT next_sequence
                FROM runtime_event_sequences
                WHERE sequence_scope = $scope AND sequence_key = $key;
                """;
            select.Parameters.AddWithValue("$scope", scope);
            select.Parameters.AddWithValue("$key", key);
            next = Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE runtime_event_sequences
                SET next_sequence = $following
                WHERE sequence_scope = $scope AND sequence_key = $key;
                """;
            update.Parameters.AddWithValue("$following", checked(next + 1));
            update.Parameters.AddWithValue("$scope", scope);
            update.Parameters.AddWithValue("$key", key);
            update.ExecuteNonQuery();
        }

        return next;
    }

    private static void AdvanceNaradMirrorSequenceCounters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NaradPersistedEvent persistedEvent)
    {
        AdvanceNaradSequenceAtLeast(
            connection,
            transaction,
            naradSessionSequenceScope,
            persistedEvent.SessionId,
            checked(persistedEvent.SessionSequence + 1));

        if (!string.IsNullOrWhiteSpace(persistedEvent.WorkflowId) && persistedEvent.WorkflowSequence.HasValue)
        {
            AdvanceNaradSequenceAtLeast(
                connection,
                transaction,
                naradWorkflowSequenceScope,
                persistedEvent.WorkflowId,
                checked(persistedEvent.WorkflowSequence.Value + 1));
        }

        if (!string.IsNullOrWhiteSpace(persistedEvent.RootRunId)
            && persistedEvent.Sequence.HasValue)
        {
            AdvanceNaradSequenceAtLeast(
                connection,
                transaction,
                naradRootSequenceScope,
                persistedEvent.RootRunId,
                checked(persistedEvent.Sequence.Value + 1));
        }
    }

    private static void AdvanceNaradSequenceAtLeast(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scope,
        string key,
        long atLeastNextSequence)
    {
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO runtime_event_sequences (sequence_scope, sequence_key, next_sequence)
                VALUES ($scope, $key, $atLeast)
                ON CONFLICT(sequence_scope, sequence_key) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$atLeast", atLeastNextSequence);
            insert.ExecuteNonQuery();
        }

        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE runtime_event_sequences
            SET next_sequence = CASE
                WHEN next_sequence < $atLeast THEN $atLeast
                ELSE next_sequence
            END
            WHERE sequence_scope = $scope AND sequence_key = $key;
            """;
        update.Parameters.AddWithValue("$scope", scope);
        update.Parameters.AddWithValue("$key", key);
        update.Parameters.AddWithValue("$atLeast", atLeastNextSequence);
        update.ExecuteNonQuery();
    }

    private static string BuildParentSessionProjectionEventId(string sourceEventId)
        => $"aaatproj:{sourceEventId}";

    private static string BuildParentSessionProjectionEventJson(
        NaradPersistedEvent sourceEvent,
        string projectionEventId,
        string parentSessionId,
        long parentSessionSequence)
    {
        JsonNode? parsed = JsonNode.Parse(sourceEvent.EventJson)
            ?? throw new InvalidDataException($"Narad source event '{sourceEvent.EventId}' has invalid JSON.");
        if (parsed is not JsonObject envelope)
        {
            throw new InvalidDataException($"Narad source event '{sourceEvent.EventId}' is not a JSON object.");
        }

        envelope["eventId"] = projectionEventId;
        envelope["sessionId"] = parentSessionId;
        envelope["sessionSequence"] = parentSessionSequence;
        envelope["sourceSessionId"] = sourceEvent.SessionId;
        envelope["sourceEventId"] = sourceEvent.EventId;
        // Parent conversation row is already on the parent session.
        envelope.Remove("parentSessionId");
        // Not run-tree authority on the parent attach: drop ordering lanes, keep lineage ids.
        envelope.Remove("sequence");
        envelope.Remove("workflowId");
        envelope.Remove("workflowSequence");

        return NaradEventJsonGuard.ValidatePersistedEventJson(envelope.ToJsonString(), "narad_runtime_journal.event_json");
    }

    private static string BuildCanonicalNaradEventJson(
        NaradAuthorityEventDraft draft,
        long sessionSequence,
        long? rootSequence,
        long? workflowSequence)
    {
        var envelope = new JsonObject
        {
            ["type"] = draft.EventType,
            ["protocolVersion"] = draft.ProtocolVersion,
            ["eventId"] = draft.EventId,
            ["timestamp"] = draft.OccurredAt,
            ["sessionId"] = draft.SessionId,
            ["sessionSequence"] = sessionSequence,
        };

        if (!string.IsNullOrWhiteSpace(draft.WorkflowId) && workflowSequence.HasValue)
        {
            envelope["workflowId"] = draft.WorkflowId;
            envelope["workflowSequence"] = workflowSequence.Value;
        }

        if (!string.IsNullOrWhiteSpace(draft.RootRunId)
            && !string.IsNullOrWhiteSpace(draft.RunId)
            && rootSequence.HasValue)
        {
            envelope["rootRunId"] = draft.RootRunId;
            envelope["runId"] = draft.RunId;
            envelope["sequence"] = rootSequence.Value;
        }

        if (!string.IsNullOrWhiteSpace(draft.ParentSessionId))
        {
            envelope["parentSessionId"] = draft.ParentSessionId;
        }

        if (!string.IsNullOrWhiteSpace(draft.ParentRunId))
        {
            envelope["parentRunId"] = draft.ParentRunId;
        }

        if (!string.IsNullOrWhiteSpace(draft.ParentToolCallId))
        {
            envelope["parentToolCallId"] = draft.ParentToolCallId;
        }

        if (draft.Body.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in draft.Body.EnumerateObject())
            {
                if (!envelope.ContainsKey(property.Name))
                {
                    envelope[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        return NaradEventJsonGuard.ValidatePersistedEventJson(envelope.ToJsonString(), "narad_runtime_journal.event_json");
    }

    private static void InsertNaradJournalRow(SqliteConnection connection, SqliteTransaction transaction, NaradPersistedEvent persistedEvent)
    {
        NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO narad_runtime_journal
                (event_id, protocol_version, event_type, session_id, session_sequence,
                 root_run_id, run_id, sequence, workflow_id, workflow_sequence,
                 record_class, event_json, occurred_at)
            VALUES
                ($eventId, $protocolVersion, $eventType, $sessionId, $sessionSequence,
                 $rootRunId, $runId, $sequence, $workflowId, $workflowSequence,
                 $recordClass, $eventJson, $occurredAt);
            """;
        command.Parameters.AddWithValue("$eventId", persistedEvent.EventId);
        command.Parameters.AddWithValue("$protocolVersion", persistedEvent.ProtocolVersion);
        command.Parameters.AddWithValue("$eventType", persistedEvent.EventType);
        command.Parameters.AddWithValue("$sessionId", persistedEvent.SessionId);
        command.Parameters.AddWithValue("$sessionSequence", persistedEvent.SessionSequence);
        command.Parameters.AddWithValue("$rootRunId", (object?)persistedEvent.RootRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", (object?)persistedEvent.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sequence", persistedEvent.Sequence.HasValue ? persistedEvent.Sequence.Value : DBNull.Value);
        command.Parameters.AddWithValue("$workflowId", (object?)persistedEvent.WorkflowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowSequence", persistedEvent.WorkflowSequence.HasValue ? persistedEvent.WorkflowSequence.Value : DBNull.Value);
        command.Parameters.AddWithValue("$recordClass", persistedEvent.RecordClass);
        command.Parameters.AddWithValue("$eventJson", persistedEvent.EventJson);
        command.Parameters.AddWithValue("$occurredAt", persistedEvent.OccurredAt);
        command.ExecuteNonQuery();
    }

    private static void EnsureNaradEventOutbox(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NaradPersistedEvent persistedEvent,
        string sourceStorageLocatorKey,
        string rootStorageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStorageLocatorKey);
        string? mirroredAt = string.Equals(sourceStorageLocatorKey, rootStorageLocatorKey, StringComparison.Ordinal)
            ? persistedEvent.OccurredAt
            : null;

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runtime_event_outbox
                (event_id, source_storage_locator_key, published_at, mirrored_at, available_at)
            VALUES
                ($eventId, $sourceStorageLocatorKey, NULL, $mirroredAt, $availableAt)
            ON CONFLICT(event_id) DO UPDATE SET
                mirrored_at = COALESCE(runtime_event_outbox.mirrored_at, excluded.mirrored_at);
            """;
        command.Parameters.AddWithValue("$eventId", persistedEvent.EventId);
        command.Parameters.AddWithValue("$sourceStorageLocatorKey", sourceStorageLocatorKey);
        command.Parameters.AddWithValue("$mirroredAt", (object?)mirroredAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$availableAt", persistedEvent.OccurredAt);
        command.ExecuteNonQuery();

        using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT source_storage_locator_key FROM runtime_event_outbox WHERE event_id = $eventId;";
        read.Parameters.AddWithValue("$eventId", persistedEvent.EventId);
        string persistedSource = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException($"Narad outbox entry '{persistedEvent.EventId}' has no source locator.");
        if (!string.Equals(persistedSource, sourceStorageLocatorKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Narad outbox entry '{persistedEvent.EventId}' was replayed with a different source locator.");
        }
    }

    private static NaradPersistedEvent? ReadNaradEventById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, protocol_version, event_type, session_id, session_sequence,
                   root_run_id, run_id, sequence, workflow_id, workflow_sequence,
                   record_class, event_json, occurred_at
            FROM narad_runtime_journal
            WHERE event_id = $eventId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadNaradPersistedEvent(reader) : null;
    }

    private static List<NaradPersistedEvent> ReadNaradPersistedEvents(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var events = new List<NaradPersistedEvent>();
        while (reader.Read())
        {
            events.Add(ReadNaradPersistedEvent(reader));
        }

        return events;
    }

    private static NaradPersistedEvent ReadNaradPersistedEvent(SqliteDataReader reader)
    {
        long? sequence = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        long? workflowSequence = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        var persistedEvent = new NaradPersistedEvent(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            sequence,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            workflowSequence);
        NaradJournalValidation.ValidateAuthorityPersistedEvent(persistedEvent);
        return persistedEvent;
    }
}
