using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqlitePlatformConfigurationProvider
{
    private sealed record SkillRow(
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
        string EnvKeysJson,
        string SourceMetadataJson,
        string Scope,
        string? OwnerUserId);

    private static List<SkillDefinition> LoadSkills(SqliteConnection connection)
    {
        if (ExecuteScalarInt64(
            static conn => new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skills'", conn),
            connection) == 0)
        {
            return [];
        }

        return QueryRows(
            static conn => new SqliteCommand("""
             SELECT id, name, description, import_timestamp, package_hash, status, package_path,
                 has_resources, has_scripts,
                 COALESCE(enabled, 1),
                 COALESCE(env_keys_json, '[]'),
                 source_metadata_json,
                 COALESCE(scope, 'system'),
                 owner_user_id
             FROM skills ORDER BY rowid
            """, conn),
            connection,
            reader => new SkillRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)))
            .Select(static row => new SkillDefinition
            {
                SkillId = row.Id,
                Name = row.Name,
                Description = row.Description,
                ImportTimestamp = DateTimeOffset.TryParse(row.ImportTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp)
                    ? timestamp
                    : DateTimeOffset.UtcNow,
                PackageHash = row.PackageHash,
                Status = row.Status,
                PackagePath = row.PackagePath,
                HasResources = row.HasResources != 0,
                HasScripts = row.HasScripts != 0,
                Enabled = row.Enabled != 0,
                EnvKeys = JsonSerializer.Deserialize(row.EnvKeysJson, jsonContext.ListString) ?? [],
                Scope = row.Scope,
                OwnerUserId = row.OwnerUserId,
                SourceMetadata = JsonSerializer.Deserialize(row.SourceMetadataJson, jsonContext.DictionaryStringString)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            })
            .ToList();
    }

    private static List<McpServerDefinition> LoadMcpServers(SqliteConnection connection)
    {
        // Return empty list if the table doesn't exist yet (pre-V13 databases).
        if (ExecuteScalarInt64(
            static conn => new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_servers'", conn),
            connection) == 0)
        {
            return [];
        }

        return QueryRows(
            static conn => new SqliteCommand("""
             SELECT id, name, description, icon_url, preset_id, transport,
                 http_url, encrypted_env_json, env_keys_json,
                 container_image, container_command, COALESCE(container_args_json, '[]'), container_sidecar_name,
                 readiness_timeout_seconds, jit_cooldown_seconds, healthcheck_url, healthcheck_interval_milliseconds,
                 enabled,
                 COALESCE(is_internal_system_connector, 0),
                 COALESCE(scope, 'system'),
                 owner_user_id,
                 created_at, updated_at,
                 tool_count, tool_names_json,
                 bundle_path, server_type,
                 runtime_artifact_type, runtime_artifact_source_digest, runtime_artifact_build_status,
                 mcp_protocol_version
             FROM mcp_servers ORDER BY rowid
            """, conn),
            connection,
            reader => new McpServerRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetString(21),
                reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetInt32(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                reader.IsDBNull(25) ? null : reader.GetString(25),
                reader.IsDBNull(26) ? null : reader.GetString(26),
                reader.IsDBNull(27) ? null : reader.GetString(27),
                reader.IsDBNull(28) ? null : reader.GetString(28),
                reader.IsDBNull(29) ? null : reader.GetString(29),
                reader.IsDBNull(30) ? null : reader.GetString(30)))
            .Select(r =>
            {
                _ = Enum.TryParse<McpTransport>(r.Transport, ignoreCase: true, out McpTransport transport);
                return new McpServerDefinition
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    IconUrl = ParseAbsoluteUriOrNull(r.IconUrl),
                    PresetId = r.PresetId,
                    Transport = transport,
                    HttpUrl = ParseAbsoluteUriOrNull(r.HttpUrl),
                    EnvKeys = JsonSerializer.Deserialize(r.EnvKeysJson, jsonContext.ListString) ?? [],
                    ContainerImage = r.ContainerImage,
                    ContainerCommand = r.ContainerCommand,
                    ContainerArgs = JsonSerializer.Deserialize(r.ContainerArgsJson, jsonContext.ListString) ?? [],
                    ContainerSidecarName = r.ContainerSidecarName,
                    BundlePath = r.BundlePath,
                    ServerType = r.ServerType,
                    RuntimeArtifactType = r.RuntimeArtifactType,
                    RuntimeArtifactSourceDigest = r.RuntimeArtifactSourceDigest,
                    RuntimeArtifactBuildStatus = r.RuntimeArtifactBuildStatus,
                    ReadinessTimeoutSeconds = r.ReadinessTimeoutSeconds,
                    JitCooldownSeconds = r.JitCooldownSeconds,
                    HealthcheckUrl = ParseAbsoluteUriOrNull(r.HealthcheckUrl),
                    HealthcheckIntervalMilliseconds = r.HealthcheckIntervalMilliseconds,
                    McpProtocolVersion = r.McpProtocolVersion,
                    Enabled = r.Enabled != 0,
                    IsInternalSystemConnector = r.IsInternalSystemConnector != 0,
                    Scope = r.Scope,
                    OwnerUserId = r.OwnerUserId,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                    ToolCount = r.ToolCount,
                    ToolNames = r.ToolNamesJson is null
                        ? null
                        : JsonSerializer.Deserialize(r.ToolNamesJson, jsonContext.ListString),
                };
            }).ToList();
    }

    // ── infrastructure ───────────────────────────────────────────────────────

    private static InfrastructureDefinition LoadInfrastructureDefinition(SqliteConnection connection)
    {
        return new InfrastructureDefinition
        {
            BaseUrl = ParseAbsoluteUriOrNull(LoadSetting(connection, "infra.base_url")),
            Workspace = new WorkspaceDefinition
            {
                BaseRoot = null,
            },
            Sandbox = new SandboxRuntimeDefinition
            {
                Image = LoadSetting(connection, "infra.sandbox.image"),
                DefaultCpus = LoadInt(connection, "infra.sandbox.default_cpus", 0),
                DefaultMemory = LoadSetting(connection, "infra.sandbox.default_memory"),
                DefaultTimeout = LoadInt(connection, "infra.sandbox.default_timeout", 0),
                ContainerEnvironment = LoadJsonDict(connection, "infra.sandbox.container_env_json"),
            },
            Storage = new StorageDefinition(),
        };
    }

    private static Uri? ParseAbsoluteUriOrNull(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : null;

    private static SystemDefinition LoadSystemDefinition(SqliteConnection connection) =>
        new()
        {
            LogLevel = LoadSetting(connection, "system.log_level") ?? "DEBUG",
            Locale = NormalizeLocale(LoadSetting(connection, "system.locale"), "en-US"),
            TimeZone = NormalizeTimeZone(LoadSetting(connection, "system.time_zone"), "UTC"),
            Observability = new ObservabilityDefinition
            {
                IncludeBinaryContent = !string.Equals(
                    LoadSetting(connection, "system.observability.include_binary_content"), "false",
                    StringComparison.OrdinalIgnoreCase),
                CaptureMessageContent = string.Equals(
                    LoadSetting(connection, "system.observability.capture_message_content"), "true",
                    StringComparison.OrdinalIgnoreCase),
            },
        };

    private static string NormalizeLocale(string? candidate, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
        try
        {
            return CultureInfo.GetCultureInfo(value).Name;
        }
        catch (CultureNotFoundException)
        {
            return fallback;
        }
    }

    private static string NormalizeTimeZone(string? candidate, string fallback)
    {
        string value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value).Id;
        }
        catch (TimeZoneNotFoundException)
        {
            return fallback;
        }
        catch (InvalidTimeZoneException)
        {
            return fallback;
        }
    }

    private static ProviderPathPolicySettings LoadProviderPathPolicySettings(SqliteConnection connection)
    {
        var defaults = new ProviderPathPolicySettings();
        return new ProviderPathPolicySettings
        {
            ToolNativePromotionThreshold = LoadDouble(
                connection,
                "provider_path.tool_native_promotion_threshold",
                defaults.ToolNativePromotionThreshold),
            ToolMinObservations = LoadInt(
                connection,
                "provider_path.tool_min_observations",
                defaults.ToolMinObservations),
            ToolNativeDemotionThreshold = LoadDouble(
                connection,
                "provider_path.tool_native_demotion_threshold",
                defaults.ToolNativeDemotionThreshold),
            StructuredJsonStrictPromotionThreshold = LoadDouble(
                connection,
                "provider_path.structured_json_strict_promotion_threshold",
                defaults.StructuredJsonStrictPromotionThreshold),
            StructuredJsonMinObservations = LoadInt(
                connection,
                "provider_path.structured_json_min_observations",
                defaults.StructuredJsonMinObservations),
            VisionEnableThreshold = LoadDouble(
                connection,
                "provider_path.vision_enable_threshold",
                defaults.VisionEnableThreshold),
            ObservationDecayWindowDays = LoadInt(
                connection,
                "provider_path.observation_decay_window_days",
                defaults.ObservationDecayWindowDays),
            ProviderPathFallbackModeEnabled = !string.Equals(
                LoadSetting(connection, "provider_path.fallback_mode_enabled"),
                "false",
                StringComparison.OrdinalIgnoreCase),
        };
    }

    private static MemorySettings LoadMemorySettings(SqliteConnection connection)
    {
        var defaults = new MemorySettings();
        return new MemorySettings
        {
            EmbeddingsEnabled = string.Equals(
                LoadSetting(connection, "memory.embeddings_enabled"),
                "true",
                StringComparison.OrdinalIgnoreCase),
            FactPersistThreshold = LoadDouble(connection,
                "memory.fact_persist_threshold",
                defaults.FactPersistThreshold),
            FactInjectThreshold = LoadDouble(connection,
                "memory.fact_inject_threshold",
                defaults.FactInjectThreshold),
            MaxInjectedFacts = LoadInt(connection,
                "memory.max_injected_facts",
                defaults.MaxInjectedFacts),
            InjectionTokenBudget = LoadInt(connection,
                "memory.injection_token_budget",
                defaults.InjectionTokenBudget),
            SessionCompactionModelId = NormalizeOptionalSetting(
                LoadSetting(connection, "memory.session_compaction_model_id")),
        };
    }

    private static string? NormalizeOptionalSetting(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
