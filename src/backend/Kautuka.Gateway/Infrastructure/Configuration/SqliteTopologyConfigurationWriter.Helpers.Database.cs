using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqliteTopologyConfigurationWriter
{
    private void UpsertMcpServer(McpServerDefinition server)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string containerArgsJson = JsonSerializer.Serialize(server.ContainerArgs.ToList(), jsonContext.ListString);
        string envKeysJson = JsonSerializer.Serialize(server.EnvKeys.ToList(), jsonContext.ListString);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ExecuteNonQuery(connection,
            static (conn, tx) => new SqliteCommand("""
            INSERT INTO mcp_servers
                (id, name, description, icon_url, preset_id, transport,
                 http_url,
                 container_image, container_command, container_args_json, container_sidecar_name,
                 readiness_timeout_seconds, jit_cooldown_seconds, healthcheck_url, healthcheck_interval_milliseconds,
                 mcp_protocol_version,
                 encrypted_env_json, env_keys_json, enabled, is_internal_system_connector,
                 scope, owner_user_id,
                 tool_count, tool_names_json,
                  created_at, updated_at, bundle_path, server_type,
                  runtime_artifact_type, runtime_artifact_source_digest, runtime_artifact_build_status)
            VALUES
                (@id, @name, @desc, @icon, @preset, @transport,
                 @httpUrl,
                 @containerImage, @containerCommand, @containerArgs, @containerSidecarName,
                 @readinessTimeoutSeconds, @jitCooldownSeconds, @healthcheckUrl, @healthcheckIntervalMilliseconds,
                 @mcpProtocolVersion,
                 @encEnv, @envKeys, @enabled, @isInternalSystemConnector,
                 @scope, @ownerUserId,
                 @toolCount, @toolNames,
                  COALESCE((SELECT created_at FROM mcp_servers WHERE id = @id), @now),
                  @now, @bundlePath, @serverType,
                  @runtimeArtifactType, @runtimeArtifactSourceDigest, @runtimeArtifactBuildStatus)
            ON CONFLICT(id) DO UPDATE SET
                name               = excluded.name,
                description        = excluded.description,
                icon_url           = excluded.icon_url,
                preset_id          = excluded.preset_id,
                transport          = excluded.transport,
                http_url           = excluded.http_url,
                container_image    = excluded.container_image,
                container_command  = excluded.container_command,
                container_args_json = excluded.container_args_json,
                container_sidecar_name = excluded.container_sidecar_name,
                readiness_timeout_seconds = excluded.readiness_timeout_seconds,
                jit_cooldown_seconds = excluded.jit_cooldown_seconds,
                healthcheck_url = excluded.healthcheck_url,
                healthcheck_interval_milliseconds = excluded.healthcheck_interval_milliseconds,
                mcp_protocol_version = excluded.mcp_protocol_version,
                encrypted_env_json = excluded.encrypted_env_json,
                env_keys_json      = excluded.env_keys_json,
                enabled            = excluded.enabled,
                is_internal_system_connector = excluded.is_internal_system_connector,
                scope              = excluded.scope,
                owner_user_id      = excluded.owner_user_id,
                tool_count         = COALESCE(excluded.tool_count, mcp_servers.tool_count),
                tool_names_json    = COALESCE(excluded.tool_names_json, mcp_servers.tool_names_json),
                updated_at         = excluded.updated_at,
                bundle_path        = COALESCE(excluded.bundle_path, mcp_servers.bundle_path),
                server_type        = COALESCE(excluded.server_type, mcp_servers.server_type),
                runtime_artifact_type = COALESCE(excluded.runtime_artifact_type, mcp_servers.runtime_artifact_type),
                runtime_artifact_source_digest = COALESCE(excluded.runtime_artifact_source_digest, mcp_servers.runtime_artifact_source_digest),
                runtime_artifact_build_status = COALESCE(excluded.runtime_artifact_build_status, mcp_servers.runtime_artifact_build_status)
            """, conn, tx),
            command => BindUpsertMcpServer(command, new UpsertMcpServerParameters(
                server.Id,
                server.Name,
                server.Description,
                server.IconUrl?.AbsoluteUri ?? string.Empty,
                server.PresetId,
                NormalizeStoredEnum(server.Transport.ToString()),
                server.HttpUrl?.AbsoluteUri,
                server.ContainerImage,
                server.ContainerCommand,
                containerArgsJson,
                server.ContainerSidecarName,
                server.ReadinessTimeoutSeconds,
                server.JitCooldownSeconds,
                server.HealthcheckUrl?.AbsoluteUri,
                server.HealthcheckIntervalMilliseconds,
                null, // encrypted_env_json removed — credentials owned by CredentialsHost
                envKeysJson,
                server.Enabled ? 1 : 0,
                server.IsInternalSystemConnector ? 1 : 0,
                server.Scope,
                server.OwnerUserId,
                server.ToolCount,
                server.ToolNames is { Count: > 0 }
                    ? JsonSerializer.Serialize(server.ToolNames.ToList(), jsonContext.ListString)
                    : null,
                now,
                server.BundlePath,
                server.ServerType,
                server.RuntimeArtifactType,
                server.RuntimeArtifactSourceDigest,
                server.RuntimeArtifactBuildStatus,
                server.McpProtocolVersion)));
    }

    private static void ExecuteNonQuery(SqliteConnection connection, Func<SqliteConnection, SqliteTransaction?, SqliteCommand> commandFactory, Action<SqliteCommand>? configure = null, SqliteTransaction? transaction = null)
    {
        using var command = commandFactory(connection, transaction);
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static List<T> QueryRows<T>(Func<SqliteConnection, SqliteTransaction?, SqliteCommand> commandFactory, SqliteConnection connection, Func<SqliteDataReader, T> map, Action<SqliteCommand>? configure = null, SqliteTransaction? transaction = null)
    {
        using var command = commandFactory(connection, transaction);
        configure?.Invoke(command);

        using SqliteDataReader reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }

    private static void BindUpsertAgent(SqliteCommand command, UpsertAgentParameters parameters)
    {
        command.Parameters.AddWithValue("@id", parameters.Id);
        command.Parameters.AddWithValue("@scope", parameters.Scope);
        command.Parameters.AddWithValue("@enabled", parameters.Enabled);
        command.Parameters.AddWithValue("@ownerUserId", parameters.OwnerUserId);
        command.Parameters.AddWithValue("@storageLocatorKey", parameters.StorageLocatorKey);
        command.Parameters.AddWithValue("@now", parameters.Now);
    }

    private static void BindUpsertProvider(SqliteCommand command, UpsertProviderParameters parameters)
    {
        command.Parameters.AddWithValue("@id", parameters.Id);
        command.Parameters.AddWithValue("@name", parameters.Name);
        command.Parameters.AddWithValue("@type", parameters.Type);
        command.Parameters.AddWithValue("@settings", parameters.Settings);
        command.Parameters.AddWithValue("@now", parameters.Now);
    }

    private static void BindUpsertModel(SqliteCommand command, UpsertModelParameters parameters)
    {
        command.Parameters.AddWithValue("@id", parameters.Id);
        command.Parameters.AddWithValue("@providerId", parameters.ProviderId);
        command.Parameters.AddWithValue("@modelName", parameters.ModelName);
        command.Parameters.AddWithValue("@settings", parameters.Settings);
        command.Parameters.AddWithValue("@capabilities", (object?)parameters.Capabilities ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", parameters.Now);
    }

    private static void BindUpsertMcpServer(SqliteCommand command, UpsertMcpServerParameters parameters)
    {
        command.Parameters.AddWithValue("@id", parameters.Id);
        command.Parameters.AddWithValue("@name", parameters.Name);
        command.Parameters.AddWithValue("@desc", parameters.Desc);
        command.Parameters.AddWithValue("@icon", parameters.Icon);
        command.Parameters.AddWithValue("@preset", (object?)parameters.Preset ?? DBNull.Value);
        command.Parameters.AddWithValue("@transport", parameters.Transport);
        command.Parameters.AddWithValue("@httpUrl", (object?)parameters.HttpUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@containerImage", (object?)parameters.ContainerImage ?? DBNull.Value);
        command.Parameters.AddWithValue("@containerCommand", (object?)parameters.ContainerCommand ?? DBNull.Value);
        command.Parameters.AddWithValue("@containerArgs", parameters.ContainerArgs);
        command.Parameters.AddWithValue("@containerSidecarName", (object?)parameters.ContainerSidecarName ?? DBNull.Value);
        command.Parameters.AddWithValue("@readinessTimeoutSeconds", parameters.ReadinessTimeoutSeconds.HasValue ? parameters.ReadinessTimeoutSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("@jitCooldownSeconds", parameters.JitCooldownSeconds.HasValue ? parameters.JitCooldownSeconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("@healthcheckUrl", (object?)parameters.HealthcheckUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@healthcheckIntervalMilliseconds", parameters.HealthcheckIntervalMilliseconds.HasValue ? parameters.HealthcheckIntervalMilliseconds.Value : DBNull.Value);
        command.Parameters.AddWithValue("@mcpProtocolVersion", (object?)parameters.McpProtocolVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@encEnv", (object?)parameters.EncEnv ?? DBNull.Value);
        command.Parameters.AddWithValue("@envKeys", parameters.EnvKeys);
        command.Parameters.AddWithValue("@enabled", parameters.Enabled);
        command.Parameters.AddWithValue("@isInternalSystemConnector", parameters.IsInternalSystemConnector);
        command.Parameters.AddWithValue("@scope", parameters.Scope);
        command.Parameters.AddWithValue("@ownerUserId", (object?)parameters.OwnerUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@toolCount", parameters.ToolCount.HasValue ? parameters.ToolCount.Value : DBNull.Value);
        command.Parameters.AddWithValue("@toolNames", (object?)parameters.ToolNames ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", parameters.Now);
        command.Parameters.AddWithValue("@bundlePath", (object?)parameters.BundlePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@serverType", (object?)parameters.ServerType ?? DBNull.Value);
        command.Parameters.AddWithValue("@runtimeArtifactType", (object?)parameters.RuntimeArtifactType ?? DBNull.Value);
        command.Parameters.AddWithValue("@runtimeArtifactSourceDigest", (object?)parameters.RuntimeArtifactSourceDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("@runtimeArtifactBuildStatus", (object?)parameters.RuntimeArtifactBuildStatus ?? DBNull.Value);
    }

    private static void BindUpsertSkill(SqliteCommand command, UpsertSkillParameters parameters)
    {
        command.Parameters.AddWithValue("@id", parameters.Id);
        command.Parameters.AddWithValue("@name", parameters.Name);
        command.Parameters.AddWithValue("@description", parameters.Description);
        command.Parameters.AddWithValue("@importTimestamp", parameters.ImportTimestamp);
        command.Parameters.AddWithValue("@packageHash", parameters.PackageHash);
        command.Parameters.AddWithValue("@status", parameters.Status);
        command.Parameters.AddWithValue("@packagePath", parameters.PackagePath);
        command.Parameters.AddWithValue("@hasResources", parameters.HasResources);
        command.Parameters.AddWithValue("@hasScripts", parameters.HasScripts);
        command.Parameters.AddWithValue("@enabled", parameters.Enabled);
        command.Parameters.AddWithValue("@envKeys", parameters.EnvKeys);
        command.Parameters.AddWithValue("@sourceMetadata", parameters.SourceMetadata);
        command.Parameters.AddWithValue("@now", parameters.Now);
        command.Parameters.AddWithValue("@scope", parameters.Scope);
        command.Parameters.AddWithValue("@ownerUserId", (object?)parameters.OwnerUserId ?? DBNull.Value);
    }

    private static string NormalizeStoredEnum(string value)
        => new string(value.Select(char.ToLowerInvariant).ToArray());
}
