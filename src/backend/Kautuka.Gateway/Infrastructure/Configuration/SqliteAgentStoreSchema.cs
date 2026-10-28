using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static class SqliteAgentStoreSchema
{
    private const string schemaId = "agent-store-2026-09-21.1";
    private const string schemaId202608071 = "agent-store-2026-08-07.1";
    private const string dynamicVectorPrefix = "runtime_memory_embeddings_vec_";
    private static readonly ConcurrentDictionary<string, object> schemaLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> validatedDatabasePaths = new(StringComparer.OrdinalIgnoreCase);

    public static void EnsureCurrent(SqliteConnection connection)
    {
        string databasePath = Path.GetFullPath(connection.DataSource);
        if (validatedDatabasePaths.ContainsKey(databasePath))
        {
            return;
        }

        object schemaLock = schemaLocks.GetOrAdd(databasePath, static _ => new object());
        lock (schemaLock)
        {
            if (validatedDatabasePaths.ContainsKey(databasePath))
            {
                return;
            }

            Dictionary<(string Type, string Name), string> actual = ReadAuthoritativeSchema(connection);
            if (actual.Count == 0)
            {
                using var create = connection.CreateCommand();
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
                create.CommandText = SqliteEmbeddedSql.AgentStoreCurrent;
#pragma warning restore CA2100
                create.ExecuteNonQuery();
                actual = ReadAuthoritativeSchema(connection);
            }
            else
            {
                if (TryReadSchemaMarker(connection) is string currentMarker
                    && string.Equals(currentMarker, schemaId202608071, StringComparison.Ordinal))
                {
                    UpgradeAgentToolSurfaceCurrent(connection);
                }

                MovePreviousJournalToCurrent(connection);
                actual = ReadAuthoritativeSchema(connection);
            }

            using var expectedConnection = new SqliteConnection("Data Source=:memory:");
            expectedConnection.Open();
            using (SqliteCommand createExpected = expectedConnection.CreateCommand())
            {
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
                createExpected.CommandText = SqliteEmbeddedSql.AgentStoreCurrent;
#pragma warning restore CA2100
                createExpected.ExecuteNonQuery();
            }

            Dictionary<(string Type, string Name), string> expected = ReadAuthoritativeSchema(expectedConnection);
            List<string> differences = [];
            foreach (((string Type, string Name) key, string expectedSql) in expected)
            {
                if (!actual.TryGetValue(key, out string? actualSql))
                {
                    differences.Add($"missing {key.Type} '{key.Name}'");
                }
                else if (!string.Equals(Normalize(expectedSql), Normalize(actualSql), StringComparison.Ordinal))
                {
                    differences.Add($"incompatible {key.Type} '{key.Name}'");
                }
            }

            differences.AddRange(actual.Keys
                .Where(key => !expected.ContainsKey(key))
                .Select(key => $"unexpected {key.Type} '{key.Name}'"));
            if (differences.Count != 0)
            {
                throw new InvalidOperationException(
                    $"The agent store schema is incompatible with this build ({schemaId}): " +
                    $"{string.Join(", ", differences.Take(8))}. Automatic migration and " +
                    "destructive repair are disabled. Explicitly reset or reprovision this agent store.");
            }

            using var marker = connection.CreateCommand();
            marker.CommandText =
                "SELECT COUNT(*), MIN(schema_id), MAX(schema_id) FROM agent_store_schema";
            using SqliteDataReader markerReader = marker.ExecuteReader();
            bool hasMarker = markerReader.Read();
            long markerCount = hasMarker ? markerReader.GetInt64(0) : 0;
            string? minimumSchemaId = hasMarker && !markerReader.IsDBNull(1) ? markerReader.GetString(1) : null;
            string? maximumSchemaId = hasMarker && !markerReader.IsDBNull(2) ? markerReader.GetString(2) : null;
            if (markerCount != 1
                || !string.Equals(minimumSchemaId, schemaId, StringComparison.Ordinal)
                || !string.Equals(maximumSchemaId, schemaId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The agent store schema marker is incompatible with this build. Expected '{schemaId}', " +
                    $"found {markerCount} row(s) spanning '{minimumSchemaId ?? "<missing>"}' to " +
                    $"'{maximumSchemaId ?? "<missing>"}'. Explicitly reset or reprovision this agent store.");
            }

            validatedDatabasePaths.TryAdd(databasePath, 0);
        }
    }

    public static void Forget(string databasePath)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            validatedDatabasePaths.TryRemove(Path.GetFullPath(databasePath), out _);
        }
    }

    private static string? TryReadSchemaMarker(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_id
            FROM agent_store_schema
            LIMIT 1;
            """;
        try
        {
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// One-way copy of the previous journal into the current baseline table.
    /// ALTER TABLE RENAME is not used because SQLite rewrites stored DDL and
    /// leaves the outbox foreign key pointing at the previous name.
    /// </summary>
    private static void MovePreviousJournalToCurrent(SqliteConnection connection)
    {
        const string previousTable = "agip_runtime_journal";
        const string currentTable = "narad_runtime_journal";
        if (!HasObject(connection, "table", previousTable))
        {
            return;
        }

        if (HasObject(connection, "table", currentTable))
        {
            long currentRows = ReadCount(connection, currentTable);
            long previousRows = ReadCount(connection, previousTable);
            if (currentRows > 0 && previousRows > 0)
            {
                throw new InvalidOperationException(
                    $"The agent store schema is incompatible with this build ({schemaId}): " +
                    $"both '{previousTable}' and '{currentTable}' contain rows. " +
                    "Automatic migration and destructive repair are disabled. Explicitly reset or reprovision this agent store.");
            }

            if (currentRows > 0)
            {
                Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_agip_runtime_journal_canonical_root_sequence");
                Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_agip_runtime_journal_session_sequence");
                Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_agip_runtime_journal_root_sequence");
                Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_agip_runtime_journal_root_run");
                Execute(connection, transaction: null, $"DROP TABLE {previousTable}");
                return;
            }

            Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_narad_runtime_journal_canonical_root_sequence");
            Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_narad_runtime_journal_session_sequence");
            Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_narad_runtime_journal_root_sequence");
            Execute(connection, transaction: null, $"DROP INDEX IF EXISTS idx_narad_runtime_journal_root_run");
            Execute(connection, transaction: null, $"DROP TABLE {currentTable}");
        }

        Execute(connection, transaction: null, "PRAGMA foreign_keys=OFF");
        using SqliteTransaction copy = connection.BeginTransaction();
        try
        {
            Execute(
                connection,
                copy,
                """
                CREATE TABLE narad_runtime_journal (
                    event_id               TEXT    PRIMARY KEY,
                    protocol_version       TEXT    NOT NULL,
                    event_type             TEXT    NOT NULL,
                    session_id             TEXT    NOT NULL,
                    session_sequence       INTEGER NOT NULL,
                    root_run_id            TEXT    NULL,
                    run_id                 TEXT    NULL,
                    sequence               INTEGER NULL,
                    workflow_id            TEXT    NULL,
                    workflow_sequence      INTEGER NULL,
                    record_class           TEXT    NOT NULL,
                    event_json             TEXT    NOT NULL,
                    occurred_at            TEXT    NOT NULL,
                    CHECK (length(event_id) > 0),
                    CHECK (length(protocol_version) > 0),
                    CHECK (length(event_type) > 0),
                    CHECK (length(session_id) > 0),
                    CHECK (session_sequence > 0),
                    CHECK (sequence IS NULL OR sequence > 0),
                    CHECK (workflow_sequence IS NULL OR workflow_sequence > 0),
                    CHECK (record_class = 'authority'),
                    CHECK (length(event_json) > 0),
                    CHECK (length(occurred_at) > 0),
                    CHECK (
                        (root_run_id IS NULL AND run_id IS NULL AND sequence IS NULL)
                        OR (root_run_id IS NOT NULL AND run_id IS NOT NULL AND sequence IS NOT NULL)
                    ),
                    CHECK (
                        (workflow_id IS NULL AND workflow_sequence IS NULL)
                        OR (workflow_id IS NOT NULL AND workflow_sequence IS NOT NULL)
                    ),
                    UNIQUE (session_id, session_sequence)
                )
                """);
            Execute(
                connection,
                copy,
                """
                INSERT INTO narad_runtime_journal
                SELECT * FROM agip_runtime_journal
                """);
            Execute(connection, copy, $"DROP TABLE {previousTable}");
            Execute(
                connection,
                copy,
                """
                CREATE INDEX idx_narad_runtime_journal_session_sequence
                    ON narad_runtime_journal(session_id, session_sequence);
                """);
            Execute(
                connection,
                copy,
                """
                CREATE UNIQUE INDEX idx_narad_runtime_journal_canonical_root_sequence
                    ON narad_runtime_journal(root_run_id, sequence)
                    WHERE json_extract(event_json, '$.sourceEventId') IS NULL;
                """);
            Execute(
                connection,
                copy,
                """
                CREATE INDEX idx_narad_runtime_journal_root_sequence
                    ON narad_runtime_journal(root_run_id, sequence);
                """);
            Execute(
                connection,
                copy,
                """
                CREATE INDEX idx_narad_runtime_journal_root_run
                    ON narad_runtime_journal(root_run_id, run_id);
                """);
            if (HasObject(connection, "table", "runtime_event_outbox"))
            {
                Execute(connection, copy, "DROP INDEX IF EXISTS idx_runtime_event_outbox_pending");
                Execute(connection, copy, "DROP INDEX IF EXISTS idx_runtime_event_outbox_dead_letter");
                Execute(connection, copy, "ALTER TABLE runtime_event_outbox RENAME TO runtime_event_outbox_pre_narad");
                Execute(
                    connection,
                    copy,
                    """
                    CREATE TABLE runtime_event_outbox (
                        event_id                    TEXT PRIMARY KEY,
                        source_storage_locator_key  TEXT NOT NULL,
                        published_at                TEXT NULL,
                        mirrored_at                 TEXT NULL,
                        attempt_count               INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                        last_error                  TEXT NULL,
                        available_at                TEXT NOT NULL,
                        lease_owner                 TEXT NULL,
                        lease_expires_at            TEXT NULL,
                        dead_lettered_at            TEXT NULL,
                        dismissed_at                TEXT NULL,
                        FOREIGN KEY (event_id) REFERENCES narad_runtime_journal(event_id) ON DELETE CASCADE
                    )
                    """);
                Execute(
                    connection,
                    copy,
                    """
                    INSERT INTO runtime_event_outbox
                    SELECT * FROM runtime_event_outbox_pre_narad
                    """);
                Execute(connection, copy, "DROP TABLE runtime_event_outbox_pre_narad");
                Execute(
                    connection,
                    copy,
                    """
                    CREATE INDEX idx_runtime_event_outbox_pending
                        ON runtime_event_outbox(available_at, lease_expires_at, event_id)
                        WHERE (published_at IS NULL OR mirrored_at IS NULL)
                          AND dead_lettered_at IS NULL
                          AND dismissed_at IS NULL;
                    """);
                Execute(
                    connection,
                    copy,
                    """
                    CREATE INDEX idx_runtime_event_outbox_dead_letter
                        ON runtime_event_outbox(dead_lettered_at, event_id)
                        WHERE dead_lettered_at IS NOT NULL AND dismissed_at IS NULL;
                    """);
            }

            Execute(
                connection,
                copy,
                """
                UPDATE agent_store_schema
                SET schema_id = 'agent-store-2026-09-21.1';
                """);
            copy.Commit();
        }
        catch
        {
            copy.Rollback();
            throw;
        }

        Execute(connection, transaction: null, "PRAGMA foreign_keys=ON");
    }

    private static bool HasObject(SqliteConnection connection, string type, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_schema
            WHERE type = $type
              AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Count query uses a closed table identifier.")]
    private static long ReadCount(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// In-place upgrade: persist the agent's resolved tool-surface catalogue.
    /// Preserves all existing rows.
    /// </summary>
    private static void UpgradeAgentToolSurfaceCurrent(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, """
                CREATE TABLE agent_tool_surface_current (
                    singleton_id   INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    fingerprint    TEXT    NOT NULL,
                    catalogue_json TEXT    NOT NULL,
                    built_at       TEXT    NOT NULL,
                    CHECK (length(fingerprint) > 0),
                    CHECK (length(catalogue_json) > 0),
                    CHECK (length(built_at) > 0)
                );
                """);
            Execute(connection, transaction, """
                UPDATE agent_store_schema
                SET schema_id = 'agent-store-2026-09-21.1';
                """);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
#pragma warning disable CA2100 // Fixed migration SQL; never contains external input.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static Dictionary<(string Type, string Name), string> ReadAuthoritativeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL
              AND name NOT LIKE 'sqlite_%'
              AND name NOT LIKE $dynamic_vector_prefix
            ORDER BY type, name;
            """;
        command.Parameters.AddWithValue("$dynamic_vector_prefix", dynamicVectorPrefix + "%");
        var result = new Dictionary<(string Type, string Name), string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)), reader.GetString(2));
        }

        return result;
    }

    private static string Normalize(string sql)
        => string.Concat(sql.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
}
