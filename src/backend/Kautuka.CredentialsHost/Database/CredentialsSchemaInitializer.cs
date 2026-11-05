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
            CREATE TABLE IF NOT EXISTS connector_credential_drafts (
                id TEXT PRIMARY KEY, actor_id TEXT NOT NULL, connector_id TEXT NOT NULL,
                credential_scope TEXT NOT NULL, candidate_json TEXT NOT NULL, base_updated_at TEXT NOT NULL, effective_endpoint TEXT NOT NULL, base_endpoint TEXT NOT NULL, use_durable INTEGER NOT NULL, credential_namespace TEXT NOT NULL,
                declared_keys_json TEXT NOT NULL, remove_keys_json TEXT NOT NULL,
                expires_at INTEGER NOT NULL, completed INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS connector_credential_draft_entries (
                draft_id TEXT NOT NULL, key TEXT NOT NULL, encrypted_value TEXT NOT NULL,
                PRIMARY KEY (draft_id, key),
                FOREIGN KEY (draft_id) REFERENCES connector_credential_drafts(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS connector_draft_sources (
                draft_id TEXT PRIMARY KEY, association_id TEXT NOT NULL,
                FOREIGN KEY (draft_id) REFERENCES connector_credential_drafts(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS connector_credential_associations (
                id TEXT PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, actor_id TEXT NOT NULL,
                connector_id TEXT NOT NULL, credential_namespace TEXT NOT NULL, source_id TEXT NOT NULL, credential_scope TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS connector_credential_association_entries (
                association_id TEXT NOT NULL, scope TEXT NOT NULL, subject TEXT NOT NULL,
                key TEXT NOT NULL, encrypted_value TEXT NOT NULL,
                PRIMARY KEY (association_id, scope, subject, key),
                FOREIGN KEY (association_id) REFERENCES connector_credential_associations(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS connector_association_draft_metadata (
                association_id TEXT PRIMARY KEY, draft_id TEXT NOT NULL, actor_id TEXT NOT NULL, connector_id TEXT NOT NULL,
                credential_scope TEXT NOT NULL, candidate_json TEXT NOT NULL, base_updated_at TEXT NOT NULL,
                effective_endpoint TEXT NOT NULL, base_endpoint TEXT NOT NULL, use_durable INTEGER NOT NULL,
                credential_namespace TEXT NOT NULL, declared_keys_json TEXT NOT NULL, remove_keys_json TEXT NOT NULL,
                expires_at INTEGER NOT NULL,
                FOREIGN KEY (association_id) REFERENCES connector_credential_associations(id) ON DELETE CASCADE
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
