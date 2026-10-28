using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

/// <summary>
/// Idempotently creates the credential tables on first run.
/// Operates on the CredentialsHost-owned database only.
/// </summary>
internal static class CredentialsSchemaInitializer
{
    public static void EnsureReady(CredentialsDatabase db)
    {
        using SqliteConnection connection = db.OpenConnection();
        ApplyWalMode(connection);
        CreateTables(connection);
    }

    private static void ApplyWalMode(SqliteConnection connection)
    {
        using var cmd = new SqliteCommand("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA wal_autocheckpoint=1000;", connection);
        cmd.ExecuteNonQuery();
    }

    private static void CreateTables(SqliteConnection connection)
    {
        using var cmd = new SqliteCommand(
            """
            CREATE TABLE IF NOT EXISTS user_credentials (
                user_id         TEXT NOT NULL,
                key             TEXT NOT NULL,
                encrypted_value TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                PRIMARY KEY (user_id, key)
            );

            CREATE TABLE IF NOT EXISTS system_credentials (
                key             TEXT NOT NULL PRIMARY KEY,
                encrypted_value TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS credential_mutation_receipts (
                operation_id TEXT PRIMARY KEY,
                actor_id TEXT NOT NULL,
                result_count INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS credential_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                actor_id TEXT NOT NULL,
                action TEXT NOT NULL,
                scope TEXT NOT NULL,
                subject TEXT NOT NULL,
                key_id TEXT NOT NULL,
                outcome TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS credential_key_rotation (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK(singleton_id = 1),
                target_key_id TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('running', 'completed')),
                last_scope TEXT NOT NULL,
                last_subject TEXT NOT NULL,
                last_key TEXT NOT NULL,
                processed_count INTEGER NOT NULL,
                started_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """,
            connection);

        cmd.ExecuteNonQuery();
    }
}
