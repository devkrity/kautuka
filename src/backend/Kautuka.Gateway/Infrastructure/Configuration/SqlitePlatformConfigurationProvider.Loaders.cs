using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqlitePlatformConfigurationProvider
{
    internal sealed record ProviderSettingsRow(string Id, string SettingsJson);
    internal sealed record ProviderTypeRow(string Id, string TypeStr);

    private static List<ProviderDefinition> LoadProviders(SqliteConnection connection)
    {
        var providers = new List<ProviderDefinition>();

        var settingsMap = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        List<ProviderSettingsRow> settingsRows = QueryRows(
            static conn => new SqliteCommand("SELECT id, settings_json FROM providers ORDER BY rowid", conn),
            connection,
            reader => new ProviderSettingsRow(
                reader.GetString(0),
                reader.GetString(1)));
        foreach (var r in settingsRows)
        {
            var dict = JsonSerializer.Deserialize(r.SettingsJson, jsonContext.DictionaryStringObject)
                ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            settingsMap[r.Id] = dict;
        }

        List<ProviderTypeRow> typeRows = QueryRows(
            static conn => new SqliteCommand("SELECT id, type FROM providers ORDER BY rowid", conn),
            connection,
            reader => new ProviderTypeRow(
                reader.GetString(0),
                reader.GetString(1)));
        foreach (var r in typeRows)
        {
            providers.Add(new ProviderDefinition
            {
                Id = r.Id,
                Type = r.TypeStr,
                Settings = settingsMap.TryGetValue(r.Id, out Dictionary<string, object?>? s) ? s
                    : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            });
        }

        return providers;
    }

    internal sealed record ModelRow(string Id, string ProviderId, string ModelName, string SettingsJson);

    private static List<ModelDefinition> LoadModels(SqliteConnection connection)
    {
        return QueryRows(
            static conn => new SqliteCommand("SELECT id, provider_id, model_name, settings_json FROM models ORDER BY rowid", conn),
            connection,
            reader => new ModelRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)))
            .Select(r => new ModelDefinition
            {
                Id = r.Id,
                ProviderId = r.ProviderId,
                ModelName = r.ModelName,
                Settings = JsonSerializer.Deserialize(r.SettingsJson, jsonContext.DictionaryStringObject)
                    ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            }).ToList();
    }

    internal sealed record AgentRow(
        string StorageLocatorKey,
        string Id, string Scope, int Enabled, string OwnerUserId);

    private List<AgentDefinition> LoadAgents(SqliteConnection connection)
    {
        var agents = new List<AgentDefinition>();

        foreach (AgentRow row in QueryRows(
            static conn => new SqliteCommand("""
             SELECT id,
                 COALESCE(NULLIF(storage_locator_key, ''), id),
                 COALESCE(scope, 'user'),
                 COALESCE(enabled, 1),
                 COALESCE(owner_user_id, '')
             FROM agents ORDER BY rowid
             """, conn),
            connection,
            reader => new AgentRow(
                reader.GetString(1),
                reader.GetString(0),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4))))
        {
            AgentDefinition? definition;
            try
            {
                definition = agentDefinitionStore.TryLoadLatestDefinition(row.StorageLocatorKey);
                if (definition is null)
                {
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            catch (SqliteException)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }

            agents.Add(new AgentDefinition
            {
                Id = definition.Id,
                Scope = row.Scope,
                OwnerUserId = row.OwnerUserId,
                Enabled = row.Enabled != 0,
                Name = definition.Name,
                ModelId = definition.ModelId,
                SystemPrompt = definition.SystemPrompt,
                SuperExecMode = definition.SuperExecMode,
                ApprovalPolicy = definition.ApprovalPolicy,
                Session = definition.Session,
                Sandbox = definition.Sandbox,
                McpBindings = definition.McpBindings ?? [],
                SkillAttachments = definition.SkillAttachments ?? [],
                ToolExposure = definition.ToolExposure,
                Runtime = definition.Runtime,
            });
        }

        return agents;
    }

    internal sealed record McpServerRow(
        string Id, string Name, string Description, string IconUrl,
        string? PresetId, string Transport, string? HttpUrl, string? EncryptedEnvJson, string EnvKeysJson,
        string? ContainerImage, string? ContainerCommand, string ContainerArgsJson, string? ContainerSidecarName,
        int? ReadinessTimeoutSeconds, int? JitCooldownSeconds, string? HealthcheckUrl, int? HealthcheckIntervalMilliseconds,
        int Enabled, int IsInternalSystemConnector, string Scope, string? OwnerUserId, string CreatedAt, string UpdatedAt,
        int? ToolCount, string? ToolNamesJson, string? BundlePath, string? ServerType,
        string? RuntimeArtifactType, string? RuntimeArtifactSourceDigest, string? RuntimeArtifactBuildStatus,
        string? McpProtocolVersion);
}
