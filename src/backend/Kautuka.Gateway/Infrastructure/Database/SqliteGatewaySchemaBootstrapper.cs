using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Database;

/// <summary>
/// Owns the Gateway registry schema boundary. Empty databases are created from the canonical DDL.
/// Existing databases receive registered versioned upgrades, are validated against that same DDL,
/// and advance their schema marker only after validation.
/// Unknown incompatibilities still fail closed without destructive repair.
/// </summary>
internal static class SqliteGatewaySchemaBootstrapper
{
    private const string telegramEraSchemaId = "gateway-2026-10-23.1";
    private static readonly HashSet<string> priorTelegramSchemaIds = new(StringComparer.Ordinal)
    {
        "gateway-2026-10-10.1",
        "gateway-2026-10-19.1",
        telegramEraSchemaId,
    };
    private const string previousCurrentSchemaId = "gateway-2026-10-23.2";
    private static readonly HashSet<string> priorSupportedSchemaIds = new(priorTelegramSchemaIds, StringComparer.Ordinal)
    {
        previousCurrentSchemaId,
    };
    public const string SchemaId = "gateway-2026-10-26.1";

    public static void EnsureCurrent(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (ReadSchema(connection).Count != 0)
        {
            string? schemaId = ReadGatewaySchemaId(connection);
            if (schemaId is null
                || (!string.Equals(schemaId, SchemaId, StringComparison.Ordinal)
                    && !priorSupportedSchemaIds.Contains(schemaId)
                    && !HasLegacyTelegramSchema(connection)))
            {
                throw new InvalidOperationException(
                    $"The Gateway registry schema marker '{schemaId ?? "<missing>"}' is not recognized by this build ({SchemaId}). " +
                    "No compatible migration is registered for this version; restore a supported backup or reprovision the database.");
            }

            ApplyCompatibleUpgrades(connection);
            ValidateExisting(connection);
            AdvanceGatewaySchemaId(connection);
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
            command.CommandText = SqliteEmbeddedSql.GatewayCurrent;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
            SqliteGatewaySchemaSeeder.SeedNewDatabase(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        ValidateExisting(connection);
    }

    public static void ValidateExisting(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IReadOnlyDictionary<SchemaObjectKey, string> expected = CreateExpectedSchema();
        IReadOnlyDictionary<SchemaObjectKey, string> actual = ReadSchema(connection);
        List<string> differences = [];

        foreach ((SchemaObjectKey key, string expectedSql) in expected)
        {
            if (!actual.TryGetValue(key, out string? actualSql))
            {
                differences.Add($"missing {key.Type} '{key.Name}'");
            }
            else if (!string.Equals(NormalizeSql(expectedSql), NormalizeSql(actualSql), StringComparison.Ordinal))
            {
                differences.Add($"incompatible {key.Type} '{key.Name}'");
            }
        }

        foreach (SchemaObjectKey key in actual.Keys)
        {
            if (!expected.ContainsKey(key))
            {
                differences.Add($"unexpected {key.Type} '{key.Name}'");
            }
        }

        if (differences.Count == 0)
        {
            return;
        }

        string summary = string.Join(", ", differences.Take(8));
        if (differences.Count > 8)
        {
            summary += $", and {differences.Count - 8} more";
        }

        throw new InvalidOperationException(
            $"The Gateway registry schema is incompatible with this build ({SchemaId}): {summary}. " +
            "No compatible upgrade is registered for these differences, and destructive repair is disabled. " +
            "Back up any required data, then explicitly reset or reprovision the Gateway database before restarting.");
    }

    private static void ApplyCompatibleUpgrades(SqliteConnection connection)
    {
        Domain.Stores.ToolApprovalSchemaCompat.EnsureAgentDefinitionRevisionColumn(connection);
        EnsureHitlStatsIndexes(connection);
        MovePreviousWorkflowJournalToCurrent(connection);
        EnsureWorkflowTables(connection);
        Domain.Stores.ToolApprovalSchemaCompat.EnsureWorkflowReviewColumns(connection);
        Domain.Connectors.McpServerSchemaCompat.EnsureProtocolVersionColumn(connection);
        Domain.Channels.ChannelInteractionSchemaCompat.EnsureInteractionGroupColumn(connection);
        SqliteGatewaySchemaSeeder.EnsureBuiltinProviderCatalogRows(connection, transaction: null);
        Domain.Stores.ExternalLoginSchemaCompat.EnsureIdentityColumns(connection);
        EnsureExternalLoginTables(connection);
        Domain.Stores.ExternalLoginSchemaCompat.EnsureInviteColumns(connection);
        string? schemaId = ReadGatewaySchemaId(connection);
        if (!string.Equals(schemaId, SchemaId, StringComparison.Ordinal)
            && ((schemaId is not null && priorTelegramSchemaIds.Contains(schemaId)) || HasLegacyTelegramSchema(connection)))
        {
            UpgradeLegacyTelegramSchema(connection);
        }
    }

    private static bool HasLegacyTelegramSchema(SqliteConnection connection)
    {
        if (HasColumn(connection, "autopilot_schedules", "notify_telegram")
            || HasColumn(connection, "runtime_channel_interactions", "telegram_message_id")
            || HasColumn(connection, "runtime_users", "telegramuser_idsJson")
            || HasColumn(connection, "runtime_users", "telegram_bot_ids_json"))
        {
            return true;
        }

        string? approvalSql = ReadObjectSql(connection, "table", "tool_approval_requests");
        return approvalSql is not null
            && NormalizeSql(approvalSql).Contains("CHANNEL_TETHER_MODETEXTNOTNULLDEFAULT'SINGLE'", StringComparison.Ordinal);
    }

    /// <summary>
    /// Migrates known Telegram-era Gateway schemas to the provider-neutral gateway-2026-10-23.2 schema.
    /// It commits table rewrites with the new marker as one transaction and validates foreign keys
    /// before commit. Unknown differences still fail closed in <see cref="ValidateExisting"/>.
    /// </summary>
    private static void UpgradeLegacyTelegramSchema(SqliteConnection connection)
    {
        IReadOnlyDictionary<SchemaObjectKey, string> expected = CreateExpectedSchema();
        var upgrades = new List<TableUpgrade>();

        if (HasColumn(connection, "autopilot_schedules", "notify_telegram")
            && !HasColumn(connection, "autopilot_schedules", "notify_channels"))
        {
            upgrades.Add(new TableUpgrade(
                "autopilot_schedules",
                [
                    "id", "owner_user_id", "target_type", "target_id", "cron_expression", "timezone_id", "mode",
                    "requires_pre_run_approval", "schedule_prompt_context", "workspace_file_paths", "notify_ui_inbox",
                    "pre_run_approval_expiry_policy", "pre_run_approval_timeout_seconds", "overlap_policy", "is_paused",
                    "created_at", "updated_at", "next_due_at", "last_evaluated_at", "lease_owner", "lease_expires_at",
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["notify_channels"] = "notify_telegram",
                }));
        }

        if (HasColumn(connection, "runtime_channel_interactions", "telegram_message_id")
            && !HasColumn(connection, "runtime_channel_interactions", "channel_message_id"))
        {
            upgrades.Add(new TableUpgrade(
                "runtime_channel_interactions",
                [
                    "id", "registration_id", "user_id", "target_type", "target_id", "session_id", "run_id", "chat_id",
                    "message_thread_id", "prompt", "sender_display_name", "options_json", "status", "created_at", "expires_at",
                    "resolved_at", "resolved_by_external_user", "resolution", "approval_request_id", "provider_message_id",
                    "interaction_group_id",
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["channel_message_id"] = "telegram_message_id",
                }));
        }

        if (HasColumn(connection, "runtime_users", "telegramuser_idsJson")
            || HasColumn(connection, "runtime_users", "telegram_bot_ids_json"))
        {
            upgrades.Add(new TableUpgrade(
                "runtime_users",
                [
                    "username", "user_id", "role", "password", "is_active", "email", "can_import_connectors",
                    "can_import_skills", "agent_ids_json", "agent_permissions_json", "created_at", "updated_at", "display_name",
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }

        string? approvalSql = ReadObjectSql(connection, "table", "tool_approval_requests");
        if (approvalSql is not null
            && NormalizeSql(approvalSql).Contains("CHANNEL_TETHER_MODETEXTNOTNULLDEFAULT'SINGLE'", StringComparison.Ordinal))
        {
            upgrades.Add(new TableUpgrade(
                "tool_approval_requests",
                [
                    "id", "session_id", "run_id", "agent_id", "user_id", "tool_call_id", "tool_name", "tool_type",
                    "mcp_server_id", "arguments_json", "requested_at", "decision", "decided_by", "decided_at", "reason",
                    "expires_at", "channel_tether_mode", "checkpoint_id", "resolution_source", "outer_session_id",
                    "outer_agent_id", "agent_definition_revision", "kind", "workflow_id", "instance_id", "node_id",
                ],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }

        if (upgrades.Count == 0)
        {
            return;
        }

        // The legacy user identity arrays cannot be mapped into provider-neutral rows.
        // Check before beginning the transactional schema rewrite.
        EnsureLegacyTelegramIdentityColumnsAreEmpty(connection);
        Execute(connection, "PRAGMA foreign_keys=OFF", transaction: null);
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                foreach (TableUpgrade upgrade in upgrades)
                {
                    RebuildTable(connection, transaction, upgrade, expected);
                }

                using (SqliteCommand foreignKeyCheck = connection.CreateCommand())
                {
                    foreignKeyCheck.Transaction = transaction;
                    foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";
                    using SqliteDataReader reader = foreignKeyCheck.ExecuteReader();
                    if (reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"Gateway schema migration would leave a foreign-key violation in '{reader.GetString(0)}'.");
                    }
                }

                UpdateGatewaySchemaId(connection, transaction, SchemaId);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            Execute(connection, "PRAGMA foreign_keys=ON", transaction: null);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Column names are closed legacy identifiers from the upgrader's fixed list.")]
    private static void EnsureLegacyTelegramIdentityColumnsAreEmpty(SqliteConnection connection)
    {
        foreach (string column in new[] { "telegramuser_idsJson", "telegram_bot_ids_json" })
        {
            if (!HasColumn(connection, "runtime_users", column))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM runtime_users WHERE trim(COALESCE({column}, '[]')) NOT IN ('', '[]', '[ ]', 'null')";
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException(
                    $"Gateway schema upgrade found non-empty legacy '{column}' values. " +
                    "The upgrade stopped without changing the database so those identities can be preserved explicitly.");
            }
        }
    }

    private static void RebuildTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableUpgrade upgrade,
        IReadOnlyDictionary<SchemaObjectKey, string> expected)
    {
        if (!expected.TryGetValue(new SchemaObjectKey("table", upgrade.Name), out string? canonicalSql))
        {
            throw new InvalidOperationException($"No canonical Gateway schema is defined for '{upgrade.Name}'.");
        }

        string temporaryName = $"__gateway_upgrade_{upgrade.Name}";
        if (ReadObjectSql(connection, "table", temporaryName) is not null)
        {
            throw new InvalidOperationException($"Gateway schema upgrade found an unexpected table '{temporaryName}'.");
        }

        string temporarySql = canonicalSql.Replace(
            $"CREATE TABLE {upgrade.Name}",
            $"CREATE TABLE {temporaryName}",
            StringComparison.Ordinal);
        Execute(connection, temporarySql, transaction);

        string[] targetColumns = GetTableColumns(connection, temporaryName).ToArray();
        var selectedExpressions = new List<string>(targetColumns.Length);
        foreach (string targetColumn in targetColumns)
        {
            if (upgrade.SourceOverrides.TryGetValue(targetColumn, out string? sourceColumn))
            {
                if (!HasColumn(connection, upgrade.Name, sourceColumn))
                {
                    throw new InvalidOperationException(
                        $"Gateway schema upgrade cannot map '{upgrade.Name}.{targetColumn}' from missing '{sourceColumn}'.");
                }

                selectedExpressions.Add(sourceColumn);
            }
            else if (HasColumn(connection, upgrade.Name, targetColumn))
            {
                selectedExpressions.Add(targetColumn);
            }
            else if (upgrade.ColumnsToCopy.Contains(targetColumn, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Gateway schema upgrade cannot preserve missing column '{upgrade.Name}.{targetColumn}'.");
            }
            else
            {
                selectedExpressions.Add(GetDefaultExpression(connection, temporaryName, targetColumn));
            }
        }

        string insertSql = $"INSERT INTO {temporaryName} ({string.Join(", ", targetColumns)}) " +
            $"SELECT {string.Join(", ", selectedExpressions)} FROM {upgrade.Name}";
        Execute(connection, insertSql, transaction);
        Execute(connection, $"DROP TABLE {upgrade.Name}", transaction);
        Execute(connection, canonicalSql, transaction);
        Execute(
            connection,
            $"INSERT INTO {upgrade.Name} ({string.Join(", ", targetColumns)}) " +
                $"SELECT {string.Join(", ", targetColumns)} FROM {temporaryName}",
            transaction);
        Execute(connection, $"DROP TABLE {temporaryName}", transaction);

        string tableToken = $"ON{upgrade.Name}(".ToUpperInvariant();
        foreach ((SchemaObjectKey key, string sql) in expected)
        {
            if (key.Type == "index"
                && NormalizeSql(sql).Contains(tableToken, StringComparison.Ordinal))
            {
                Execute(connection, sql, transaction);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a closed identifier from the schema upgrader.")]
    private static IEnumerable<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(1);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and column names are closed identifiers from the canonical schema.")]
    private static string GetDefaultExpression(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                if (reader.IsDBNull(4))
                {
                    throw new InvalidOperationException(
                        $"Gateway schema upgrade cannot supply required column '{tableName}.{columnName}'.");
                }

                return reader.GetString(4);
            }
        }

        throw new InvalidOperationException($"Gateway schema upgrade cannot find default for '{tableName}.{columnName}'.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table and column names are closed identifiers from fixed schema upgrade paths.")]
    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        if (ReadObjectSql(connection, "table", tableName) is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One-way copy of the previous workflow journal into the current baseline table.
    /// Drops an empty current-named sibling if a failed start created one beside the
    /// previous table. The current table is created from the baseline DDL so stored
    /// schema SQL matches validation; renaming the previous table would rewrite that SQL.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL uses closed table and index identifiers.")]
    private static void MovePreviousWorkflowJournalToCurrent(SqliteConnection connection)
    {
        const string previousTable = "workflow_agip_events";
        const string currentTable = "workflow_narad_events";
        const string previousIndex = "idx_workflow_agip_events_instance_sequence";
        const string currentIndex = "idx_workflow_narad_events_instance_sequence";

        if (ReadObjectSql(connection, "table", previousTable) is null)
        {
            return;
        }

        if (ReadObjectSql(connection, "table", currentTable) is not null)
        {
            long currentRows = ReadCount(connection, currentTable);
            long previousRows = ReadCount(connection, previousTable);
            if (currentRows > 0 && previousRows > 0)
            {
                throw new InvalidOperationException(
                    $"The Gateway registry schema is incompatible with this build ({SchemaId}): " +
                    $"both '{previousTable}' and '{currentTable}' contain rows. " +
                    "No compatible upgrade is registered for these differences, and destructive repair is disabled. " +
                    "Back up any required data, then explicitly reset or reprovision the Gateway database before restarting.");
            }

            if (currentRows > 0)
            {
                Execute(connection, $"DROP INDEX IF EXISTS {previousIndex}");
                Execute(connection, $"DROP TABLE {previousTable}");
                return;
            }

            Execute(connection, $"DROP INDEX IF EXISTS {currentIndex}");
            Execute(connection, $"DROP TABLE {currentTable}");
        }

        Execute(connection, "PRAGMA foreign_keys=OFF");
        using SqliteTransaction copy = connection.BeginTransaction();
        try
        {
            Execute(
                connection,
                """
                CREATE TABLE workflow_narad_events (
                    event_id TEXT PRIMARY KEY,
                    instance_id TEXT NOT NULL,
                    session_sequence INTEGER NOT NULL,
                    workflow_sequence INTEGER NOT NULL,
                    event_type TEXT NOT NULL,
                    record_class TEXT NOT NULL,
                    event_json TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    UNIQUE(instance_id, session_sequence),
                    FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE
                )
                """,
                copy);
            Execute(
                connection,
                """
                INSERT INTO workflow_narad_events
                SELECT * FROM workflow_agip_events
                """,
                copy);
            Execute(connection, $"DROP TABLE {previousTable}", copy);
            Execute(
                connection,
                $"CREATE INDEX {currentIndex} ON {currentTable}(instance_id, session_sequence)",
                copy);
            copy.Commit();
        }
        catch
        {
            copy.Rollback();
            throw;
        }

        Execute(connection, "PRAGMA foreign_keys=ON");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Count query uses a closed table identifier.")]
    private static long ReadCount(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL is a closed constant.")]
    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.ExecuteNonQuery();
    }

    private static void EnsureExternalLoginTables(SqliteConnection connection)
    {
        EnsureObject(
            connection,
            "table",
            "external_logins",
            """
            CREATE TABLE external_logins (
                provider TEXT NOT NULL,
                subject TEXT NOT NULL,
                user_id TEXT NOT NULL,
                email TEXT NOT NULL,
                created_at TEXT NOT NULL,
                issuer TEXT,
                tenant_id TEXT,
                object_id TEXT,
                PRIMARY KEY (provider, subject)
            )
            """);
        EnsureObject(
            connection,
            "table",
            "external_login_invites",
            """
            CREATE TABLE external_login_invites (
                code_hash TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                email TEXT NOT NULL,
                expires_at TEXT NOT NULL
            )
            """);
        EnsureObject(
            connection,
            "table",
            "runtime_user_password_flags",
            """
            CREATE TABLE runtime_user_password_flags (
                user_id TEXT PRIMARY KEY,
                has_password INTEGER NOT NULL
            )
            """);
        EnsureIndex(connection, "ux_external_logins_provider_tenant_object",
            "CREATE UNIQUE INDEX ux_external_logins_provider_tenant_object ON external_logins(provider, tenant_id, object_id) WHERE tenant_id IS NOT NULL AND object_id IS NOT NULL");
    }

    private static void AdvanceGatewaySchemaId(SqliteConnection connection)
    {
        if (ReadObjectSql(connection, "table", "gateway_schema") is null)
        {
            return;
        }

        UpdateGatewaySchemaId(connection, transaction: null, SchemaId);
    }

    private static string? ReadGatewaySchemaId(SqliteConnection connection)
    {
        if (ReadObjectSql(connection, "table", "gateway_schema") is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_id FROM gateway_schema ORDER BY created_at DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    private static void UpdateGatewaySchemaId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string schemaId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE gateway_schema SET schema_id = $new WHERE schema_id <> $new";
        command.Parameters.AddWithValue("$new", schemaId);
        command.ExecuteNonQuery();
    }

    private static void EnsureHitlStatsIndexes(SqliteConnection connection)
    {
        if (ReadObjectSql(connection, "table", "tool_approval_requests") is null)
        {
            return;
        }

        EnsureIndex(
            connection,
            "idx_tool_approval_requests_stats_agent",
            "CREATE INDEX idx_tool_approval_requests_stats_agent ON tool_approval_requests(agent_id, tool_name, decision, decided_at)");
        EnsureIndex(
            connection,
            "idx_tool_approval_requests_stats_mcp",
            "CREATE INDEX idx_tool_approval_requests_stats_mcp ON tool_approval_requests(mcp_server_id, tool_name, decision)");
    }

    private static void EnsureWorkflowTables(SqliteConnection connection)
    {
        EnsureObject(
            connection,
            "table",
            "workflow_definitions",
            """
            CREATE TABLE workflow_definitions (
                id TEXT PRIMARY KEY,
                owner_user_id TEXT NOT NULL,
                name TEXT NOT NULL,
                governed INTEGER NOT NULL DEFAULT 0,
                revision INTEGER NOT NULL DEFAULT 1,
                definition_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )
            """);
        EnsureObject(
            connection,
            "table",
            "workflow_definition_lifecycle",
            """
            CREATE TABLE workflow_definition_lifecycle (
                definition_id TEXT PRIMARY KEY,
                archived INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (definition_id) REFERENCES workflow_definitions(id) ON DELETE CASCADE
            )
            """);
        EnsureIndex(
            connection,
            "idx_workflow_definitions_owner",
            "CREATE INDEX idx_workflow_definitions_owner ON workflow_definitions(owner_user_id, updated_at DESC)");
        EnsureObject(
            connection,
            "table",
            "workflow_instances",
            """
            CREATE TABLE workflow_instances (
                id TEXT PRIMARY KEY,
                definition_id TEXT NOT NULL,
                definition_revision INTEGER NOT NULL,
                owner_user_id TEXT NOT NULL,
                correlation_key TEXT NULL,
                status TEXT NOT NULL CHECK (status IN ('running', 'paused', 'finished', 'error', 'cancelled')),
                stop_requested INTEGER NOT NULL DEFAULT 0,
                current_node_id TEXT NULL,
                envelope_json TEXT NOT NULL,
                envelope_hash TEXT NOT NULL,
                envelope_revision INTEGER NOT NULL DEFAULT 0,
                snapshot_json TEXT NOT NULL,
                maf_checkpoint_json TEXT NULL,
                failure_code TEXT NULL,
                error TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                started_at TEXT NULL,
                ended_at TEXT NULL,
                FOREIGN KEY (definition_id) REFERENCES workflow_definitions(id)
            )
            """);
        EnsureIndex(
            connection,
            "idx_workflow_instances_definition_created",
            "CREATE INDEX idx_workflow_instances_definition_created ON workflow_instances(definition_id, created_at DESC)");
        EnsureIndex(
            connection,
            "idx_workflow_instances_owner_created",
            "CREATE INDEX idx_workflow_instances_owner_created ON workflow_instances(owner_user_id, created_at DESC)");
        EnsureIndex(
            connection,
            "idx_workflow_instances_overlap",
            """
            CREATE UNIQUE INDEX idx_workflow_instances_overlap
                ON workflow_instances(definition_id, correlation_key)
                WHERE correlation_key IS NOT NULL AND status IN ('running', 'paused')
            """);
        EnsureObject(
            connection,
            "table",
            "workflow_step_attempts",
            """
            CREATE TABLE workflow_step_attempts (
                id TEXT PRIMARY KEY,
                instance_id TEXT NOT NULL,
                node_id TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                run_id TEXT NOT NULL,
                session_id TEXT NULL,
                status TEXT NOT NULL CHECK (status IN ('pending', 'running', 'finished', 'failed', 'cancelled')),
                failure_code TEXT NULL,
                started_at TEXT NULL,
                ended_at TEXT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE,
                UNIQUE(instance_id, run_id),
                UNIQUE(instance_id, node_id, attempt)
            )
            """);
        EnsureIndex(
            connection,
            "idx_workflow_step_attempts_instance",
            "CREATE INDEX idx_workflow_step_attempts_instance ON workflow_step_attempts(instance_id, created_at)");
        EnsureObject(
            connection,
            "table",
            "workflow_envelope_revisions",
            """
            CREATE TABLE workflow_envelope_revisions (
                instance_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                envelope_hash TEXT NOT NULL,
                envelope_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (instance_id, revision),
                FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE
            )
            """);
        EnsureObject(
            connection,
            "table",
            "workflow_artifacts",
            """
            CREATE TABLE workflow_artifacts (
                id TEXT PRIMARY KEY,
                instance_id TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                name TEXT NOT NULL,
                storage_path TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE,
                UNIQUE(instance_id, sha256)
            )
            """);
        EnsureIndex(
            connection,
            "idx_workflow_artifacts_instance",
            "CREATE INDEX idx_workflow_artifacts_instance ON workflow_artifacts(instance_id)");
        EnsureObject(
            connection,
            "table",
            "workflow_narad_events",
            """
            CREATE TABLE workflow_narad_events (
                event_id TEXT PRIMARY KEY,
                instance_id TEXT NOT NULL,
                session_sequence INTEGER NOT NULL,
                workflow_sequence INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                record_class TEXT NOT NULL,
                event_json TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                UNIQUE(instance_id, session_sequence),
                FOREIGN KEY (instance_id) REFERENCES workflow_instances(id) ON DELETE CASCADE
            )
            """);
        EnsureIndex(
            connection,
            "idx_workflow_narad_events_instance_sequence",
            "CREATE INDEX idx_workflow_narad_events_instance_sequence ON workflow_narad_events(instance_id, session_sequence)");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL is a closed constant; object name is a closed identifier.")]
    private static void EnsureObject(SqliteConnection connection, string type, string name, string createSql)
    {
        if (ReadObjectSql(connection, type, name) is not null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = createSql;
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "DDL is a closed constant; index name is a closed identifier.")]
    private static void EnsureIndex(SqliteConnection connection, string indexName, string createSql)
    {
        if (ReadObjectSql(connection, "index", indexName) is not null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = createSql;
        command.ExecuteNonQuery();
    }

    private static string? ReadObjectSql(SqliteConnection connection, string type, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sql
            FROM sqlite_schema
            WHERE type = $type
              AND name = $name
              AND sql IS NOT NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static Dictionary<SchemaObjectKey, string> CreateExpectedSchema()
    {
        using var expected = new SqliteConnection("Data Source=:memory:");
        expected.Open();
        using var command = expected.CreateCommand();
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
        command.CommandText = SqliteEmbeddedSql.GatewayCurrent;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
        return ReadSchema(expected);
    }

    private static Dictionary<SchemaObjectKey, string> ReadSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, name, sql
            FROM sqlite_schema
            WHERE sql IS NOT NULL
              AND name NOT LIKE 'sqlite_%'
              AND type IN ('table', 'index', 'view', 'trigger')
            ORDER BY type, name;
            """;

        var result = new Dictionary<SchemaObjectKey, string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SchemaObjectKey(reader.GetString(0), reader.GetString(1)), reader.GetString(2));
        }

        return result;
    }

    private static string NormalizeSql(string sql)
        => string.Concat(sql.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private sealed record TableUpgrade(
        string Name,
        IReadOnlyList<string> ColumnsToCopy,
        IReadOnlyDictionary<string, string> SourceOverrides);

    private readonly record struct SchemaObjectKey(string Type, string Name);
}
