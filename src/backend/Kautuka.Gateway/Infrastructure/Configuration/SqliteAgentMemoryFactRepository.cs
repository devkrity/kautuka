using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteAgentMemoryFactRepository(SqliteAgentStoreDatabase database) : IAgentMemoryFactRepository
{
    public IReadOnlyList<GatewayMemoryFact> ListMemoryFacts(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!database.Exists(storageLocatorKey))
        {
            return [];
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_type, target_id, owner_user_id, title, content, category, source, confidence, created_at, updated_at, session_id
            FROM runtime_memory_facts
            ORDER BY updated_at DESC;
            """;
        var facts = new List<GatewayMemoryFact>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            facts.Add(new GatewayMemoryFact(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDouble(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return facts;
    }

    public void UpsertMemoryFacts(string storageLocatorKey, IReadOnlyList<GatewayMemoryFact> facts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            return;
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        foreach (GatewayMemoryFact fact in facts)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_memory_facts (id, target_type, target_id, owner_user_id, title, content, category, source, confidence, created_at, updated_at, session_id)
                VALUES ($id, $targetType, $targetId, $ownerUserId, $title, $content, $category, $source, $confidence, $createdAt, $updatedAt, $sessionId)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    content = excluded.content,
                    category = excluded.category,
                    source = excluded.source,
                    confidence = excluded.confidence,
                    updated_at = excluded.updated_at,
                    session_id = excluded.session_id;
                """;
            AddMemoryFactParameters(command, fact);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public bool DeleteMemoryFact(string storageLocatorKey, string factId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runtime_memory_facts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", factId);
        return command.ExecuteNonQuery() == 1;
    }

    private static void AddMemoryFactParameters(SqliteCommand command, GatewayMemoryFact fact)
    {
        command.Parameters.AddWithValue("$id", fact.Id);
        command.Parameters.AddWithValue("$targetType", fact.TargetType);
        command.Parameters.AddWithValue("$targetId", fact.TargetId);
        command.Parameters.AddWithValue("$ownerUserId", fact.OwnerUserId);
        command.Parameters.AddWithValue("$title", fact.Title);
        command.Parameters.AddWithValue("$content", fact.Content);
        command.Parameters.AddWithValue("$category", fact.Category);
        command.Parameters.AddWithValue("$source", fact.Source);
        command.Parameters.AddWithValue("$confidence", fact.Confidence);
        command.Parameters.AddWithValue("$createdAt", fact.CreatedAt);
        command.Parameters.AddWithValue("$updatedAt", fact.UpdatedAt);
        command.Parameters.AddWithValue("$sessionId", (object?)fact.SessionId ?? DBNull.Value);
    }
}
