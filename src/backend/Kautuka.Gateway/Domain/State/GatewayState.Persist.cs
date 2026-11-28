using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal void PersistRawSettingsAuthoritatively(IReadOnlyDictionary<string, string> keyValues, IReadOnlyDictionary<string, string>? jsonValues = null)
    {
        lock (settingsWriteLock)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();

            connection.Execute("PRAGMA busy_timeout=5000;");

            using var transaction = connection.BeginTransaction();
            foreach (KeyValuePair<string, string> kv in keyValues)
            {
                UpsertSetting(connection, transaction, kv.Key, kv.Value);
            }

            foreach (KeyValuePair<string, string> entry in jsonValues ?? new Dictionary<string, string>())
                UpsertSetting(connection, transaction, entry.Key, entry.Value, isJson: true);
            transaction.Commit();
        }
    }

    internal static string GetAgentOwnerNoticesSettingKey(string agentId) => "agent.owner_notices." + agentId;

    internal void ClearAgentOwnerNotices(string agentId)
    {
        AgentOwnerNotices.TryRemove(agentId, out _);
        lock (settingsWriteLock)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            connection.Execute("PRAGMA busy_timeout=5000;");
            connection.Execute(
                "DELETE FROM system_settings WHERE key = @key",
                new { key = GetAgentOwnerNoticesSettingKey(agentId) });
        }
    }

    private static void UpsertSetting(SqliteConnection connection, SqliteTransaction transaction, string key, string value, bool isJson = false)
    {
        connection.Execute(
            "INSERT INTO system_settings (key, value_json, updated_at) VALUES (@key, @valueJson, @updatedAt) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at",
            new { key, valueJson = isJson ? value : $"\"{value}\"", updatedAt = Timestamp() },
            transaction: transaction);
    }
}
