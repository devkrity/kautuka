using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqliteTopologyConfigurationWriter
{
    internal sealed record UpsertProviderParameters(
        string Id,
        string Name,
        string Type,
        string Settings,
        string Now);

    internal sealed record UpsertModelParameters(
        string Id,
        string ProviderId,
        string ModelName,
        string Settings,
        string? Capabilities,
        string Now);

    internal sealed record UpsertAgentParameters(
        string Id,
        string Scope,
        int Enabled,
        string OwnerUserId,
        string StorageLocatorKey,
        string Now);

    internal sealed record UpsertMcpServerParameters(
        string Id,
        string Name,
        string Desc,
        string Icon,
        string? Preset,
        string Transport,
        string? HttpUrl,
        string? ContainerImage,
        string? ContainerCommand,
        string ContainerArgs,
        string? ContainerSidecarName,
        int? ReadinessTimeoutSeconds,
        int? JitCooldownSeconds,
        string? HealthcheckUrl,
        int? HealthcheckIntervalMilliseconds,
        string? EncEnv,
        string EnvKeys,
        int Enabled,
        int IsInternalSystemConnector,
        string Scope,
        string? OwnerUserId,
        int? ToolCount,
        string? ToolNames,
        string Now,
        string? BundlePath = null,
        string? ServerType = null,
        string? RuntimeArtifactType = null,
        string? RuntimeArtifactSourceDigest = null,
        string? RuntimeArtifactBuildStatus = null,
        string? McpProtocolVersion = null);

    internal sealed record UpsertSkillParameters(
        string Id,
        string Name,
        string Description,
        string ImportTimestamp,
        string PackageHash,
        string Status,
        string PackagePath,
        int HasResources,
        int HasScripts,
        int Enabled,
        string EnvKeys,
        string SourceMetadata,
        string Now,
        string Scope,
        string? OwnerUserId);

    private void UpsertProvider(ProviderDefinition provider)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string settingsJson = JsonSerializer.Serialize(provider.Settings, jsonContext.DictionaryStringObject);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ExecuteNonQuery(connection,
            static (conn, tx) => new SqliteCommand("""
            INSERT INTO providers
                (id, name, type, settings_json, created_at, updated_at)
            VALUES
                (@id, @name, @type, @settings,
                 COALESCE((SELECT created_at FROM providers WHERE id = @id), @now),
                 @now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                type = excluded.type,
                settings_json = excluded.settings_json,
                updated_at = excluded.updated_at
            """, conn, tx),
            command => BindUpsertProvider(command, new UpsertProviderParameters(
                provider.Id,
                provider.Id,
                provider.Type,
                settingsJson,
                now)));
    }

    private void UpsertModel(ModelDefinition model)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string settingsJson = JsonSerializer.Serialize(model.Settings, jsonContext.DictionaryStringObject);
        string? capabilitiesJson = model.Capabilities is null
            ? null
            : JsonSerializer.Serialize(model.Capabilities, KautukaJsonContext.Default.GatewayModelCapabilities);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ExecuteNonQuery(connection,
            static (conn, tx) => new SqliteCommand("""
            INSERT INTO models
                (id, provider_id, model_name, settings_json, capabilities_json, created_at, updated_at)
            VALUES
                (@id, @providerId, @modelName, @settings, @capabilities,
                 COALESCE((SELECT created_at FROM models WHERE id = @id), @now),
                 @now)
            ON CONFLICT(id) DO UPDATE SET
                provider_id = excluded.provider_id,
                model_name = excluded.model_name,
                settings_json = excluded.settings_json,
                capabilities_json = excluded.capabilities_json,
                updated_at = excluded.updated_at
            """, conn, tx),
            command => BindUpsertModel(command, new UpsertModelParameters(
                model.Id,
                model.ProviderId,
                model.ModelName,
                settingsJson,
                capabilitiesJson,
                now)));
    }

    private void UpsertAgent(AgentRegistryEntry agent)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ExecuteNonQuery(connection,
            static (conn, tx) => new SqliteCommand("""
            INSERT INTO agents
                (id, scope, enabled, owner_user_id, storage_locator_key,
                 created_at, updated_at)
            VALUES
                (@id, @scope, @enabled, @ownerUserId, @storageLocatorKey,
                 COALESCE((SELECT created_at FROM agents WHERE id = @id), @now),
                 @now)
            ON CONFLICT(id) DO UPDATE SET
                scope                 = excluded.scope,
                enabled               = excluded.enabled,
                owner_user_id         = excluded.owner_user_id,
                storage_locator_key   = excluded.storage_locator_key,
                updated_at            = excluded.updated_at
            """, conn, tx),
            command => BindUpsertAgent(command, new UpsertAgentParameters(
                agent.Id,
                agent.Scope,
                agent.Enabled ? 1 : 0,
                agent.OwnerUserId,
                agent.StorageLocatorKey,
                now)));
    }

    private void UpsertSkill(SkillDefinition skill)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string sourceMetadataJson = JsonSerializer.Serialize(
            skill.SourceMetadata is Dictionary<string, string> sourceMetadata
                ? sourceMetadata
                : new Dictionary<string, string>(skill.SourceMetadata, StringComparer.OrdinalIgnoreCase),
            jsonContext.DictionaryStringString);
        string envKeysJson = JsonSerializer.Serialize(skill.EnvKeys.ToList(), jsonContext.ListString);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using (var existingIds = new SqliteCommand("SELECT id FROM skills", connection, transaction))
        using (SqliteDataReader reader = existingIds.ExecuteReader())
        {
            while (reader.Read())
            {
                string existingId = reader.GetString(0);
                if (string.Equals(existingId, skill.SkillId, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(existingId, skill.SkillId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Skill id '{skill.SkillId}' differs only by letter case from '{existingId}'.");
                }
            }
        }

        ExecuteNonQuery(connection,
            static (conn, tx) => new SqliteCommand("""
            INSERT INTO skills
                (id, name, description, import_timestamp, package_hash, status, package_path,
                 has_resources, has_scripts, enabled, env_keys_json, source_metadata_json,
                 scope, owner_user_id, created_at, updated_at)
            VALUES
                (@id, @name, @description, @importTimestamp, @packageHash, @status, @packagePath,
                 @hasResources, @hasScripts, @enabled, @envKeys, @sourceMetadata,
                 @scope, @ownerUserId,
                 COALESCE((SELECT created_at FROM skills WHERE id = @id), @now), @now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                import_timestamp = excluded.import_timestamp,
                package_hash = excluded.package_hash,
                status = excluded.status,
                package_path = excluded.package_path,
                has_resources = excluded.has_resources,
                has_scripts = excluded.has_scripts,
                enabled = excluded.enabled,
                env_keys_json = excluded.env_keys_json,
                source_metadata_json = excluded.source_metadata_json,
                scope = excluded.scope,
                owner_user_id = excluded.owner_user_id,
                updated_at = excluded.updated_at
            """, conn, tx),
            command => BindUpsertSkill(command, new UpsertSkillParameters(
                skill.SkillId,
                skill.Name,
                skill.Description,
                skill.ImportTimestamp.ToString("O"),
                skill.PackageHash,
                skill.Status,
                skill.PackagePath,
                skill.HasResources ? 1 : 0,
                skill.HasScripts ? 1 : 0,
                skill.Enabled ? 1 : 0,
                envKeysJson,
                sourceMetadataJson,
                now,
                skill.Scope,
                skill.OwnerUserId)),
            transaction);
        transaction.Commit();
    }

}
