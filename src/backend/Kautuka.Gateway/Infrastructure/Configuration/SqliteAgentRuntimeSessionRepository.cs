using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteAgentRuntimeSessionRepository(SqliteAgentStoreDatabase database) : IAgentRuntimeSessionRepository
{
    public IReadOnlyList<GatewaySession> ListRuntimeSessions(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!database.Exists(storageLocatorKey))
        {
            return [];
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        return LoadRuntimeSessions(connection);
    }

    private static List<GatewaySession> LoadRuntimeSessions(SqliteConnection connection)
    {
        List<GatewaySession> sessions = [];
        using (SqliteCommand sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = """
                  SELECT id, target_type, target_id, owner_user_id, title, compacted, compact_summary, created_at, updated_at,
                      estimated_context_tokens, raw_transcript_estimated_tokens, last_estimated_at, last_compacted_at, last_compaction_reason,
                      archived_at, title_user_set, originator
                FROM runtime_sessions
                ORDER BY updated_at DESC;
                """;

            using SqliteDataReader reader = sessionCommand.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new GatewaySession(
                    Id: reader.GetString(0),
                    TargetType: reader.GetString(1),
                    TargetId: reader.GetString(2),
                    OwnerUserId: reader.GetString(3),
                    Title: reader.IsDBNull(4) ? "New session" : reader.GetString(4),
                    CreatedAt: reader.GetString(7),
                    UpdatedAt: reader.GetString(8),
                    Messages: [],
                    Compacted: reader.GetInt32(5) != 0,
                    CompactSummary: reader.IsDBNull(6) ? null : reader.GetString(6),
                    EstimatedContextTokens: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    RawTranscriptEstimatedTokens: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    LastEstimatedAt: reader.IsDBNull(11) ? null : reader.GetString(11),
                    LastCompactedAt: reader.IsDBNull(12) ? null : reader.GetString(12),
                    LastCompactionReason: reader.IsDBNull(13) ? null : reader.GetString(13),
                    ArchivedAt: reader.IsDBNull(14) ? null : reader.GetString(14),
                    TitleUserSet: !reader.IsDBNull(15) && reader.GetInt32(15) != 0,
                    Originator: reader.IsDBNull(16) ? null : reader.GetString(16)));
            }
        }

        var messagesBySession = new Dictionary<string, List<GatewayMessage>>(StringComparer.Ordinal);
        using (SqliteCommand messageCommand = connection.CreateCommand())
        {
            messageCommand.CommandText = "SELECT session_id, message_json FROM runtime_session_messages ORDER BY session_id, sequence;";
            using SqliteDataReader reader = messageCommand.ExecuteReader();
            while (reader.Read())
            {
                string sessionId = reader.GetString(0);
                GatewayMessage? message = JsonSerializer.Deserialize(reader.GetString(1), KautukaJsonContext.Default.GatewayMessage);
                if (message is null)
                {
                    continue;
                }

                if (!messagesBySession.TryGetValue(sessionId, out List<GatewayMessage>? messages))
                {
                    messages = [];
                    messagesBySession.Add(sessionId, messages);
                }

                messages.Add(message);
            }
        }

        sessions = [.. sessions.Select(session => session with
        {
            Messages = messagesBySession.TryGetValue(session.Id, out List<GatewayMessage>? messages) ? messages : [],
        })];
        return sessions;
    }
    public void CreateRuntimeSession(string storageLocatorKey, GatewaySession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Messages.Count != 0)
        {
            throw new ArgumentException("A new runtime session must not contain messages.", nameof(session));
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_sessions (id, target_type, target_id, owner_user_id, title, compacted, compact_summary, created_at, updated_at, estimated_context_tokens, raw_transcript_estimated_tokens, last_estimated_at, last_compacted_at, last_compaction_reason, archived_at, title_user_set, originator)
            VALUES ($id, $targetType, $targetId, $ownerUserId, $title, $compacted, $compactSummary, $createdAt, $updatedAt, $estimatedContextTokens, $rawTranscriptEstimatedTokens, $lastEstimatedAt, $lastCompactedAt, $lastCompactionReason, $archivedAt, $titleUserSet, $originator);
            """;
        AddRuntimeSessionParameters(command, session);
        command.ExecuteNonQuery();
    }

    public void UpdateRuntimeSession(string storageLocatorKey, GatewaySession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(session);

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runtime_sessions
            SET title = $title,
                compacted = $compacted,
                compact_summary = $compactSummary,
                updated_at = $updatedAt,
                estimated_context_tokens = $estimatedContextTokens,
                raw_transcript_estimated_tokens = $rawTranscriptEstimatedTokens,
                last_estimated_at = $lastEstimatedAt,
                last_compacted_at = $lastCompactedAt,
                last_compaction_reason = $lastCompactionReason,
                archived_at = $archivedAt,
                title_user_set = $titleUserSet,
                originator = $originator
            WHERE id = $id;
            """;
        AddRuntimeSessionParameters(command, session);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Runtime session '{session.Id}' does not exist in the authoritative store.");
        }
    }

    public bool DeleteRuntimeSession(string storageLocatorKey, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runtime_sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteNonQuery() == 1;
    }

    public void AppendRuntimeSessionMessages(string storageLocatorKey, GatewaySession session, IReadOnlyList<GatewayMessage> appendedMessages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(appendedMessages);
        if (appendedMessages.Count == 0)
        {
            return;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE runtime_sessions
                SET title = $title,
                    updated_at = $updatedAt,
                    estimated_context_tokens = $estimatedContextTokens,
                    raw_transcript_estimated_tokens = $rawTranscriptEstimatedTokens,
                    last_estimated_at = $lastEstimatedAt
                WHERE id = $id;
                """;
            AddRuntimeSessionParameters(command, session);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException($"Runtime session '{session.Id}' does not exist in the authoritative store.");
            }
        }

        foreach (GatewayMessage message in appendedMessages)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_session_messages (session_id, sequence, message_id, message_json)
                VALUES ($sessionId, $sequence, $messageId, $messageJson)
                ON CONFLICT(session_id, message_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$sessionId", session.Id);
            command.Parameters.AddWithValue("$sequence", message.Sequence);
            command.Parameters.AddWithValue("$messageId", message.Id);
            command.Parameters.AddWithValue(
                "$messageJson",
                GatewayRuntimePayloadGuard.ValidatePersistedJson(
                    JsonSerializer.Serialize(message, KautukaJsonContext.Default.GatewayMessage),
                    "agent.runtime_session_messages.message_json"));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void AddRuntimeSessionParameters(SqliteCommand command, GatewaySession session)
    {
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$targetType", session.TargetType);
        command.Parameters.AddWithValue("$targetId", session.TargetId);
        command.Parameters.AddWithValue("$ownerUserId", session.OwnerUserId);
        command.Parameters.AddWithValue("$title", (object?)session.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$compacted", session.Compacted ? 1 : 0);
        command.Parameters.AddWithValue("$compactSummary", (object?)session.CompactSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", session.CreatedAt);
        command.Parameters.AddWithValue("$updatedAt", session.UpdatedAt);
        command.Parameters.AddWithValue("$estimatedContextTokens", session.EstimatedContextTokens.HasValue ? session.EstimatedContextTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$rawTranscriptEstimatedTokens", session.RawTranscriptEstimatedTokens.HasValue ? session.RawTranscriptEstimatedTokens.Value : DBNull.Value);
        command.Parameters.AddWithValue("$lastEstimatedAt", (object?)session.LastEstimatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastCompactedAt", (object?)session.LastCompactedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastCompactionReason", (object?)session.LastCompactionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$archivedAt", (object?)session.ArchivedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleUserSet", session.TitleUserSet ? 1 : 0);
        command.Parameters.AddWithValue("$originator", (object?)session.Originator ?? DBNull.Value);
    }
}
