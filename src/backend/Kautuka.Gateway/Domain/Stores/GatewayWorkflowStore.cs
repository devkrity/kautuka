using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using Kautuka.Gateway.Domain.Workflows;

using Microsoft.Data.Sqlite;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayWorkflowStore(KautukaDatabase database) : IGatewayWorkflowStore
{
    public GatewayWorkflowDefinition CreateDefinition(GatewayWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var connection = OpenConnection();
        connection.Execute(
            """
            INSERT INTO workflow_definitions
                (id, owner_user_id, name, governed, revision, definition_json, created_at, updated_at)
            VALUES
                (@id, @ownerUserId, @name, @governed, @revision, @definitionJson, @createdAt, @updatedAt)
            """,
            new
            {
                id = definition.Id,
                ownerUserId = definition.OwnerUserId,
                name = definition.Name,
                governed = definition.Governed ? 1 : 0,
                revision = definition.Revision,
                definitionJson = SerializeGraph(definition),
                createdAt = definition.CreatedAt,
                updatedAt = definition.UpdatedAt,
            });
        return definition;
    }

    public bool UpdateDefinition(GatewayWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE workflow_definitions
            SET name = @name,
                governed = @governed,
                revision = @revision,
                definition_json = @definitionJson,
                updated_at = @updatedAt
            WHERE id = @id AND owner_user_id = @ownerUserId
            """,
            new
            {
                id = definition.Id,
                ownerUserId = definition.OwnerUserId,
                name = definition.Name,
                governed = definition.Governed ? 1 : 0,
                revision = definition.Revision,
                definitionJson = SerializeGraph(definition),
                updatedAt = definition.UpdatedAt,
            }) > 0;
    }

    public GatewayWorkflowDefinition? GetDefinition(string workflowId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<WorkflowDefinitionRow>(
            selectDefinitionsSql + " WHERE d.id = @id",
            new { id = workflowId }) is { } row
            ? MapDefinition(row)
            : null;
    }

    public IReadOnlyList<GatewayWorkflowDefinition> ListDefinitions(string ownerUserId, bool archived = false)
    {
        using var connection = OpenConnection();
        return [.. connection.Query<WorkflowDefinitionRow>(
            selectDefinitionsSql + " WHERE d.owner_user_id = @ownerUserId AND COALESCE(l.archived, 0) = @archived ORDER BY d.updated_at DESC",
            new { ownerUserId, archived = archived ? 1 : 0 }).Select(MapDefinition)];
    }

    public bool SetArchived(string workflowId, string ownerUserId, bool archived)
    {
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (archived && connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM workflow_instances WHERE definition_id = @workflowId AND status IN ('running', 'paused')",
                new { workflowId }, transaction) > 0)
        {
            return false;
        }
        bool updated = connection.Execute(
            "INSERT INTO workflow_definition_lifecycle (definition_id, archived) SELECT id, @archived FROM workflow_definitions WHERE id = @workflowId AND owner_user_id = @ownerUserId ON CONFLICT(definition_id) DO UPDATE SET archived = excluded.archived",
            new { workflowId, ownerUserId, archived = archived ? 1 : 0 }, transaction) > 0;
        if (updated) transaction.Commit();
        return updated;
    }

    public bool HasRuns(string workflowId, string ownerUserId)
    {
        using var connection = OpenConnection();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM workflow_instances WHERE definition_id = @workflowId AND owner_user_id = @ownerUserId",
            new { workflowId, ownerUserId }) > 0;
    }

    public bool DeleteDefinitionIfNeverRun(string workflowId, string ownerUserId)
    {
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM workflow_instances WHERE definition_id = @workflowId", new { workflowId }, transaction) > 0)
        {
            return false;
        }
        bool deleted = connection.Execute("DELETE FROM workflow_definitions WHERE id = @workflowId AND owner_user_id = @ownerUserId", new { workflowId, ownerUserId }, transaction) > 0;
        if (deleted) transaction.Commit();
        return deleted;
    }

    public GatewayWorkflowInstance CreateInstance(GatewayWorkflowInstance instance, IReadOnlyList<WorkflowArtifact>? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            connection.Execute(
                """
                INSERT INTO workflow_instances
                    (id, definition_id, definition_revision, owner_user_id, correlation_key,
                     status, stop_requested, current_node_id, envelope_json, envelope_hash,
                     envelope_revision, snapshot_json, maf_checkpoint_json, failure_code, error,
                     created_at, updated_at, started_at, ended_at)
                VALUES
                    (@id, @definitionId, @definitionRevision, @ownerUserId, @correlationKey,
                     @status, @stopRequested, @currentNodeId, @envelopeJson, @envelopeHash,
                     @envelopeRevision, @snapshotJson, @mafCheckpointJson, @failureCode, @error,
                     @createdAt, @updatedAt, @startedAt, @endedAt)
                """,
                new
                {
                    id = instance.Id,
                    definitionId = instance.WorkflowId,
                    definitionRevision = instance.DefinitionRevision,
                    ownerUserId = instance.OwnerUserId,
                    correlationKey = instance.CorrelationKey,
                    status = instance.Status,
                    stopRequested = instance.StopRequested ? 1 : 0,
                    currentNodeId = instance.CurrentNodeId,
                    envelopeJson = instance.Envelope.ToJsonString(),
                    envelopeHash = instance.EnvelopeHash,
                    envelopeRevision = instance.EnvelopeRevision,
                    snapshotJson = SerializeSnapshot(instance.Snapshot),
                    mafCheckpointJson = (string?)null,
                    failureCode = instance.FailureCode,
                    error = instance.Error,
                    createdAt = instance.CreatedAt,
                    updatedAt = instance.UpdatedAt,
                    startedAt = instance.StartedAt,
                    endedAt = instance.EndedAt,
                },
                transaction);

            connection.Execute(
                """
                INSERT INTO workflow_envelope_revisions
                    (instance_id, revision, envelope_hash, envelope_json, created_at)
                VALUES
                    (@instanceId, @revision, @envelopeHash, @envelopeJson, @createdAt)
                """,
                new
                {
                    instanceId = instance.Id,
                    revision = instance.EnvelopeRevision,
                    envelopeHash = instance.EnvelopeHash,
                    envelopeJson = instance.Envelope.ToJsonString(),
                    createdAt = instance.CreatedAt,
                },
                transaction);

            foreach (GatewayWorkflowStepAttempt step in instance.Steps)
            {
                connection.Execute(
                    """
                    INSERT INTO workflow_step_attempts
                        (id, instance_id, node_id, attempt, run_id, session_id, status,
                         failure_code, started_at, ended_at, created_at)
                    VALUES
                        (@id, @instanceId, @nodeId, @attempt, @runId, @sessionId, @status,
                         @failureCode, @startedAt, @endedAt, @createdAt)
                    """,
                    new
                    {
                        id = step.Id,
                        instanceId = step.InstanceId,
                        nodeId = step.NodeId,
                        attempt = step.Attempt,
                        runId = step.RunId,
                        sessionId = step.SessionId,
                        status = step.Status,
                        failureCode = step.FailureCode,
                        startedAt = step.StartedAt,
                        endedAt = step.EndedAt,
                        createdAt = step.CreatedAt,
                    },
                    transaction);
            }

            foreach (WorkflowArtifact artifact in artifacts ?? [])
            {
                connection.Execute(
                    "INSERT INTO workflow_artifacts (id, instance_id, sha256, name, storage_path, created_at) VALUES (@Id, @InstanceId, @Sha256, @Name, @StoragePath, @CreatedAt)",
                    artifact with { InstanceId = instance.Id },
                    transaction);
            }

            transaction.Commit();
            return instance;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public IReadOnlyList<WorkflowArtifact> ListArtifacts(string instanceId)
    {
        using var connection = OpenConnection();
        return [.. connection.Query<WorkflowArtifact>(
            "SELECT id AS Id, instance_id AS InstanceId, sha256 AS Sha256, name AS Name, storage_path AS StoragePath, created_at AS CreatedAt FROM workflow_artifacts WHERE instance_id = @instanceId ORDER BY created_at, id",
            new { instanceId })];
    }

    public GatewayWorkflowInstance? GetInstance(string instanceId)
    {
        using var connection = OpenConnection();
        if (connection.QueryFirstOrDefault<WorkflowInstanceRow>(
                selectInstancesSql + " WHERE id = @id",
                new { id = instanceId }) is not { } row)
        {
            return null;
        }

        IReadOnlyList<GatewayWorkflowStepAttempt> steps = [.. connection.Query<WorkflowStepAttemptRow>(
            selectAttemptsSql + " WHERE instance_id = @instanceId ORDER BY created_at ASC",
            new { instanceId }).Select(MapAttempt)];
        return MapInstance(row, steps);
    }

    public GatewayWorkflowInstance? GetInstanceByStepSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        using var connection = OpenConnection();
        string? instanceId = connection.QueryFirstOrDefault<string>(
            "SELECT instance_id FROM workflow_step_attempts WHERE session_id = @sessionId LIMIT 1",
            new { sessionId });
        return string.IsNullOrWhiteSpace(instanceId) ? null : GetInstance(instanceId);
    }

    public IReadOnlyList<GatewayWorkflowInstance> ListInstances(string ownerUserId, string? workflowId, int limit, int offset)
    {
        int boundedLimit = Math.Clamp(limit, 1, 200);
        int boundedOffset = Math.Max(offset, 0);
        using var connection = OpenConnection();
        IReadOnlyList<WorkflowInstanceRow> rows = string.IsNullOrWhiteSpace(workflowId)
            ? [.. connection.Query<WorkflowInstanceRow>(
                selectInstancesSql + " WHERE owner_user_id = @ownerUserId ORDER BY created_at DESC LIMIT @boundedLimit OFFSET @boundedOffset",
                new { ownerUserId, boundedLimit, boundedOffset })]
            : [.. connection.Query<WorkflowInstanceRow>(
                selectInstancesSql + " WHERE owner_user_id = @ownerUserId AND definition_id = @workflowId ORDER BY created_at DESC LIMIT @boundedLimit OFFSET @boundedOffset",
                new { ownerUserId, workflowId, boundedLimit, boundedOffset })];

        if (rows.Count == 0)
        {
            return [];
        }

        return [.. rows.Select(static row => MapInstance(row, []))];
    }

    public bool TryRequestStop(string instanceId, string ownerUserId, DateTimeOffset nowUtc, out GatewayWorkflowInstance? instance)
    {
        using var connection = OpenConnection();
        string now = nowUtc.ToString("O");
        int changed = connection.Execute(
            """
            UPDATE workflow_instances
            SET stop_requested = 1,
                status = CASE WHEN status IN ('finished', 'error', 'cancelled') THEN status ELSE 'cancelled' END,
                ended_at = CASE WHEN status IN ('finished', 'error', 'cancelled') THEN ended_at ELSE @now END,
                updated_at = @now
            WHERE id = @instanceId AND owner_user_id = @ownerUserId
            """,
            new { instanceId, ownerUserId, now });
        instance = changed > 0 ? GetInstance(instanceId) : null;
        return instance is not null;
    }

    public IReadOnlyList<GatewayWorkflowInstance> ListRecoverableInstances()
    {
        using var connection = OpenConnection();
        IReadOnlyList<WorkflowInstanceRow> rows = [.. connection.Query<WorkflowInstanceRow>(
            selectInstancesSql + " WHERE status IN ('running', 'paused') ORDER BY created_at ASC")];
        return [.. rows.Select(row =>
        {
            IReadOnlyList<GatewayWorkflowStepAttempt> steps = [.. connection.Query<WorkflowStepAttemptRow>(
                selectAttemptsSql + " WHERE instance_id = @instanceId ORDER BY created_at ASC",
                new { instanceId = row.Id }).Select(MapAttempt)];
            return MapInstance(row, steps);
        })];
    }

    public bool TrySetInstanceStatus(
        string instanceId,
        string status,
        string? currentNodeId,
        string? failureCode,
        string? error,
        bool? stopRequested,
        DateTimeOffset nowUtc,
        out GatewayWorkflowInstance? instance)
    {
        using var connection = OpenConnection();
        string now = nowUtc.ToString("O");
        bool terminal = WorkflowInstanceStatuses.IsTerminal(status);
        int changed = connection.Execute(
            """
            UPDATE workflow_instances
            SET status = @status,
                current_node_id = COALESCE(@currentNodeId, current_node_id),
                failure_code = @failureCode,
                error = @error,
                stop_requested = CASE WHEN @stopRequested IS NULL THEN stop_requested ELSE @stopRequested END,
                updated_at = @now,
                ended_at = CASE WHEN @terminal = 1 THEN COALESCE(ended_at, @now) ELSE ended_at END
            WHERE id = @instanceId
              AND status IN ('running', 'paused')
            """,
            new
            {
                instanceId,
                status,
                currentNodeId,
                failureCode,
                error,
                stopRequested = stopRequested is null ? (int?)null : stopRequested.Value ? 1 : 0,
                terminal = terminal ? 1 : 0,
                now,
            });
        instance = changed > 0 ? GetInstance(instanceId) : GetInstance(instanceId);
        return changed > 0;
    }

    public bool TryUpdateEnvelope(string instanceId, JsonObject envelope, DateTimeOffset nowUtc, out GatewayWorkflowInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        WorkflowEnvelopeCanonical.EnsureSafe(envelope);
        GatewayWorkflowInstance? current = GetInstance(instanceId);
        if (current is null)
        {
            instance = null;
            return false;
        }

        int nextRevision = checked(current.EnvelopeRevision + 1);
        string hash = WorkflowEnvelopeCanonical.Sha256Hex(envelope);
        string json = envelope.ToJsonString();
        string now = nowUtc.ToString("O");
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            int changed = connection.Execute(
                """
                UPDATE workflow_instances
                SET envelope_json = @envelopeJson,
                    envelope_hash = @envelopeHash,
                    envelope_revision = @envelopeRevision,
                    updated_at = @now
                WHERE id = @instanceId
                """,
                new
                {
                    instanceId,
                    envelopeJson = json,
                    envelopeHash = hash,
                    envelopeRevision = nextRevision,
                    now,
                },
                transaction);
            if (changed <= 0)
            {
                transaction.Rollback();
                instance = null;
                return false;
            }

            connection.Execute(
                """
                INSERT INTO workflow_envelope_revisions
                    (instance_id, revision, envelope_hash, envelope_json, created_at)
                VALUES
                    (@instanceId, @revision, @envelopeHash, @envelopeJson, @createdAt)
                """,
                new
                {
                    instanceId,
                    revision = nextRevision,
                    envelopeHash = hash,
                    envelopeJson = json,
                    createdAt = now,
                },
                transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        instance = GetInstance(instanceId);
        return instance is not null;
    }

    public bool TryUpdateStepAttempt(GatewayWorkflowStepAttempt attempt, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE workflow_step_attempts
            SET session_id = @sessionId,
                status = @status,
                failure_code = @failureCode,
                started_at = @startedAt,
                ended_at = @endedAt
            WHERE id = @id
            """,
            new
            {
                id = attempt.Id,
                sessionId = attempt.SessionId,
                status = attempt.Status,
                failureCode = attempt.FailureCode,
                startedAt = attempt.StartedAt,
                endedAt = attempt.EndedAt,
            }) > 0;
    }

    public bool TryAddStepAttempt(GatewayWorkflowStepAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        using var connection = OpenConnection();
        return connection.Execute(
            """
            INSERT INTO workflow_step_attempts
                (id, instance_id, node_id, attempt, run_id, session_id, status,
                 failure_code, started_at, ended_at, created_at)
            VALUES
                (@id, @instanceId, @nodeId, @attempt, @runId, @sessionId, @status,
                 @failureCode, @startedAt, @endedAt, @createdAt)
            """,
            new
            {
                id = attempt.Id,
                instanceId = attempt.InstanceId,
                nodeId = attempt.NodeId,
                attempt = attempt.Attempt,
                runId = attempt.RunId,
                sessionId = attempt.SessionId,
                status = attempt.Status,
                failureCode = attempt.FailureCode,
                startedAt = attempt.StartedAt,
                endedAt = attempt.EndedAt,
                createdAt = attempt.CreatedAt,
            }) > 0;
    }

    public bool TryClearMafCheckpoint(string instanceId, DateTimeOffset nowUtc)
    {
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE workflow_instances
            SET maf_checkpoint_json = NULL,
                updated_at = @now
            WHERE id = @instanceId
            """,
            new { instanceId, now = nowUtc.ToString("O") }) > 0;
    }

    public string? GetMafCheckpointJson(string instanceId)
    {
        using var connection = OpenConnection();
        return connection.QueryFirstOrDefault<string?>(
            "SELECT maf_checkpoint_json FROM workflow_instances WHERE id = @instanceId",
            new { instanceId });
    }

    public bool TrySaveMafCheckpointJson(string instanceId, string checkpointJson, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointJson);
        using var connection = OpenConnection();
        return connection.Execute(
            """
            UPDATE workflow_instances
            SET maf_checkpoint_json = @checkpointJson,
                updated_at = @now
            WHERE id = @instanceId
            """,
            new { instanceId, checkpointJson, now = nowUtc.ToString("O") }) > 0;
    }

    public NaradPersistedEvent AppendNaradEvent(NaradPersistedEvent persistedEvent)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);
        using var connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            WorkflowNaradEventRow? existing = connection.QueryFirstOrDefault<WorkflowNaradEventRow>(
                selectNaradEventsSql + " WHERE event_id = @eventId",
                new { eventId = persistedEvent.EventId },
                transaction);
            if (existing is not null)
            {
                transaction.Commit();
                return MapNaradEvent(existing);
            }

            long nextSequence = connection.ExecuteScalar<long>(
                "SELECT COALESCE(MAX(session_sequence), 0) + 1 FROM workflow_narad_events WHERE instance_id = @instanceId",
                new { instanceId = persistedEvent.SessionId },
                transaction);

            NaradPersistedEvent allocated = persistedEvent with
            {
                SessionSequence = nextSequence,
                WorkflowSequence = nextSequence,
            };
            string eventJson = RewriteWorkflowEventSequences(allocated);
            allocated = allocated with { EventJson = eventJson };
            NaradJournalValidation.ValidateAuthorityPersistedEvent(allocated);

            connection.Execute(
                """
                INSERT INTO workflow_narad_events
                    (event_id, instance_id, session_sequence, workflow_sequence,
                     event_type, record_class, event_json, occurred_at)
                VALUES
                    (@eventId, @instanceId, @sessionSequence, @workflowSequence,
                     @eventType, @recordClass, @eventJson, @occurredAt)
                """,
                new
                {
                    eventId = allocated.EventId,
                    instanceId = allocated.SessionId,
                    sessionSequence = allocated.SessionSequence,
                    workflowSequence = allocated.WorkflowSequence,
                    eventType = allocated.EventType,
                    recordClass = allocated.RecordClass,
                    eventJson = allocated.EventJson,
                    occurredAt = allocated.OccurredAt,
                },
                transaction);
            transaction.Commit();
            return allocated;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public IReadOnlyList<NaradPersistedEvent> ListNaradEvents(string instanceId, long afterSessionSequence, int limit)
    {
        int boundedLimit = Math.Clamp(limit, 1, NaradJournalValidation.MaxHistoryPeekLimit);
        using var connection = OpenConnection();
        return [.. connection.Query<WorkflowNaradEventRow>(
            selectNaradEventsSql + """
                 WHERE instance_id = @instanceId AND session_sequence > @afterSessionSequence
                 ORDER BY session_sequence ASC
                 LIMIT @boundedLimit
                """,
            new { instanceId, afterSessionSequence, boundedLimit }).Select(MapNaradEvent)];
    }

    public long ReadNaradSessionSequence(string instanceId)
    {
        using var connection = OpenConnection();
        return connection.ExecuteScalar<long>(
            "SELECT COALESCE(MAX(session_sequence), 0) FROM workflow_narad_events WHERE instance_id = @instanceId",
            new { instanceId });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return connection;
    }

    private const string selectDefinitionsSql =
        """
        SELECT d.id,
               d.owner_user_id AS OwnerUserId,
               d.name AS Name,
               d.governed AS Governed,
               d.revision AS Revision,
               d.definition_json AS DefinitionJson,
               COALESCE(l.archived, 0) AS Archived,
               d.created_at AS CreatedAt,
               d.updated_at AS UpdatedAt
        FROM workflow_definitions d
        LEFT JOIN workflow_definition_lifecycle l ON l.definition_id = d.id
        """;

    private const string selectInstancesSql =
        """
        SELECT id,
               definition_id AS DefinitionId,
               definition_revision AS DefinitionRevision,
               owner_user_id AS OwnerUserId,
               correlation_key AS CorrelationKey,
               status AS Status,
               stop_requested AS StopRequested,
               current_node_id AS CurrentNodeId,
               envelope_json AS EnvelopeJson,
               envelope_hash AS EnvelopeHash,
               envelope_revision AS EnvelopeRevision,
               snapshot_json AS SnapshotJson,
               failure_code AS FailureCode,
               error AS Error,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt,
               started_at AS StartedAt,
               ended_at AS EndedAt
        FROM workflow_instances
        """;

    private const string selectAttemptsSql =
        """
        SELECT id,
               instance_id AS InstanceId,
               node_id AS NodeId,
               attempt AS Attempt,
               run_id AS RunId,
               session_id AS SessionId,
               status AS Status,
               failure_code AS FailureCode,
               started_at AS StartedAt,
               ended_at AS EndedAt,
               created_at AS CreatedAt
        FROM workflow_step_attempts
        """;

    private static string SerializeGraph(GatewayWorkflowDefinition definition)
        => JsonSerializer.Serialize(WorkflowDefinitionCodec.ToGraph(definition), KautukaJsonContext.Default.GatewayWorkflowGraph);

    private static string SerializeSnapshot(GatewayWorkflowDefinition definition)
    {
        var root = new JsonObject
        {
            ["id"] = definition.Id,
            ["ownerUserId"] = definition.OwnerUserId,
            ["name"] = definition.Name,
            ["governed"] = definition.Governed,
            ["revision"] = definition.Revision,
            ["createdAt"] = definition.CreatedAt,
            ["updatedAt"] = definition.UpdatedAt,
            ["graph"] = JsonNode.Parse(SerializeGraph(definition)),
        };
        return root.ToJsonString();
    }

    private static GatewayWorkflowDefinition MapDefinition(WorkflowDefinitionRow row)
    {
        GatewayWorkflowGraph graph = DeserializeGraph(row.DefinitionJson);
        return WorkflowDefinitionCodec.FromGraph(
            row.Id,
            row.OwnerUserId,
            row.Name,
            row.Governed != 0,
            checked((int)row.Revision),
            graph,
            row.CreatedAt,
            row.UpdatedAt,
            row.Archived != 0);
    }

    private static GatewayWorkflowInstance MapInstance(WorkflowInstanceRow row, IReadOnlyList<GatewayWorkflowStepAttempt> steps)
    {
        GatewayWorkflowDefinition snapshot = MapSnapshot(row);
        JsonObject envelope = JsonNode.Parse(row.EnvelopeJson)?.AsObject()
            ?? throw new InvalidOperationException($"Workflow instance '{row.Id}' envelope is missing.");
        return new GatewayWorkflowInstance(
            row.Id,
            row.DefinitionId,
            checked((int)row.DefinitionRevision),
            row.OwnerUserId,
            row.CorrelationKey,
            row.Status,
            row.StopRequested != 0,
            row.CurrentNodeId,
            envelope,
            row.EnvelopeHash,
            checked((int)row.EnvelopeRevision),
            snapshot,
            [.. steps],
            row.FailureCode,
            row.Error,
            row.CreatedAt,
            row.UpdatedAt,
            row.StartedAt,
            row.EndedAt);
    }

    private static GatewayWorkflowDefinition MapSnapshot(WorkflowInstanceRow row)
    {
        JsonObject root = JsonNode.Parse(row.SnapshotJson)?.AsObject()
            ?? throw new InvalidOperationException($"Workflow instance '{row.Id}' snapshot is missing.");
        GatewayWorkflowGraph graph = DeserializeGraph(root["graph"]?.ToJsonString() ?? "{}");
        return WorkflowDefinitionCodec.FromGraph(
            root["id"]?.GetValue<string>() ?? row.DefinitionId,
            root["ownerUserId"]?.GetValue<string>() ?? row.OwnerUserId,
            root["name"]?.GetValue<string>() ?? string.Empty,
            root["governed"]?.GetValue<bool>() ?? false,
            root["revision"]?.GetValue<int>() ?? checked((int)row.DefinitionRevision),
            graph,
            root["createdAt"]?.GetValue<string>() ?? row.CreatedAt,
            root["updatedAt"]?.GetValue<string>() ?? row.UpdatedAt);
    }

    private static GatewayWorkflowStepAttempt MapAttempt(WorkflowStepAttemptRow row) =>
        new(
            row.Id,
            row.InstanceId,
            row.NodeId,
            checked((int)row.Attempt),
            row.RunId,
            row.SessionId,
            row.Status,
            row.FailureCode,
            row.StartedAt,
            row.EndedAt,
            row.CreatedAt);

    private static GatewayWorkflowGraph DeserializeGraph(string json)
        => JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayWorkflowGraph)
            ?? throw new InvalidOperationException("Workflow definition graph is missing.");

    private static NaradPersistedEvent MapNaradEvent(WorkflowNaradEventRow row)
        => new(
            row.EventId,
            NaradProtocolVersions.V1,
            row.EventType,
            row.InstanceId,
            row.SessionSequence,
            row.RecordClass,
            row.EventJson,
            row.OccurredAt,
            WorkflowId: row.InstanceId,
            WorkflowSequence: row.WorkflowSequence);

    private static string RewriteWorkflowEventSequences(NaradPersistedEvent persistedEvent)
    {
        JsonObject envelope = JsonNode.Parse(persistedEvent.EventJson)?.AsObject()
            ?? throw new InvalidOperationException($"Workflow Narad event '{persistedEvent.EventId}' is missing.");
        envelope["sessionSequence"] = persistedEvent.SessionSequence;
        envelope["workflowSequence"] = persistedEvent.WorkflowSequence ?? persistedEvent.SessionSequence;
        envelope["sessionId"] = persistedEvent.SessionId;
        envelope["workflowId"] = persistedEvent.WorkflowId ?? persistedEvent.SessionId;
        return NaradEventJsonGuard.ValidatePersistedEventJson(envelope.ToJsonString(), "workflow_narad_events.event_json");
    }

    private const string selectNaradEventsSql =
        """
        SELECT event_id AS EventId,
               instance_id AS InstanceId,
               session_sequence AS SessionSequence,
               workflow_sequence AS WorkflowSequence,
               event_type AS EventType,
               record_class AS RecordClass,
               event_json AS EventJson,
               occurred_at AS OccurredAt
        FROM workflow_narad_events
        """;
}

internal sealed class WorkflowDefinitionRow
{
    public string Id { get; init; } = string.Empty;
    public string OwnerUserId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long Governed { get; init; }
    public long Revision { get; init; }
    public string DefinitionJson { get; init; } = "{}";
    public long Archived { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
}

internal sealed class WorkflowInstanceRow
{
    public string Id { get; init; } = string.Empty;
    public string DefinitionId { get; init; } = string.Empty;
    public long DefinitionRevision { get; init; }
    public string OwnerUserId { get; init; } = string.Empty;
    public string? CorrelationKey { get; init; }
    public string Status { get; init; } = string.Empty;
    public long StopRequested { get; init; }
    public string? CurrentNodeId { get; init; }
    public string EnvelopeJson { get; init; } = "{}";
    public string EnvelopeHash { get; init; } = string.Empty;
    public long EnvelopeRevision { get; init; }
    public string SnapshotJson { get; init; } = "{}";
    public string? FailureCode { get; init; }
    public string? Error { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string? StartedAt { get; init; }
    public string? EndedAt { get; init; }
}

internal sealed class WorkflowStepAttemptRow
{
    public string Id { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
    public long Attempt { get; init; }
    public string RunId { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    public string? StartedAt { get; init; }
    public string? EndedAt { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
}

internal sealed class WorkflowNaradEventRow
{
    public string EventId { get; init; } = string.Empty;
    public string InstanceId { get; init; } = string.Empty;
    public long SessionSequence { get; init; }
    public long WorkflowSequence { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string RecordClass { get; init; } = string.Empty;
    public string EventJson { get; init; } = "{}";
    public string OccurredAt { get; init; } = string.Empty;
}
