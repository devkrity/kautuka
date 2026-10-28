using System.Globalization;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteAgentContinuationCheckpointRepository(SqliteAgentStoreDatabase database) : IAgentContinuationCheckpointRepository
{
    public void CreateContinuationCheckpoint(string storageLocatorKey, GatewayContinuationCheckpoint checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(checkpoint);

        using var connection = database.OpenCurrentConnection(storageLocatorKey);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO continuation_checkpoints
                (id, continuation_run_id, checkpoint_index, session_id, agent_id, user_id,
                 run_id, boundary_type, session_state_json, effective_behavior_json, artifact_manifest_json,
                 resume_token_hash, created_at)
            VALUES
                ($id, $continuationRunId, $checkpointIndex, $sessionId, $agentId, $userId,
                 $runId, $boundaryType, $sessionStateJson, $effectiveBehaviorJson, $artifactManifestJson,
                 $resumeTokenHash, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", checkpoint.Id);
        command.Parameters.AddWithValue("$continuationRunId", checkpoint.ContinuationRunId);
        command.Parameters.AddWithValue("$checkpointIndex", checkpoint.CheckpointIndex);
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId);
        command.Parameters.AddWithValue("$agentId", checkpoint.AgentId);
        command.Parameters.AddWithValue("$userId", (object?)checkpoint.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", checkpoint.RunId);
        command.Parameters.AddWithValue("$boundaryType", checkpoint.BoundaryType);
        command.Parameters.AddWithValue("$sessionStateJson", checkpoint.SessionStateJson);
        command.Parameters.AddWithValue("$effectiveBehaviorJson", checkpoint.EffectiveBehaviorJson);
        command.Parameters.AddWithValue("$artifactManifestJson", checkpoint.ArtifactManifestJson);
        command.Parameters.AddWithValue("$resumeTokenHash", checkpoint.ResumeTokenHash);
        command.Parameters.AddWithValue("$createdAt", checkpoint.CreatedAt);
        command.ExecuteNonQuery();
    }

    public int GetNextContinuationCheckpointIndex(string storageLocatorKey, string continuationRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationRunId);

        if (!database.Exists(storageLocatorKey))
        {
            return 0;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(checkpoint_index) + 1, 0) FROM continuation_checkpoints WHERE continuation_run_id = $continuationRunId;";
        command.Parameters.AddWithValue("$continuationRunId", continuationRunId);
        object? result = command.ExecuteScalar();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public GatewayContinuationCheckpoint? GetContinuationCheckpointById(string storageLocatorKey, string checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);

        if (!database.Exists(storageLocatorKey))
        {
            return null;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, continuation_run_id, checkpoint_index, session_id, agent_id, user_id,
                   run_id, boundary_type, session_state_json, effective_behavior_json, artifact_manifest_json,
                   resume_token_hash, created_at
            FROM continuation_checkpoints
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", checkpointId);
        return ReadContinuationCheckpoint(command);
    }

    public GatewayContinuationCheckpoint? GetContinuationCheckpointByResumeTokenHash(string storageLocatorKey, string resumeTokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeTokenHash);

        if (!database.Exists(storageLocatorKey))
        {
            return null;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, continuation_run_id, checkpoint_index, session_id, agent_id, user_id,
                   run_id, boundary_type, session_state_json, effective_behavior_json, artifact_manifest_json,
                   resume_token_hash, created_at
            FROM continuation_checkpoints
            WHERE resume_token_hash = $resumeTokenHash
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$resumeTokenHash", resumeTokenHash);
        return ReadContinuationCheckpoint(command);
    }

    public void DeleteContinuationCheckpointsBySessions(string storageLocatorKey, IReadOnlyCollection<string> sessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(sessionIds);

        if (sessionIds.Count == 0)
        {
            return;
        }

        if (!database.Exists(storageLocatorKey))
        {
            return;
        }

        string[] uniqueSessionIds = [.. sessionIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)];

        if (uniqueSessionIds.Length == 0)
        {
            return;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM continuation_checkpoints WHERE session_id = $sessionId;";
        SqliteParameter parameter = command.Parameters.Add("$sessionId", SqliteType.Text);

        foreach (string sessionId in uniqueSessionIds)
        {
            parameter.Value = sessionId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static GatewayContinuationCheckpoint? ReadContinuationCheckpoint(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GatewayContinuationCheckpoint(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));
    }
}
