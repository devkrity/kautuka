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
        ConsolidateDraftMetadata(connection);
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
                expires_at INTEGER NOT NULL, completed INTEGER NOT NULL DEFAULT 0,
                source_association_id TEXT NOT NULL DEFAULT '', staged_association_id TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS connector_credential_draft_entries (
                draft_id TEXT NOT NULL, key TEXT NOT NULL, encrypted_value TEXT NOT NULL,
                PRIMARY KEY (draft_id, key),
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

    private static void ConsolidateDraftMetadata(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed migration statements call this helper; values are parameters.")]
        static bool Exists(SqliteConnection db, SqliteTransaction tx, string sql, string value)
        {
            using SqliteCommand query = db.CreateCommand(); query.Transaction = tx; query.CommandText = sql; query.Parameters.AddWithValue("$value", value);
            return Convert.ToInt64(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed migration statements call this helper.")]
        static void Run(SqliteConnection db, SqliteTransaction tx, string sql)
        {
            using SqliteCommand command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.ExecuteNonQuery();
        }
        const string columnQuery = "SELECT COUNT(*) FROM pragma_table_info('connector_credential_drafts') WHERE name=$value";
        const string tableQuery = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$value";
        if (!Exists(connection, transaction, columnQuery, "source_association_id"))
            Run(connection, transaction, "ALTER TABLE connector_credential_drafts ADD COLUMN source_association_id TEXT NOT NULL DEFAULT ''");
        if (!Exists(connection, transaction, columnQuery, "staged_association_id"))
            Run(connection, transaction, "ALTER TABLE connector_credential_drafts ADD COLUMN staged_association_id TEXT NULL");
        if (Exists(connection, transaction, tableQuery, "connector_draft_sources"))
        {
            if (Exists(connection, transaction, "SELECT COUNT(*) FROM connector_draft_sources s LEFT JOIN connector_credential_drafts d ON d.id=s.draft_id WHERE d.id IS NULL OR $value IS NULL", "checked"))
                throw new InvalidOperationException("Connector draft source metadata is inconsistent; no credential migration was applied.");
            Run(connection, transaction, "UPDATE connector_credential_drafts SET source_association_id=COALESCE((SELECT association_id FROM connector_draft_sources WHERE draft_id=connector_credential_drafts.id),'')");
        }
        if (Exists(connection, transaction, tableQuery, "connector_association_draft_metadata"))
        {
            if (Exists(connection, transaction, "SELECT COUNT(*) FROM connector_association_draft_metadata m LEFT JOIN connector_credential_associations a ON a.id=m.association_id LEFT JOIN connector_credential_drafts d ON d.id=m.draft_id WHERE a.id IS NULL OR a.operation_id<>m.draft_id OR a.actor_id<>m.actor_id OR a.connector_id<>m.connector_id OR a.credential_namespace<>m.credential_namespace OR a.credential_scope<>m.credential_scope OR (d.id IS NOT NULL AND (d.actor_id<>m.actor_id OR d.connector_id<>m.connector_id OR d.credential_scope<>m.credential_scope OR d.credential_namespace<>m.credential_namespace OR d.candidate_json<>m.candidate_json OR d.base_updated_at<>m.base_updated_at OR d.effective_endpoint<>m.effective_endpoint OR d.base_endpoint<>m.base_endpoint OR d.use_durable<>m.use_durable OR d.declared_keys_json<>m.declared_keys_json OR d.remove_keys_json<>m.remove_keys_json OR d.expires_at<>m.expires_at OR d.source_association_id<>a.source_id)) OR $value IS NULL", "checked"))
                throw new InvalidOperationException("Staged connector metadata is inconsistent; no credential migration was applied.");
            Run(connection, transaction, "INSERT INTO connector_credential_drafts(id,actor_id,connector_id,credential_scope,candidate_json,base_updated_at,effective_endpoint,base_endpoint,use_durable,credential_namespace,declared_keys_json,remove_keys_json,expires_at,completed,source_association_id,staged_association_id) SELECT m.draft_id,m.actor_id,m.connector_id,m.credential_scope,m.candidate_json,m.base_updated_at,m.effective_endpoint,m.base_endpoint,m.use_durable,m.credential_namespace,m.declared_keys_json,m.remove_keys_json,m.expires_at,1,a.source_id,m.association_id FROM connector_association_draft_metadata m JOIN connector_credential_associations a ON a.id=m.association_id WHERE NOT EXISTS (SELECT 1 FROM connector_credential_drafts d WHERE d.id=m.draft_id)");
            Run(connection, transaction, "UPDATE connector_credential_drafts SET staged_association_id=(SELECT association_id FROM connector_association_draft_metadata WHERE draft_id=connector_credential_drafts.id), source_association_id=COALESCE((SELECT a.source_id FROM connector_association_draft_metadata m JOIN connector_credential_associations a ON a.id=m.association_id WHERE m.draft_id=connector_credential_drafts.id),source_association_id) WHERE id IN (SELECT draft_id FROM connector_association_draft_metadata)");
            Run(connection, transaction, "DROP TABLE connector_association_draft_metadata");
        }
        if (Exists(connection, transaction, tableQuery, "connector_draft_sources")) Run(connection, transaction, "DROP TABLE connector_draft_sources");
        transaction.Commit();
    }
}
