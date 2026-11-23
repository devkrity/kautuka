using System.Collections.Concurrent;

using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed class ConnectorUserEnablement(KautukaDatabase database) : IConnectorUserEnablement
{
    private readonly object writeLock = new();
    private readonly Lazy<ConcurrentDictionary<(string UserId, string ConnectorId), bool>> overrides = new(() => Load(database));

    public bool IsEnabled(string? userId, string connectorId, bool defaultEnabled)
        => userId is not null && overrides.Value.TryGetValue((userId, connectorId), out bool enabled) ? enabled : defaultEnabled;

    public void SetEnabled(string userId, string connectorId, bool enabled)
    {
        lock (writeLock)
        {
            var cache = overrides.Value;
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=ON;
                INSERT INTO connector_user_enablement(user_id, connector_id, enabled) VALUES ($user, $connector, $enabled)
                ON CONFLICT (user_id, connector_id) DO UPDATE SET enabled=excluded.enabled;
                """;
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$connector", connectorId);
            command.Parameters.AddWithValue("$enabled", enabled);
            command.ExecuteNonQuery();
            cache[(userId, connectorId)] = enabled;
        }
    }

    public void ForgetConnector(string connectorId)
    {
        lock (writeLock)
        {
            foreach (var key in overrides.Value.Keys.Where(key => key.ConnectorId == connectorId))
                overrides.Value.TryRemove(key, out _);
        }
    }

    private static ConcurrentDictionary<(string UserId, string ConnectorId), bool> Load(KautukaDatabase database)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id, connector_id, enabled FROM connector_user_enablement";
        using SqliteDataReader rows = command.ExecuteReader();
        var values = new ConcurrentDictionary<(string UserId, string ConnectorId), bool>();
        while (rows.Read()) values[(rows.GetString(0), rows.GetString(1))] = rows.GetBoolean(2);
        return values;
    }
}
