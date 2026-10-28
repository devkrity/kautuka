using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class SqliteAgentToolSurfaceStore(
    SqliteAgentStoreDatabase database,
    IAgentDefinitionStore agentDefinitionStore) : IAgentToolSurfaceStore
{
    private readonly ConcurrentDictionary<string, AgentToolSurfaceSnapshot> memory = new(StringComparer.OrdinalIgnoreCase);

    public AgentToolSurfaceSnapshot? TryGet(string agentId, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        if (memory.TryGetValue(MemoryKey(agentId, fingerprint), out AgentToolSurfaceSnapshot? cached))
        {
            return cached;
        }

        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        if (!database.Exists(storageLocatorKey))
        {
            return null;
        }

        using SqliteConnection connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT fingerprint, catalogue_json, built_at
            FROM agent_tool_surface_current
            WHERE singleton_id = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string storedFingerprint = reader.GetString(0);
        if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        AgentToolSurfaceCatalogue? catalogue;
        try
        {
            catalogue = JsonSerializer.Deserialize(
                reader.GetString(1),
                KautukaJsonContext.Default.AgentToolSurfaceCatalogue);
        }
        catch (JsonException)
        {
            return null;
        }
        if (catalogue is null)
        {
            return null;
        }

        var snapshot = new AgentToolSurfaceSnapshot(storedFingerprint, catalogue, reader.GetString(2));
        memory[MemoryKey(agentId, fingerprint)] = snapshot;
        return snapshot;
    }

    public void Save(string agentId, string fingerprint, AgentToolSurfaceCatalogue catalogue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(catalogue);

        string builtAt = DateTimeOffset.UtcNow.ToString("O");
        string catalogueJson = JsonSerializer.Serialize(catalogue, KautukaJsonContext.Default.AgentToolSurfaceCatalogue);
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        using SqliteConnection connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_tool_surface_current (singleton_id, fingerprint, catalogue_json, built_at)
            VALUES (1, $fingerprint, $catalogueJson, $builtAt)
            ON CONFLICT(singleton_id) DO UPDATE SET
                fingerprint = excluded.fingerprint,
                catalogue_json = excluded.catalogue_json,
                built_at = excluded.built_at;
            """;
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$catalogueJson", catalogueJson);
        command.Parameters.AddWithValue("$builtAt", builtAt);
        command.ExecuteNonQuery();

        memory[MemoryKey(agentId, fingerprint)] = new AgentToolSurfaceSnapshot(fingerprint, catalogue, builtAt);
    }

    private static string MemoryKey(string agentId, string fingerprint)
        => string.Concat(agentId, "\u001f", fingerprint);
}
