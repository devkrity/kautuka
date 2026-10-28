using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

public sealed class SqliteGatewaySchemaBootstrapperTests
{
    [Fact]
    public void ExistingDatabaseGainsExternalLoginIdentityColumns()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText =
                """
                DROP TABLE external_login_invites;
                DROP INDEX ux_external_logins_provider_tenant_object;
                ALTER TABLE external_logins DROP COLUMN object_id;
                ALTER TABLE external_logins DROP COLUMN tenant_id;
                ALTER TABLE external_logins DROP COLUMN issuer;
                """;
            drop.ExecuteNonQuery();
        }

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.True(ColumnExists(connection, "external_logins", "issuer"));
        Assert.True(ColumnExists(connection, "external_logins", "tenant_id"));
        Assert.True(ColumnExists(connection, "external_logins", "object_id"));
        Assert.True(TableExists(connection, "external_login_invites"));
    }

    [Fact]
    public void ExistingDatabaseGainsLinkedChannelInteractionGroupColumn()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE runtime_channel_interactions DROP COLUMN interaction_group_id";
            drop.ExecuteNonQuery();
        }

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.True(ColumnExists(connection, "runtime_channel_interactions", "interaction_group_id"));
        SqliteGatewaySchemaBootstrapper.ValidateExisting(connection);
    }

    [Fact]
    public void ExistingTelegramEraDatabaseUpgradesProviderNeutralTablesAndPreservesRows()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.CommandText =
                """
                INSERT INTO autopilot_schedules(
                    id, owner_user_id, target_type, target_id, cron_expression, timezone_id, mode, notify_channels,
                    created_at, updated_at)
                VALUES ('schedule-1', 'user-1', 'agent', 'agent-1', '* * * * *', 'UTC', 'prompt', 1, 'created', 'updated');
                INSERT INTO runtime_channel_interactions(
                    id, registration_id, chat_id, prompt, created_at, expires_at, channel_message_id, provider_message_id,
                    interaction_group_id)
                VALUES ('interaction-1', 'telegram-1', 'chat-1', 'approve?', 'created', 'expires', 481, 'provider-481', 'group-1');
                INSERT INTO runtime_users(username, user_id, display_name, created_at, updated_at)
                VALUES ('amit', 'user-1', 'Example User', 'created', 'updated');
                INSERT INTO tool_approval_requests(id, tool_call_id, tool_name, tool_type, requested_at, channel_tether_mode)
                VALUES ('approval-1', 'call-1', 'shell', 'mcp', 'requested', 'single');
                ALTER TABLE autopilot_schedules RENAME COLUMN notify_channels TO notify_telegram;
                ALTER TABLE runtime_channel_interactions RENAME COLUMN channel_message_id TO telegram_message_id;
                ALTER TABLE runtime_users ADD COLUMN telegramuser_idsJson TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE runtime_users ADD COLUMN telegram_bot_ids_json TEXT NOT NULL DEFAULT '[]';
                UPDATE gateway_schema SET schema_id = 'gateway-2026-10-23.1';
                """;
            prepare.ExecuteNonQuery();
        }

        MakeApprovalDefaultLegacy(connection);

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal(1L, QueryInt64(connection, "SELECT notify_channels FROM autopilot_schedules WHERE id='schedule-1'"));
        Assert.Equal(481L, QueryInt64(connection, "SELECT channel_message_id FROM runtime_channel_interactions WHERE id='interaction-1'"));
        Assert.Equal("provider-481", QueryString(connection, "SELECT provider_message_id FROM runtime_channel_interactions WHERE id='interaction-1'"));
        Assert.Equal("group-1", QueryString(connection, "SELECT interaction_group_id FROM runtime_channel_interactions WHERE id='interaction-1'"));
        Assert.Equal("Example User", QueryString(connection, "SELECT display_name FROM runtime_users WHERE user_id='user-1'"));
        Assert.Equal("single", QueryString(connection, "SELECT channel_tether_mode FROM tool_approval_requests WHERE id='approval-1'"));
        using (SqliteCommand insertDefault = connection.CreateCommand())
        {
            insertDefault.CommandText =
                "INSERT INTO tool_approval_requests(id, tool_call_id, tool_name, tool_type, requested_at) " +
                "VALUES ('approval-default', 'call-2', 'shell', 'mcp', 'requested')";
            insertDefault.ExecuteNonQuery();
        }
        Assert.Equal("mirror", QueryString(connection, "SELECT channel_tether_mode FROM tool_approval_requests WHERE id='approval-default'"));
        Assert.False(ColumnExists(connection, "autopilot_schedules", "notify_telegram"));
        Assert.False(ColumnExists(connection, "runtime_channel_interactions", "telegram_message_id"));
        Assert.False(ColumnExists(connection, "runtime_users", "telegramuser_idsJson"));
        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, QueryString(connection, "SELECT schema_id FROM gateway_schema"));
        SqliteGatewaySchemaBootstrapper.ValidateExisting(connection);
    }

    [Fact]
    public void TelegramEraUpgradeStopsWithoutDroppingNonemptyLegacyIdentityColumns()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.CommandText =
                """
                ALTER TABLE runtime_users ADD COLUMN telegramuser_idsJson TEXT NOT NULL DEFAULT '[]';
                ALTER TABLE runtime_users ADD COLUMN telegram_bot_ids_json TEXT NOT NULL DEFAULT '[]';
                INSERT INTO runtime_users(username, user_id, created_at, updated_at, telegramuser_idsJson)
                VALUES ('amit', 'user-1', 'created', 'updated', '["telegram-user-1"]');
                UPDATE gateway_schema SET schema_id = 'gateway-2026-10-23.1';
                """;
            prepare.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection));

        Assert.Contains("non-empty legacy 'telegramuser_idsJson'", error.Message, StringComparison.Ordinal);
        Assert.Equal("[\"telegram-user-1\"]", QueryString(connection, "SELECT telegramuser_idsJson FROM runtime_users WHERE user_id='user-1'"));
        Assert.True(ColumnExists(connection, "runtime_users", "telegramuser_idsJson"));
        Assert.Equal("gateway-2026-10-23.1", QueryString(connection, "SELECT schema_id FROM gateway_schema"));
    }

    [Fact]
    public void FreshDatabaseCreatesCanonicalSchema()
    {
        using SqliteConnection connection = OpenMemoryDatabase();

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, QueryString(
            connection,
            "SELECT schema_id FROM gateway_schema"));
        Assert.True(ColumnExists(connection, "agent_tool_invocations", "child_result_json"));
        Assert.True(ColumnExists(connection, "agent_tool_invocations", "child_outcome"));
        Assert.True(ColumnExists(connection, "agent_tool_invocations", "child_completed_at"));
        Assert.False(TableExists(connection, "provider_secrets"));
        Assert.True(TableExists(connection, "config_snapshots"));
        Assert.True(TableExists(connection, "conversation_asset_upload_staging"));
        Assert.True(ColumnExists(connection, "runtime_channel_interactions", "interaction_group_id"));
        Assert.True(TableExists(connection, "workflow_narad_events"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM providers"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM models"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM agents"));
        Assert.Equal("markitdown", QueryString(
            connection,
            "SELECT id FROM mcp_servers WHERE id = 'markitdown'"));
        Assert.Equal("2024-11-05", QueryString(
            connection,
            "SELECT mcp_protocol_version FROM mcp_servers WHERE id = 'markitdown'"));
        Assert.Equal("openai-responses", QueryString(
            connection,
            "SELECT type FROM provider_type_catalog WHERE type = 'openai-responses'"));
    }

    [Fact]
    public void ExistingCanonicalDatabaseValidatesWithoutMutation()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO system_settings(key, value_json, updated_at) VALUES ('test.preserved', 'true', 'now')";
            insert.ExecuteNonQuery();
        }
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal("true", QueryString(
            connection,
            "SELECT value_json FROM system_settings WHERE key = 'test.preserved'"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM providers"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM models"));
        Assert.Equal(0L, QueryInt64(connection, "SELECT COUNT(*) FROM agents"));
    }

    [Fact]
    public void PreviousWorkflowJournalTableIsMovedToCurrentName()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.CommandText =
                """
                PRAGMA foreign_keys=OFF;
                INSERT INTO workflow_narad_events(
                    event_id, instance_id, session_sequence, workflow_sequence,
                    event_type, record_class, event_json, occurred_at)
                VALUES (
                    'evt-keep', 'wf-1', 1, 1,
                    'workflow.paused', 'authority', '{}', '2026-09-21T00:00:00Z');
                DROP INDEX IF EXISTS idx_workflow_narad_events_instance_sequence;
                ALTER TABLE workflow_narad_events RENAME TO workflow_agip_events;
                CREATE INDEX idx_workflow_agip_events_instance_sequence
                    ON workflow_agip_events(instance_id, session_sequence);
                CREATE TABLE workflow_narad_events (
                    event_id TEXT PRIMARY KEY,
                    instance_id TEXT NOT NULL,
                    session_sequence INTEGER NOT NULL,
                    workflow_sequence INTEGER NOT NULL,
                    event_type TEXT NOT NULL,
                    record_class TEXT NOT NULL,
                    event_json TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    UNIQUE(instance_id, session_sequence)
                );
                CREATE INDEX idx_workflow_narad_events_instance_sequence
                    ON workflow_narad_events(instance_id, session_sequence);
                PRAGMA foreign_keys=ON;
                """;
            prepare.ExecuteNonQuery();
        }

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.True(TableExists(connection, "workflow_narad_events"));
        Assert.False(TableExists(connection, "workflow_agip_events"));
        Assert.Equal("evt-keep", QueryString(
            connection,
            "SELECT event_id FROM workflow_narad_events WHERE event_id = 'evt-keep'"));
        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, QueryString(
            connection,
            "SELECT schema_id FROM gateway_schema"));
    }

    [Fact]
    public void AutoincrementGeneratedSqliteSequenceIsIgnoredByValidation()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO config_snapshots(content_hash, payload_json, captured_at) VALUES ('hash', '{}', 'now')";
            insert.ExecuteNonQuery();
        }

        Assert.Equal("config_snapshots", QueryString(
            connection,
            "SELECT name FROM sqlite_sequence WHERE name = 'config_snapshots'"));

        SqliteGatewaySchemaBootstrapper.ValidateExisting(connection);
    }

    [Fact]
    public void IncompatibleExistingDatabaseFailsWithoutRepairOrDataLoss()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE existing_data(id TEXT PRIMARY KEY, value TEXT NOT NULL); " +
                "INSERT INTO existing_data(id, value) VALUES ('one', 'preserve-me')";
            create.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection));

        Assert.Contains("not recognized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve-me", QueryString(connection, "SELECT value FROM existing_data WHERE id = 'one'"));
        Assert.Equal(0L, QueryInt64(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'gateway_schema'"));
    }

    [Fact]
    public void UnknownSchemaMarkerFailsClosedWhenNoRecognizedLegacyShapeExists()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand markUnknown = connection.CreateCommand())
        {
            markUnknown.CommandText = "UPDATE gateway_schema SET schema_id = 'gateway-2099-01-01.1'";
            markUnknown.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection));

        Assert.Contains("not recognized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("gateway-2099-01-01.1", QueryString(connection, "SELECT schema_id FROM gateway_schema"));
    }

    [Fact]
    public void NewApprovalRowsDefaultToMirrorTetherMode()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO tool_approval_requests(id, tool_call_id, tool_name, tool_type, requested_at)
            VALUES ('req-default', 'tc-1', 'shell', 'mcp', 'now');
            """;
        insert.ExecuteNonQuery();

        Assert.Equal("mirror", QueryString(connection, "SELECT channel_tether_mode FROM tool_approval_requests WHERE id='req-default'"));
        Assert.Contains(
            "DEFAULT 'mirror'",
            QueryString(connection, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='tool_approval_requests'"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDatabaseGainsHitlStatsIndexesWithoutRewrite()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText =
                """
                DROP INDEX idx_tool_approval_requests_stats_agent;
                DROP INDEX idx_tool_approval_requests_stats_mcp;
                """;
            drop.ExecuteNonQuery();
        }

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal(1L, QueryInt64(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='idx_tool_approval_requests_stats_agent'"));
        Assert.Equal(1L, QueryInt64(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='idx_tool_approval_requests_stats_mcp'"));
    }

    [Fact]
    public void ExistingDatabaseGainsMcpProtocolVersionAndKeepsMarkItDownOnInitialize()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE mcp_servers DROP COLUMN mcp_protocol_version";
            drop.ExecuteNonQuery();
        }

        Assert.False(ColumnExists(connection, "mcp_servers", "mcp_protocol_version"));
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.True(ColumnExists(connection, "mcp_servers", "mcp_protocol_version"));
        Assert.Equal("2024-11-05", QueryString(
            connection,
            "SELECT mcp_protocol_version FROM mcp_servers WHERE id = 'markitdown'"));
    }

    [Fact]
    public void StoredProtocolVersionIsLeftAloneOnStartup()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.CommandText =
                """
                UPDATE mcp_servers SET mcp_protocol_version = '2025-11-25' WHERE id = 'markitdown';
                INSERT INTO mcp_servers (id, name, created_at, updated_at, mcp_protocol_version)
                VALUES ('other', 'Other', 't', 't', '2025-06-18');
                """;
            prepare.ExecuteNonQuery();
        }

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal("2025-11-25", QueryString(
            connection,
            "SELECT mcp_protocol_version FROM mcp_servers WHERE id = 'markitdown'"));
        Assert.Equal("2025-06-18", QueryString(
            connection,
            "SELECT mcp_protocol_version FROM mcp_servers WHERE id = 'other'"));
    }

    [Fact]
    public void ExistingDatabaseGainsAgentDefinitionRevisionColumnWithoutRewrite()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        Assert.True(ColumnExists(connection, "tool_approval_requests", "agent_definition_revision"));
        Assert.True(ColumnExists(connection, "tool_approval_requests", "kind"));
        Assert.True(ColumnExists(connection, "tool_approval_requests", "node_id"));

        using (SqliteCommand drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE tool_approval_requests DROP COLUMN node_id";
            drop.ExecuteNonQuery();
        }

        Assert.False(ColumnExists(connection, "tool_approval_requests", "node_id"));
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        Assert.True(ColumnExists(connection, "tool_approval_requests", "node_id"));
        Assert.True(ColumnExists(connection, "tool_approval_requests", "agent_definition_revision"));
    }

    [Fact]
    public void ExistingDatabaseGainsOpenaiResponsesCatalogRowAndAdvancesSchemaId()
    {
        using SqliteConnection connection = OpenMemoryDatabase();
        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.CommandText =
                """
                DELETE FROM provider_type_catalog WHERE type = 'openai-responses';
                UPDATE gateway_schema SET schema_id = 'gateway-2026-10-10.1';
                """;
            prepare.ExecuteNonQuery();
        }

        Assert.Equal("gateway-2026-10-10.1", QueryString(connection, "SELECT schema_id FROM gateway_schema"));
        Assert.Equal(0L, QueryInt64(
            connection,
            "SELECT COUNT(*) FROM provider_type_catalog WHERE type = 'openai-responses'"));

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);

        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, QueryString(
            connection,
            "SELECT schema_id FROM gateway_schema"));
        Assert.Equal("openai-responses", QueryString(
            connection,
            "SELECT type FROM provider_type_catalog WHERE type = 'openai-responses'"));
        Assert.Equal(
            "OpenAI Compatible (Responses)",
            QueryString(connection, "SELECT friendly_name FROM provider_type_catalog WHERE type = 'openai-responses'"));
    }

    private static SqliteConnection OpenMemoryDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test helper rewrites a fixed canonical CREATE TABLE statement into its known legacy form.")]
    private static void MakeApprovalDefaultLegacy(SqliteConnection connection)
    {
        string canonicalSql = QueryString(
            connection,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='tool_approval_requests'");
        string legacySql = canonicalSql
            .Replace("CREATE TABLE tool_approval_requests", "CREATE TABLE tool_approval_requests_legacy", StringComparison.Ordinal)
            .Replace("DEFAULT 'mirror'", "DEFAULT 'single'", StringComparison.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            legacySql + "; " +
            "INSERT INTO tool_approval_requests_legacy SELECT * FROM tool_approval_requests; " +
            "DROP TABLE tool_approval_requests; " +
            "ALTER TABLE tool_approval_requests_legacy RENAME TO tool_approval_requests;";
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed schema identifiers.")]
    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1L;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed SQL statements.")]
    private static string QueryString(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only fixed SQL statements.")]
    private static long QueryInt64(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<long>(command.ExecuteScalar());
    }
}
