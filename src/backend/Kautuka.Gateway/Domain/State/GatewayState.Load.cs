using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipping agent {AgentId} because its per-agent store {StorageLocatorKey} is missing.")]
    private static partial void LogMissingAgentStore(ILogger logger, string agentId, string storageLocatorKey);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Skipping agent {AgentId} because its per-agent store {StorageLocatorKey} could not be loaded.")]
    private static partial void LogFailedAgentStoreLoad(ILogger logger, Exception exception, string agentId, string storageLocatorKey);

    // ── load methods ─────────────────────────────────────────────────────────

    private static void LoadProviders(SqliteConnection connection, GatewayState state)
    {
        foreach (DbProvider row in connection.Query<DbProvider>(
            "SELECT id AS Id, COALESCE(name, id) AS Name, type AS Type, settings_json AS SettingsJson, created_at AS CreatedAt FROM providers ORDER BY rowid"))
        {
            var settings = SafeCreateJsonObject(DeserializeJsonElement(row.SettingsJson));
            state.Providers[row.Id] = new GatewayProvider(row.Id, row.Name, row.Type, settings, row.CreatedAt);
        }
    }

    private static void LoadModels(SqliteConnection connection, GatewayState state)
    {
        foreach (DbModel row in connection.Query<DbModel>(
            "SELECT id AS Id, provider_id AS ProviderId, model_name AS ModelName, settings_json AS SettingsJson, capabilities_json AS CapabilitiesJson, created_at AS CreatedAt FROM models ORDER BY rowid"))
        {
            var settings = SafeCreateJsonObject(DeserializeJsonElement(row.SettingsJson));
            GatewayModelCapabilities? capabilities = string.IsNullOrWhiteSpace(row.CapabilitiesJson)
                ? null
                : JsonSerializer.Deserialize(row.CapabilitiesJson, KautukaJsonContext.Default.GatewayModelCapabilities);
            state.Models[row.Id] = new GatewayModel(row.Id, row.ProviderId, row.ModelName, settings, capabilities, row.CreatedAt);
        }
    }

    private static void LoadAgents(SqliteConnection connection, GatewayState state, IAgentDefinitionStore agentDefinitionStore)
    {
        foreach (DbAgent row in connection.Query<DbAgent>(
            """
             SELECT COALESCE(NULLIF(storage_locator_key, ''), id) AS StorageLocatorKey,
                 id AS Id,
                 COALESCE(scope, 'user') AS Scope,
                 COALESCE(enabled, 1) AS Enabled,
                 COALESCE(owner_user_id, '') AS OwnerUserId,
                 created_at AS CreatedAt
            FROM agents ORDER BY rowid
            """))
        {
            AgentDefinition? definition;
            long revision = 0;
            try
            {
                definition = agentDefinitionStore.TryLoadLatestDefinition(row.StorageLocatorKey, out revision);
                if (definition is null)
                {
                    LogMissingAgentStore(state.Logger, row.Id, row.StorageLocatorKey);
                    continue;
                }
            }
            catch (InvalidOperationException exception)
            {
                LogFailedAgentStoreLoad(state.Logger, exception, row.Id, row.StorageLocatorKey);
                continue;
            }
            catch (SqliteException exception)
            {
                LogFailedAgentStoreLoad(state.Logger, exception, row.Id, row.StorageLocatorKey);
                continue;
            }
            catch (JsonException exception)
            {
                LogFailedAgentStoreLoad(state.Logger, exception, row.Id, row.StorageLocatorKey);
                continue;
            }

            definition = ApplyRegistryMetadata(definition, row.Scope, row.Enabled != 0);

            AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(definition);
            var session = SafeCreateJsonObject(DeserializeJsonElement(serialized.SessionJson));
            var planning = new JsonObject
            {
                ["enabled"] = false,
                ["requireSignoff"] = false,
            };
            var sandbox = SafeCreateJsonObject(DeserializeJsonElement(serialized.SandboxJson));
            GatewayAgentMcpBinding[] mcpBindings = [.. (definition.McpBindings ?? []).Select(static binding => new GatewayAgentMcpBinding(
                binding.ConnectorId,
                binding.Context))];
            AgentSkillAttachmentDefinition[] skillAttachments = [.. (definition.SkillAttachments ?? [])];
            JsonObject persona = definition.Persona.DeepClone() as JsonObject ?? new JsonObject();
            var approvalConfig = SafeCreateJsonObject(DeserializeJsonElement(serialized.ApprovalConfigJson));
            var toolExposure = SafeCreateJsonObject(DeserializeJsonElement(serialized.ToolExposureJson));
            JsonObject runtime = GatewayAgentSettingsMapper.ToJsonObject(
                GatewayAgentSettingsMapper.FromDefinition(definition.Runtime));
            state.Agents[row.Id] = new GatewayAgent(
                definition.Id,
                definition.Name,
                definition.ModelId,
                definition.SystemPrompt,
                definition.SuperExecMode,
                session,
                planning,
                sandbox,
                skillAttachments,
                row.OwnerUserId,
                row.CreatedAt,
                approvalConfig,
                toolExposure,
                definition.Scope,
                definition.Enabled,
                persona,
                mcpBindings,
                runtime,
                revision);
            state.AgentStorageLocators[row.Id] = row.StorageLocatorKey;
        }
    }

    private static void LoadMcpServers(SqliteConnection connection, GatewayState state)
    {
        long tableCount = connection.QueryFirstOrDefault<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mcp_servers'");
        if (tableCount == 0) return;

        foreach (DbMcpServer row in connection.Query<DbMcpServer>(
            """
            SELECT id AS Id, name AS Name, description AS Description, icon_url AS IconUrl,
                   preset_id AS PresetId, transport AS Transport, http_url AS HttpUrl,
                     encrypted_env_json AS EncryptedEnvJson, env_keys_json AS EnvKeysJson,
                   container_image AS ContainerImage, container_command AS ContainerCommand,
                   COALESCE(container_args_json, '[]') AS ContainerArgsJson, container_sidecar_name AS ContainerSidecarName,
                   readiness_timeout_seconds AS ReadinessTimeoutSeconds,
                   jit_cooldown_seconds AS JitCooldownSeconds,
                   healthcheck_url AS HealthcheckUrl,
                   healthcheck_interval_milliseconds AS HealthcheckIntervalMilliseconds,
                     enabled AS Enabled, COALESCE(is_internal_system_connector, 0) AS IsInternalSystemConnector, COALESCE(scope, 'system') AS Scope,
                   owner_user_id AS OwnerUserId,
                   created_at AS CreatedAt, updated_at AS UpdatedAt,
                   tool_count AS ToolCount, tool_names_json AS ToolNamesJson,
                                     bundle_path AS BundlePath, server_type AS ServerType,
                                     runtime_artifact_type AS RuntimeArtifactType,
                                     runtime_artifact_source_digest AS RuntimeArtifactSourceDigest,
                                     runtime_artifact_build_status AS RuntimeArtifactBuildStatus,
                                     mcp_protocol_version AS McpProtocolVersion
            FROM mcp_servers ORDER BY rowid
            """))
        {
            string[] containerArgs = DeserializeStringArray(row.ContainerArgsJson);

            IReadOnlyList<string>? toolNames = DeserializeStringList(row.ToolNamesJson);
            IReadOnlyList<string>? envKeys = DeserializeStringList(row.EnvKeysJson);
            state.McpServers[row.Id] = new GatewayMcpServer(
                row.Id, row.Name, row.Description, row.IconUrl,
                row.PresetId, row.Transport, row.HttpUrl, row.ContainerImage, row.ContainerCommand, containerArgs, row.ContainerSidecarName,
                row.BundlePath, row.ServerType,
                row.ReadinessTimeoutSeconds, row.JitCooldownSeconds, row.HealthcheckUrl, row.HealthcheckIntervalMilliseconds,
                row.Enabled != 0, row.IsInternalSystemConnector != 0, row.CreatedAt, row.UpdatedAt, row.Scope, row.OwnerUserId, row.ToolCount, toolNames, envKeys,
                row.RuntimeArtifactType, row.RuntimeArtifactSourceDigest, row.RuntimeArtifactBuildStatus, row.McpProtocolVersion);
        }
    }

    private static void LoadSkills(SqliteConnection connection, GatewayState state)
    {
        long tableCount = connection.QueryFirstOrDefault<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='skills'");
        if (tableCount == 0)
        {
            return;
        }

        foreach (DbSkill row in connection.Query<DbSkill>(
            """
            SELECT id AS Id, name AS Name, description AS Description, package_hash AS PackageHash,
                   status AS Status, package_path AS PackagePath, has_resources AS HasResources,
                   has_scripts AS HasScripts,
                     COALESCE(enabled, 1) AS Enabled,
                     COALESCE(env_keys_json, '[]') AS EnvKeysJson,
                   import_timestamp AS ImportTimestamp,
                     COALESCE(source_metadata_json, '{}') AS SourceMetadataJson,
                     COALESCE(scope, 'system') AS Scope,
                     owner_user_id AS OwnerUserId
            FROM skills ORDER BY rowid
            """))
        {
            Dictionary<string, string> sourceMetadata = JsonSerializer.Deserialize(row.SourceMetadataJson, KautukaJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string>? envKeys = DeserializeStringList(row.EnvKeysJson);
            state.Skills[row.Id] = new GatewaySkill(
                row.Id,
                row.Name,
                row.Description,
                row.PackageHash,
                row.Status,
                row.PackagePath,
                row.HasResources != 0,
                row.HasScripts != 0,
                row.Enabled != 0,
                envKeys,
                row.ImportTimestamp,
                sourceMetadata,
                row.Scope,
                row.OwnerUserId);
        }
    }

    private static GatewayOrganizationSettings LoadOrganizationSettings(SqliteConnection connection)
    {
        string? name = ReadSetting(connection, "workspace.org.name");
        string? industry = ReadSetting(connection, "workspace.org.industry");
        return new GatewayOrganizationSettings { Name = name, Industry = industry };
    }

    private static GatewaySystemSettings LoadSettings(SqliteConnection connection, IConfiguration configuration)
    {
        string logLevel = ReadSetting(connection, "system.log_level") ?? "DEBUG";
        string releaseChannel = NormalizeReleaseChannel(ReadSetting(connection, "updates.release_channel"), "stable");
        string locale = NormalizeLocale(ReadSetting(connection, "system.locale"), "en-US");
        string timeZone = NormalizeTimeZone(ReadSetting(connection, "system.time_zone"), "UTC");
        bool includeBinaryContent = !string.Equals(ReadSetting(connection, "system.observability.include_binary_content"), "false", StringComparison.OrdinalIgnoreCase);
        bool captureMessageContent = string.Equals(ReadSetting(connection, "system.observability.capture_message_content"), "true", StringComparison.OrdinalIgnoreCase);
        string workspaceBaseRoot = WorkspaceHostRootPolicy.ResolveWorkspaceBaseRoot(configuration, configuredBaseRoot: null);
        string? sandboxImage = ReadSetting(connection, SandboxRuntimeKeys.RuntimeImageSettingKey);
        int sandboxCpus = int.TryParse(ReadSetting(connection, SandboxRuntimeKeys.RuntimeDefaultCpuSettingKey), out int c) ? c : 0;
        string? sandboxMemory = ReadSetting(connection, SandboxRuntimeKeys.RuntimeDefaultMemorySettingKey);
        int sandboxTimeout = int.TryParse(ReadSetting(connection, SandboxRuntimeKeys.RuntimeDefaultTimeoutSettingKey), out int t) ? t : 0;
        bool documentIngestionEnabled = string.Equals(ReadSetting(connection, "document_ingestion.enabled"), "true", StringComparison.OrdinalIgnoreCase);
        string internalParserConnectorId = ReadSetting(connection, "document_ingestion.internal_parser_connector_id") ?? "markitdown";
        string internalParserToolName = ReadSetting(connection, "document_ingestion.internal_parser_tool_name") ?? "convert_to_markdown";
        int parserTimeoutSeconds = int.TryParse(ReadSetting(connection, "document_ingestion.parser_timeout_seconds"), out int p) ? p : 30;
        string parseRequiredExtensions = ReadSetting(connection, "document_ingestion.parse_required_extensions") ?? ".pdf,.docx,.pptx,.xlsx";
        bool pdfImageFallbackEnabled = string.Equals(ReadSetting(connection, "document_ingestion.pdf_image_fallback_enabled"), "true", StringComparison.OrdinalIgnoreCase);
        bool visionRouteAllowed = string.Equals(ReadSetting(connection, "document_ingestion.vision_route_allowed"), "true", StringComparison.OrdinalIgnoreCase);
        int modelTimeoutSeconds = int.TryParse(ReadSetting(connection, "runtime.model_timeout_seconds"), out int modelTimeout)
            ? Math.Max(1, modelTimeout)
            : 300;
        int maxRunDurationSeconds = int.TryParse(ReadSetting(connection, "runtime.max_run_duration_seconds"), out int maxRun)
            ? Math.Max(0, maxRun)
            : 0;
        int hitlExpirySeconds = int.TryParse(ReadSetting(connection, "runtime.hitl_expiry_seconds"), out int hitlExpiry)
            ? Math.Max(0, hitlExpiry)
            : 0;
        string hitlExpiryAction = NormalizeHitlExpiryAction(ReadSetting(connection, "runtime.hitl_expiry_action"));
        bool allowMultipleToolCalls = RuntimeSettingKeys.ParseBoolSetting(
            ReadSetting(connection, RuntimeSettingKeys.AllowMultipleToolCalls),
            RuntimeSettingKeys.DefaultAllowMultipleToolCalls);
        bool allowConcurrentInvocation = RuntimeSettingKeys.ParseBoolSetting(
            ReadSetting(connection, RuntimeSettingKeys.AllowConcurrentInvocation),
            RuntimeSettingKeys.DefaultAllowConcurrentInvocation);
        int maxParallelTools = int.TryParse(ReadSetting(connection, RuntimeSettingKeys.MaxParallelTools), out int maxParallel)
            ? RuntimeSettingKeys.ClampMaxParallelTools(maxParallel)
            : RuntimeSettingKeys.DefaultMaxParallelTools;

        return new GatewaySystemSettings
        {
            LogLevel = logLevel,
            ReleaseChannel = releaseChannel,
            Locale = locale,
            TimeZone = timeZone,
            IncludeBinaryContent = includeBinaryContent,
            CaptureMessageContent = captureMessageContent,
            WorkspaceBaseRoot = workspaceBaseRoot,
            SandboxImage = sandboxImage,
            SandboxDefaultCpus = sandboxCpus,
            SandboxDefaultMemory = sandboxMemory,
            SandboxDefaultTimeout = sandboxTimeout,
            DocumentIngestionEnabled = documentIngestionEnabled,
            DocumentIngestionInternalParserConnectorId = internalParserConnectorId,
            DocumentIngestionInternalParserToolName = internalParserToolName,
            DocumentIngestionParserTimeoutSeconds = parserTimeoutSeconds,
            DocumentIngestionParseRequiredExtensions = parseRequiredExtensions,
            DocumentIngestionPdfImageFallbackEnabled = pdfImageFallbackEnabled,
            DocumentIngestionVisionRouteAllowed = visionRouteAllowed,
            ModelTimeoutSeconds = modelTimeoutSeconds,
            MaxRunDurationSeconds = maxRunDurationSeconds,
            HitlExpirySeconds = hitlExpirySeconds,
            HitlExpiryAction = hitlExpiryAction,
            AllowMultipleToolCalls = allowMultipleToolCalls,
            AllowConcurrentInvocation = allowConcurrentInvocation,
            MaxParallelTools = maxParallelTools,
        };
    }

    private static string NormalizeHitlExpiryAction(string? raw)
        => HitlExpiryActions.ToStorage(HitlExpiryActions.Parse(raw));

    private static string NormalizeReleaseChannel(string? candidate, string fallback)
        => string.Equals(candidate?.Trim(), "stable", StringComparison.OrdinalIgnoreCase)
            ? "stable"
            : string.Equals(candidate?.Trim(), "preview", StringComparison.OrdinalIgnoreCase)
                ? "preview"
                : fallback;

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

    private static void LoadUsers(SqliteConnection connection, GatewayState state)
    {
        foreach (DbUser row in connection.Query<DbUser>(
                $$"""
            SELECT username AS Username, user_id AS UserId, role AS Role, password AS Password,
                     is_active AS IsActive, email AS Email, display_name AS DisplayName,
                   COALESCE(can_import_connectors, 0) AS CanImportConnectors,
                   COALESCE(can_import_skills, 0) AS CanImportSkills
            FROM runtime_users
            """))
        {
            state.Users[row.Username] = new GatewayUser(
                Id: row.UserId,
                Username: row.Username,
                Password: row.Password,
                Role: row.Role,
                IsActive: row.IsActive != 0,
                Email: row.Email,
                DisplayName: row.DisplayName,
                CanImportConnectors: row.CanImportConnectors != 0,
                CanImportSkills: row.CanImportSkills != 0);
        }

        LoadExternalSignIn(connection, state);
    }

    private static void LoadExternalSignIn(SqliteConnection connection, GatewayState state)
    {
        using SqliteCommand flags = connection.CreateCommand();
        flags.CommandText = "SELECT user_id FROM runtime_user_password_flags WHERE has_password = 0;";
        using SqliteDataReader flagReader = flags.ExecuteReader();
        var passwordless = new List<string>();
        while (flagReader.Read())
        {
            passwordless.Add(flagReader.GetString(0));
        }

        flagReader.Close();
        foreach (string userId in passwordless)
        {
            state.PasswordlessUserIds[userId] = 1;
            GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
            if (user is not null)
            {
                state.Users[user.Username] = user with { HasPassword = false };
            }
        }

        using SqliteCommand logins = connection.CreateCommand();
        logins.CommandText = "SELECT provider, subject, user_id, email, issuer, tenant_id, object_id FROM external_logins;";
        using SqliteDataReader loginReader = logins.ExecuteReader();
        while (loginReader.Read())
        {
            var link = new ExternalLoginLink(
                loginReader.GetString(0),
                loginReader.GetString(1),
                loginReader.GetString(2),
                loginReader.GetString(3),
                loginReader.IsDBNull(4) ? null : loginReader.GetString(4),
                loginReader.IsDBNull(5) ? null : loginReader.GetString(5),
                loginReader.IsDBNull(6) ? null : loginReader.GetString(6));
            state.ExternalLogins[ExternalLoginLink.Key(link.Provider, link.Subject)] = link;
        }
    }

    private static void LoadSessions(SqliteConnection connection, GatewayState state)
    {
        state.sessions.Clear();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_type, target_id, COALESCE(owner_user_id, ''), title,
                   messages_json, compacted, compact_summary,
                   created_at, updated_at,
                   estimated_context_tokens,
                   last_estimated_at,
                   last_compacted_at,
                   last_compaction_reason
            FROM runtime_sessions ORDER BY updated_at DESC
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string targetType = reader.GetString(1);
            if (string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var messages = DeserializeMessages(reader.GetString(5));
            string sessionId = reader.GetString(0);
            state.sessions[sessionId] = new GatewaySession(
                Id: sessionId,
                TargetType: targetType,
                TargetId: reader.GetString(2),
                OwnerUserId: reader.GetString(3),
                Title: reader.IsDBNull(4) ? "New session" : reader.GetString(4),
                CreatedAt: reader.GetString(8),
                UpdatedAt: reader.GetString(9),
                Messages: messages,
                Compacted: reader.GetInt32(6) != 0,
                CompactSummary: reader.IsDBNull(7) ? null : reader.GetString(7),
                EstimatedContextTokens: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                RawTranscriptEstimatedTokens: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                LastEstimatedAt: reader.IsDBNull(12) ? null : reader.GetString(12),
                LastCompactedAt: reader.IsDBNull(13) ? null : reader.GetString(13),
                LastCompactionReason: reader.IsDBNull(14) ? null : reader.GetString(14));
        }
    }

    private static void LoadRuns(SqliteConnection connection, GatewayState state)
    {
        lock (state.runLock)
        {
            state.runs.Clear();
        }

        IEnumerable<string> rows = connection.Query<string>(
            "SELECT run_json FROM runtime_runs ORDER BY created_at DESC LIMIT 5000");
        lock (state.runLock)
        {
            foreach (string json in rows)
            {
                GatewayRun? run = DeserializeRun(json);
                if (run is not null && !string.Equals(run.TargetType, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    state.runs.Add(run);
                }
            }
        }
    }

    private static List<GatewayMemoryFact> LoadMemoryFacts(SqliteConnection connection)
    {
        return connection.Query<DbMemoryFact>(
            """
            SELECT id AS Id, target_type AS TargetType, target_id AS TargetId, COALESCE(owner_user_id, '') AS OwnerUserId, title AS Title,
                   content AS Content, category AS Category, source AS Source, confidence AS Confidence,
                   created_at AS CreatedAt, updated_at AS UpdatedAt, session_id AS SessionId
            FROM runtime_memory_facts
            """)
            .Where(static r => !string.Equals(r.TargetType, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(static r => new GatewayMemoryFact(
                r.Id, r.TargetType, r.TargetId, r.OwnerUserId, r.Title, r.Content, r.Category,
                r.Source, r.Confidence, r.CreatedAt, r.UpdatedAt, r.SessionId))
            .ToList();
    }

    private static void LoadAgentMemoryFacts(SqliteConnection connection, GatewayState state)
    {
        _ = connection;
        foreach ((string agentId, string storageLocatorKey) in state.AgentStorageLocators)
        {
            state.MemoryStore.AddRange("agent", agentId, state.AgentMemoryFactRepository.ListMemoryFacts(storageLocatorKey));
        }
    }

    private static void LoadAgentSessionsAndRuns(SqliteConnection connection, GatewayState state)
    {
        _ = connection;
        foreach ((string agentId, string storageLocatorKey) in state.AgentStorageLocators)
        {
            IReadOnlyList<GatewaySession> sessions = state.AgentRuntimeSessionRepository.ListRuntimeSessions(storageLocatorKey);
            IReadOnlyList<GatewayRun> runs = state.AgentRuntimeRunRepository.ListRuntimeRuns(storageLocatorKey);

            foreach (GatewaySession session in sessions)
            {
                state.sessions[session.Id] = session;
            }

            lock (state.runLock)
            {
                state.runs.AddRange(runs);
            }
        }
    }

    private static AgentDefinition ApplyRegistryMetadata(AgentDefinition definition, string scope, bool enabled)
        => new()
        {
            Id = definition.Id,
            Scope = scope,
            Enabled = enabled,
            Name = definition.Name,
            ModelId = definition.ModelId,
            SystemPrompt = definition.SystemPrompt,
            SuperExecMode = definition.SuperExecMode,
            ApprovalPolicy = definition.ApprovalPolicy,
            Session = definition.Session,
            Sandbox = definition.Sandbox,
            McpBindings = definition.McpBindings,
            SkillAttachments = definition.SkillAttachments,
            Persona = definition.Persona,
            ToolExposure = definition.ToolExposure,
            Runtime = definition.Runtime,
        };

}
