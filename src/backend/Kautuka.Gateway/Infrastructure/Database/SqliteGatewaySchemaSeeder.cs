using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Database;

internal static class SqliteGatewaySchemaSeeder
{
    public static void SeedNewDatabase(SqliteConnection connection, SqliteTransaction transaction)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");

        using var settings = connection.CreateCommand();
        settings.Transaction = transaction;
        settings.CommandText =
            """
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('infra.sandbox.image',              '"kautuka-base:latest"',$at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('infra.sandbox.default_cpus',        '2',                 $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('infra.sandbox.default_memory',      '"4g"',              $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('infra.sandbox.default_timeout',     '120',               $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('infra.sandbox.container_env_json', '{}',                $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.model_timeout_seconds',     '300',               $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.max_run_duration_seconds',  '0',                 $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.hitl_expiry_seconds',       '0',                 $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.hitl_expiry_action',        '"abort"',           $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.allow_multiple_tool_calls', 'true',              $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.allow_concurrent_invocation','false',             $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('runtime.max_parallel_tools',         '0',                 $at);
            INSERT INTO system_settings (key, value_json, updated_at) VALUES ('agent.persona.definitions', $personas, $at);
            """;
        settings.Parameters.AddWithValue("$at", now);
        settings.Parameters.AddWithValue("$personas", CreateDefaultPersonaDefinitions().ToJsonString());
        settings.ExecuteNonQuery();

        SeedMarkitdownConnector(connection, transaction, now);
        EnsureBuiltinProviderCatalogRows(connection, transaction);
    }

    internal static void EnsureBuiltinProviderCatalogRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!TableExists(connection, "provider_type_catalog"))
        {
            return;
        }

        string now = DateTimeOffset.UtcNow.ToString("O");
        foreach ((string type, AiProviderCatalog.ProviderMetadata metadata) in AiProviderCatalog.Defaults)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO provider_type_catalog (
                    type, friendly_name, default_base_url, default_http_timeout_seconds,
                    requires_api_key, requires_base_url, supports_http_timeout_seconds,
                    is_builtin, seed_version, created_at, updated_at)
                VALUES (
                    $type, $friendly_name, $default_base_url, $default_http_timeout_seconds,
                    $requires_api_key, $requires_base_url, $supports_http_timeout_seconds,
                    1, 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$friendly_name", metadata.FriendlyName);
            command.Parameters.AddWithValue(
                "$default_base_url",
                (object?)metadata.DefaultBaseUrl ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$default_http_timeout_seconds",
                metadata.DefaultHttpTimeoutSeconds is int timeoutSeconds
                    ? timeoutSeconds
                    : DBNull.Value);
            command.Parameters.AddWithValue("$requires_api_key", metadata.RequiresApiKey ? 1 : 0);
            command.Parameters.AddWithValue("$requires_base_url", metadata.RequiresBaseUrl ? 1 : 0);
            command.Parameters.AddWithValue(
                "$supports_http_timeout_seconds",
                metadata.SupportsHttpTimeoutSeconds ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
              AND name = $name
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void SeedMarkitdownConnector(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO mcp_servers (
                id, name, description, icon_url, preset_id, transport,
                http_url, encrypted_env_json, env_keys_json, enabled,
                is_internal_system_connector, created_at, updated_at,
                container_image, container_command, container_args_json,
                container_sidecar_name, readiness_timeout_seconds, jit_cooldown_seconds,
                mcp_protocol_version)
            VALUES (
                'markitdown', 'MarkItDown',
                'Official Microsoft MarkItDown MCP server for document-to-markdown conversion.',
                '', 'markitdown', 'container',
                NULL, NULL, '[]', 0,
                1, $now, $now,
                'docker.io/mcp/markitdown', NULL, '[]',
                'mcp-markitdown', 45, 600,
                $protocolVersion);
            """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$protocolVersion", McpServerSchemaCompat.MarkItDownInitialProtocolVersion);
        command.ExecuteNonQuery();
    }

    private static JsonObject CreateDefaultPersonaDefinitions()
        => new()
        {
            ["styles"] = new JsonArray("professional", "helper", "analytical"),
            ["tones"] = new JsonArray("neutral", "friendly", "formal", "assertive", "empathetic"),
            ["verbosity_levels"] = new JsonArray("concise", "normal", "detailed"),
        };
}
