using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal void PersistRawSettingsAuthoritatively(IReadOnlyDictionary<string, string> keyValues)
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

            transaction.Commit();
        }
    }

    private static void UpsertSetting(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        connection.Execute(
            "INSERT INTO system_settings (key, value_json, updated_at) VALUES (@key, @valueJson, @updatedAt) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at",
            new { key, valueJson = $"\"{value}\"", updatedAt = Timestamp() },
            transaction: transaction);
    }
}
