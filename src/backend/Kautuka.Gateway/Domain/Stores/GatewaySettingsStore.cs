using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.UpdateService.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewaySettingsStore : IGatewaySettingsStore
{
    private const string personaDefinitionsKey = "agent.persona.definitions";
    private readonly GatewayState state;
    private readonly KautukaLogLevelSwitch logLevelSwitch;
    private readonly IWorkspaceDesiredStatePublisher desiredStatePublisher;
    private readonly IUpdateServiceGrpcService updateServiceGrpcService;
    private readonly IConfiguration configuration;
    private readonly IPlatformConfigurationProvider configurationProvider;
    private readonly Kautuka.Gateway.Infrastructure.AI.IProviderTypeCatalog providerTypeCatalog;

    public GatewaySettingsStore(GatewayState state, KautukaLogLevelSwitch logLevelSwitch, IWorkspaceDesiredStatePublisher desiredStatePublisher, IUpdateServiceGrpcService updateServiceGrpcService, IConfiguration configuration, IPlatformConfigurationProvider configurationProvider)
        : this(
            state,
            logLevelSwitch,
            desiredStatePublisher,
            updateServiceGrpcService,
            configuration,
            configurationProvider,
            new Kautuka.Gateway.Infrastructure.AI.SeededProviderTypeCatalog())
    {
    }

    public GatewaySettingsStore(GatewayState state, KautukaLogLevelSwitch logLevelSwitch, IWorkspaceDesiredStatePublisher desiredStatePublisher, IUpdateServiceGrpcService updateServiceGrpcService, IConfiguration configuration, IPlatformConfigurationProvider configurationProvider, Kautuka.Gateway.Infrastructure.AI.IProviderTypeCatalog providerTypeCatalog)
    {
        this.state = state;
        this.logLevelSwitch = logLevelSwitch;
        this.desiredStatePublisher = desiredStatePublisher;
        this.updateServiceGrpcService = updateServiceGrpcService;
        this.configuration = configuration;
        this.configurationProvider = configurationProvider;
        this.providerTypeCatalog = providerTypeCatalog;
    }

    public void Save(JsonElement body)
        => SaveAsync(body).GetAwaiter().GetResult();

    public async Task SaveAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        string logLevel = state.SystemSettings.LogLevel;
        string releaseChannel = state.SystemSettings.ReleaseChannel;
        string locale = state.SystemSettings.Locale;
        string timeZone = state.SystemSettings.TimeZone;
        bool includeBinaryContent = state.SystemSettings.IncludeBinaryContent;
        bool captureMessageContent = state.SystemSettings.CaptureMessageContent;
        string? sandboxImage = state.SystemSettings.SandboxImage;
        int sandboxCpus = state.SystemSettings.SandboxDefaultCpus;
        string? sandboxMemory = state.SystemSettings.SandboxDefaultMemory;
        int sandboxTimeout = state.SystemSettings.SandboxDefaultTimeout;
        bool documentIngestionEnabled = state.SystemSettings.DocumentIngestionEnabled;
        string documentIngestionInternalParserConnectorId = state.SystemSettings.DocumentIngestionInternalParserConnectorId;
        string documentIngestionInternalParserToolName = state.SystemSettings.DocumentIngestionInternalParserToolName;
        int documentIngestionParserTimeoutSeconds = state.SystemSettings.DocumentIngestionParserTimeoutSeconds;
        string documentIngestionParseRequiredExtensions = state.SystemSettings.DocumentIngestionParseRequiredExtensions;
        bool documentIngestionPdfImageFallbackEnabled = state.SystemSettings.DocumentIngestionPdfImageFallbackEnabled;
        bool documentIngestionVisionRouteAllowed = state.SystemSettings.DocumentIngestionVisionRouteAllowed;
        int modelTimeoutSeconds = state.SystemSettings.ModelTimeoutSeconds;
        int maxRunDurationSeconds = state.SystemSettings.MaxRunDurationSeconds;
        int hitlExpirySeconds = state.SystemSettings.HitlExpirySeconds;
        string hitlExpiryAction = state.SystemSettings.HitlExpiryAction;
        bool allowMultipleToolCalls = state.SystemSettings.AllowMultipleToolCalls;
        bool allowConcurrentInvocation = state.SystemSettings.AllowConcurrentInvocation;
        int maxParallelTools = state.SystemSettings.MaxParallelTools;

        if (body.TryGetProperty("system", out JsonElement system) && system.ValueKind == JsonValueKind.Object)
        {
            logLevel = system.GetStringProperty("logLevel", logLevel).ToUpperInvariant();
            if (system.TryGetProperty("updates", out JsonElement updates) && updates.ValueKind == JsonValueKind.Object)
            {
                releaseChannel = NormalizeReleaseChannel(
                    updates.GetStringProperty("releaseChannel", releaseChannel),
                    releaseChannel);
            }

            if (system.TryGetProperty("observability", out JsonElement observability) && observability.ValueKind == JsonValueKind.Object)
            {
                includeBinaryContent = observability.GetBoolProperty("includeBinaryContent", includeBinaryContent);
                captureMessageContent = observability.GetBoolProperty("captureMessageContent", captureMessageContent);
            }

            if (system.TryGetProperty("runtimeGrounding", out JsonElement runtimeGrounding)
                && runtimeGrounding.ValueKind == JsonValueKind.Object)
            {
                locale = ValidateLocale(runtimeGrounding.GetStringProperty("locale", locale));
                timeZone = ValidateTimeZone(runtimeGrounding.GetStringProperty("timeZone", timeZone));
            }

            if (system.TryGetProperty("runtimeLimits", out JsonElement runtimeLimits)
                && runtimeLimits.ValueKind == JsonValueKind.Object)
            {
                modelTimeoutSeconds = Math.Max(1, runtimeLimits.GetIntProperty("modelTimeoutSeconds", modelTimeoutSeconds));
                maxRunDurationSeconds = Math.Max(0, runtimeLimits.GetIntProperty("maxRunDurationSeconds", maxRunDurationSeconds));
                hitlExpirySeconds = Math.Max(0, runtimeLimits.GetIntProperty("hitlExpirySeconds", hitlExpirySeconds));
                hitlExpiryAction = NormalizeHitlExpiryAction(
                    runtimeLimits.GetStringProperty("hitlExpiryAction", hitlExpiryAction));
                allowMultipleToolCalls = runtimeLimits.GetBoolProperty(
                    "allowMultipleToolCalls", allowMultipleToolCalls);
                allowConcurrentInvocation = runtimeLimits.GetBoolProperty(
                    "allowConcurrentInvocation", allowConcurrentInvocation);
                maxParallelTools = RuntimeSettingKeys.ClampMaxParallelTools(
                    runtimeLimits.GetIntProperty("maxParallelTools", maxParallelTools));
            }
        }

        if (body.TryGetProperty("kautuka", out JsonElement kautuka) && kautuka.ValueKind == JsonValueKind.Object)
        {
            if (kautuka.TryGetProperty("sandbox", out JsonElement sandbox) && sandbox.ValueKind == JsonValueKind.Object)
            {
                sandboxImage = sandbox.GetNullableStringProperty("image") ?? sandboxImage;
                sandboxCpus = sandbox.GetIntProperty("defaultCpus", sandboxCpus);
                sandboxMemory = sandbox.GetNullableStringProperty("defaultMemory") ?? sandboxMemory;
                sandboxTimeout = sandbox.GetIntProperty("defaultTimeout", sandboxTimeout);
            }

            if (kautuka.TryGetProperty("documentIngestion", out JsonElement documentIngestion)
                && documentIngestion.ValueKind == JsonValueKind.Object)
            {
                documentIngestionEnabled = documentIngestion.GetBoolProperty("enabled", documentIngestionEnabled);
                documentIngestionInternalParserConnectorId = documentIngestion.GetStringProperty("internalParserConnectorId", documentIngestionInternalParserConnectorId);
                documentIngestionInternalParserToolName = documentIngestion.GetStringProperty("internalParserToolName", documentIngestionInternalParserToolName);
                documentIngestionParserTimeoutSeconds = Math.Max(1, documentIngestion.GetIntProperty("parserTimeoutSeconds", documentIngestionParserTimeoutSeconds));
                documentIngestionParseRequiredExtensions = documentIngestion.GetStringProperty("parseRequiredExtensions", documentIngestionParseRequiredExtensions);
                documentIngestionPdfImageFallbackEnabled = documentIngestion.GetBoolProperty("pdfImageFallbackEnabled", documentIngestionPdfImageFallbackEnabled);
                documentIngestionVisionRouteAllowed = documentIngestion.GetBoolProperty("visionRouteAllowed", documentIngestionVisionRouteAllowed);
            }
        }

        bool updatePolicyChanged = !string.Equals(releaseChannel, state.SystemSettings.ReleaseChannel, StringComparison.Ordinal);
        if (updatePolicyChanged)
        {
            UpdateStatusResponse policyResponse = await updateServiceGrpcService.SetUpdatePolicyAsync(
                new UpdatePolicyRequest { ReleaseChannel = releaseChannel },
                cancellationToken).ConfigureAwait(false);

            if (!policyResponse.Ok)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(policyResponse.LastError)
                    ? policyResponse.Message
                    : policyResponse.LastError);
            }
        }

        var newSettings = new GatewaySystemSettings
        {
            LogLevel = logLevel,
            ReleaseChannel = releaseChannel,
            Locale = locale,
            TimeZone = timeZone,
            IncludeBinaryContent = includeBinaryContent,
            CaptureMessageContent = captureMessageContent,
            WorkspaceBaseRoot = WorkspaceHostRootPolicy.ResolveWorkspaceBaseRoot(configuration, configuredBaseRoot: null),
            SandboxImage = sandboxImage,
            SandboxDefaultCpus = sandboxCpus,
            SandboxDefaultMemory = sandboxMemory,
            SandboxDefaultTimeout = sandboxTimeout,
            DocumentIngestionEnabled = documentIngestionEnabled,
            DocumentIngestionInternalParserConnectorId = documentIngestionInternalParserConnectorId,
            DocumentIngestionInternalParserToolName = documentIngestionInternalParserToolName,
            DocumentIngestionParserTimeoutSeconds = documentIngestionParserTimeoutSeconds,
            DocumentIngestionParseRequiredExtensions = documentIngestionParseRequiredExtensions,
            DocumentIngestionPdfImageFallbackEnabled = documentIngestionPdfImageFallbackEnabled,
            DocumentIngestionVisionRouteAllowed = documentIngestionVisionRouteAllowed,
            ModelTimeoutSeconds = modelTimeoutSeconds,
            MaxRunDurationSeconds = maxRunDurationSeconds,
            HitlExpirySeconds = hitlExpirySeconds,
            HitlExpiryAction = hitlExpiryAction,
            AllowMultipleToolCalls = allowMultipleToolCalls,
            AllowConcurrentInvocation = allowConcurrentInvocation,
            MaxParallelTools = maxParallelTools,
        };

        state.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["system.log_level"] = logLevel,
            ["system.locale"] = locale,
            ["system.time_zone"] = timeZone,
            ["updates.release_channel"] = releaseChannel,
            ["system.observability.include_binary_content"] = includeBinaryContent ? "true" : "false",
            ["system.observability.capture_message_content"] = captureMessageContent ? "true" : "false",
            ["infra.sandbox.image"] = sandboxImage ?? string.Empty,
            ["infra.sandbox.default_cpus"] = sandboxCpus.ToString(CultureInfo.InvariantCulture),
            ["infra.sandbox.default_memory"] = sandboxMemory ?? string.Empty,
            ["infra.sandbox.default_timeout"] = sandboxTimeout.ToString(CultureInfo.InvariantCulture),
            ["document_ingestion.enabled"] = documentIngestionEnabled ? "true" : "false",
            ["document_ingestion.internal_parser_connector_id"] = documentIngestionInternalParserConnectorId,
            ["document_ingestion.internal_parser_tool_name"] = documentIngestionInternalParserToolName,
            ["document_ingestion.parser_timeout_seconds"] = documentIngestionParserTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["document_ingestion.parse_required_extensions"] = documentIngestionParseRequiredExtensions,
            ["document_ingestion.pdf_image_fallback_enabled"] = documentIngestionPdfImageFallbackEnabled ? "true" : "false",
            ["document_ingestion.vision_route_allowed"] = documentIngestionVisionRouteAllowed ? "true" : "false",
            ["runtime.model_timeout_seconds"] = modelTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["runtime.max_run_duration_seconds"] = maxRunDurationSeconds.ToString(CultureInfo.InvariantCulture),
            ["runtime.hitl_expiry_seconds"] = hitlExpirySeconds.ToString(CultureInfo.InvariantCulture),
            ["runtime.hitl_expiry_action"] = hitlExpiryAction,
            ["runtime.allow_multiple_tool_calls"] = allowMultipleToolCalls ? "true" : "false",
            ["runtime.allow_concurrent_invocation"] = allowConcurrentInvocation ? "true" : "false",
            ["runtime.max_parallel_tools"] = maxParallelTools.ToString(CultureInfo.InvariantCulture),
        });

        state.SystemSettings = newSettings;
        GenAiTelemetryEnvironment.ApplyCaptureMessageContent(captureMessageContent);
        logLevelSwitch.SetLevel(KautukaLogLevelSwitch.Parse(logLevel, LogLevel.Warning));
        await desiredStatePublisher.TryPublishCurrentAsync("system-settings-save", cancellationToken).ConfigureAwait(false);
    }

    public void SaveProviderPathPolicySettings(JsonElement body)
    {
        state.PersistRawSettingsAuthoritatively(BuildProviderPathPolicyRawSettings(body));
    }

    public void SaveMemorySettings(JsonElement body)
    {
        state.PersistRawSettingsAuthoritatively(BuildMemoryRawSettings(body));
    }

    public GatewaySystemSettingsResponse GetPayload() => new(
        new GatewaySystemSettingsPayload(
            state.SystemSettings.LogLevel,
            new GatewayObservabilitySettingsPayload(
                state.SystemSettings.IncludeBinaryContent,
                state.SystemSettings.CaptureMessageContent),
            new GatewayUpdateSettingsPayload(
                state.SystemSettings.ReleaseChannel),
            new GatewayRuntimeGroundingSettingsPayload(
                state.SystemSettings.Locale,
                state.SystemSettings.TimeZone),
            new GatewayRuntimeLimitsSettingsPayload(
                state.SystemSettings.ModelTimeoutSeconds,
                state.SystemSettings.MaxRunDurationSeconds,
                state.SystemSettings.HitlExpirySeconds,
                state.SystemSettings.HitlExpiryAction,
                state.SystemSettings.AllowMultipleToolCalls,
                state.SystemSettings.AllowConcurrentInvocation,
                state.SystemSettings.MaxParallelTools)),
        new GatewayKautukaInfrastructureSettingsPayload(
            new GatewayWorkspaceSettingsPayload(state.SystemSettings.WorkspaceBaseRoot),
            new GatewaySandboxSettingsPayload(
                state.SystemSettings.SandboxImage,
                state.SystemSettings.SandboxDefaultCpus,
                state.SystemSettings.SandboxDefaultMemory,
                state.SystemSettings.SandboxDefaultTimeout),
            new GatewayDocumentIngestionSettingsPayload(
                state.SystemSettings.DocumentIngestionEnabled,
                state.SystemSettings.DocumentIngestionInternalParserConnectorId,
                state.SystemSettings.DocumentIngestionInternalParserToolName,
                state.SystemSettings.DocumentIngestionParserTimeoutSeconds,
                state.SystemSettings.DocumentIngestionParseRequiredExtensions,
                state.SystemSettings.DocumentIngestionPdfImageFallbackEnabled,
                state.SystemSettings.DocumentIngestionVisionRouteAllowed)));

    public GatewayAdvancedSettingsResponse GetAdvancedSettingsPayload()
    {
        GatewaySystemSettingsResponse payload = GetPayload();
        return new GatewayAdvancedSettingsResponse(
            payload.System,
            payload.Kautuka,
            GetPersonaDefinitions(),
            GetProviderPathPolicyPayload(),
            GetMemorySettingsPayload(),
            [.. providerTypeCatalog.ListCatalogEntries()
                .Select(static entry => new GatewayProviderCatalogItem(
                    entry.Type,
                    entry.FriendlyName,
                    entry.DefaultBaseUrl,
                    entry.DefaultHttpTimeoutSeconds,
                    entry.RequiresApiKey,
                    entry.RequiresBaseUrl,
                    entry.SupportsHttpTimeoutSeconds,
                    entry.IsBuiltIn,
                    entry.SeedVersion))]);
    }

    public async Task SaveAdvancedSettingsAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        JsonObject? normalizedPersonaDefinitions = null;
        Dictionary<string, string> rawSettings = new(StringComparer.Ordinal);

        if (body.TryGetProperty("personaDefinitions", out JsonElement personaDefinitions)
            && personaDefinitions.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            normalizedPersonaDefinitions = NormalizePersonaDefinitionsPayload(personaDefinitions);
        }

        if (body.TryGetProperty("providerPathPolicy", out JsonElement providerPathPolicy)
            && providerPathPolicy.ValueKind == JsonValueKind.Object)
        {
            AppendRawSettings(rawSettings, BuildProviderPathPolicyRawSettings(providerPathPolicy));
        }

        if (body.TryGetProperty("memory", out JsonElement memory)
            && memory.ValueKind == JsonValueKind.Object)
        {
            AppendRawSettings(rawSettings, BuildMemoryRawSettings(memory));
        }

        if (body.TryGetProperty("providerTypeCatalog", out JsonElement providerTypeCatalogElement)
            && providerTypeCatalogElement.ValueKind == JsonValueKind.Array)
        {
            providerTypeCatalog.SaveCatalogEntries(BuildProviderCatalogEntries(providerTypeCatalogElement));
        }

        JsonObject? systemBody = CreateSystemSettingsBody(body);
        if (systemBody is not null)
        {
            await SaveAsync(
                JsonSerializer.SerializeToElement(systemBody, KautukaJsonContext.Default.JsonObject),
                cancellationToken).ConfigureAwait(false);
        }

        if (normalizedPersonaDefinitions is not null)
        {
            SaveRawJsonSetting(personaDefinitionsKey, normalizedPersonaDefinitions.ToJsonString());
        }

        if (rawSettings.Count > 0)
        {
            state.PersistRawSettingsAuthoritatively(rawSettings);
        }
    }

    private List<Kautuka.Gateway.Infrastructure.AI.ProviderCatalogEntry> BuildProviderCatalogEntries(JsonElement providerTypeCatalogElement)
    {
        Dictionary<string, Kautuka.Gateway.Infrastructure.AI.ProviderCatalogEntry> currentEntries = providerTypeCatalog
            .ListCatalogEntries()
            .ToDictionary(static entry => entry.Type, StringComparer.OrdinalIgnoreCase);

        var updates = new List<Kautuka.Gateway.Infrastructure.AI.ProviderCatalogEntry>();
        foreach (JsonElement entryElement in providerTypeCatalogElement.EnumerateArray())
        {
            string type = entryElement.GetStringProperty("type", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ArgumentException("Provider catalog entry type is required.");
            }

            if (!currentEntries.TryGetValue(type, out Kautuka.Gateway.Infrastructure.AI.ProviderCatalogEntry? currentEntry))
            {
                throw new ArgumentException($"Unknown provider catalog entry '{type}'.");
            }

            if (!currentEntry.IsBuiltIn)
            {
                throw new ArgumentException($"Provider catalog entry '{type}' is not editable.");
            }

            string friendlyName = entryElement.GetStringProperty("friendlyName", currentEntry.FriendlyName).Trim();
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                throw new ArgumentException($"Provider catalog entry '{type}' must have a friendly name.");
            }

            string? defaultBaseUrl = entryElement.TryGetProperty("defaultBaseUrl", out JsonElement baseUrlElement)
                ? baseUrlElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : string.IsNullOrWhiteSpace(baseUrlElement.GetString()) ? null : baseUrlElement.GetString()!.Trim()
                : currentEntry.DefaultBaseUrl;

            int? defaultHttpTimeoutSeconds = currentEntry.DefaultHttpTimeoutSeconds;
            if (entryElement.TryGetProperty("defaultHttpTimeoutSeconds", out JsonElement timeoutElement))
            {
                defaultHttpTimeoutSeconds = timeoutElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : timeoutElement.GetInt32();
                if (defaultHttpTimeoutSeconds is <= 0)
                {
                    throw new ArgumentException($"Provider catalog entry '{type}' must have a positive default timeout when set.");
                }
            }

            updates.Add(currentEntry with
            {
                FriendlyName = friendlyName,
                DefaultBaseUrl = defaultBaseUrl,
                DefaultHttpTimeoutSeconds = defaultHttpTimeoutSeconds,
            });
        }

        return updates;
    }

    public GatewayProviderPathPolicySettingsResponse GetProviderPathPolicyPayload()
    {
        GatewayProviderPathPolicySettings settings = GatewayProviderPathPolicySettings.FromConfiguration(configurationProvider.GetCurrent());
        return new GatewayProviderPathPolicySettingsResponse(
            settings.ToolNativePromotionThreshold,
            settings.ToolMinObservations,
            settings.ToolNativeDemotionThreshold,
            settings.StructuredJsonStrictPromotionThreshold,
            settings.StructuredJsonMinObservations,
            settings.VisionEnableThreshold,
            settings.ObservationDecayWindowDays,
            settings.ProviderPathFallbackModeEnabled);
    }

    public GatewayMemorySettingsResponse GetMemorySettingsPayload()
    {
        GatewayMemorySettings settings = GatewayMemorySettings.FromConfiguration(configurationProvider.GetCurrent());
        return new GatewayMemorySettingsResponse(
            settings.EmbeddingsEnabled,
            settings.FactPersistThreshold,
            settings.FactInjectThreshold,
            settings.MaxInjectedFacts,
            settings.InjectionTokenBudget,
            settings.SessionCompactionModelId);
    }

    private static string NormalizeReleaseChannel(string? candidate, string fallback)
        => string.Equals(candidate?.Trim(), "stable", StringComparison.OrdinalIgnoreCase)
            ? "stable"
            : string.Equals(candidate?.Trim(), "preview", StringComparison.OrdinalIgnoreCase)
                ? "preview"
                : fallback;

    private static string NormalizeHitlExpiryAction(string? candidate)
        => HitlExpiryActions.ToStorage(HitlExpiryActions.Parse(candidate));

    private static string ValidateLocale(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("System locale is required.");
        }

        try
        {
            return CultureInfo.GetCultureInfo(candidate.Trim()).Name;
        }
        catch (CultureNotFoundException ex)
        {
            throw new InvalidOperationException($"Unsupported locale '{candidate}'.", ex);
        }
    }

    private static string ValidateTimeZone(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("System time zone is required.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(candidate.Trim()).Id;
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException($"Unsupported time zone '{candidate}'.", ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException($"Invalid time zone '{candidate}'.", ex);
        }
    }

    public GatewayPersonaDefinitionsResponse GetPersonaDefinitionsPayload()
        => new(GetPersonaDefinitions());

    public void SavePersonaDefinitions(JsonElement body)
    {
        JsonObject definitions = NormalizePersonaDefinitionsPayload(body);
        SaveRawJsonSetting(personaDefinitionsKey, definitions.ToJsonString());
    }

    public GatewayPersonaProfilesResponse GetPersonaProfilesPayload()
    {
        JsonObject profiles = new();
        foreach ((string agentId, GatewayAgent agent) in state.Agents)
        {
            profiles[agentId] = ResolveAgentPersonaProfile(agent.Persona);
        }

        return new(profiles);
    }

    public JsonObject GetAgentPersonaProfile(string agentId)
    {
        if (state.Agents.TryGetValue(agentId, out GatewayAgent? agent) && agent is not null)
        {
            return ResolveAgentPersonaProfile(agent.Persona);
        }

        return ResolveAgentPersonaProfile(null);
    }

    public JsonObject NormalizeAgentPersonaProfile(JsonObject? profile)
    {
        JsonObject definitions = GetPersonaDefinitions();
        return GatewayPersonaProfileMapper.NormalizeStoredProfile(profile, definitions);
    }

    public JsonObject ResolveAgentPersonaProfile(JsonObject? profile)
    {
        try
        {
            return NormalizeAgentPersonaProfile(profile);
        }
        catch (ArgumentException)
        {
            return GatewayPersonaProfileMapper.BuildDefaultProfile(GetPersonaDefinitions());
        }
    }

    public GatewayOrganizationSettingsResponse GetOrganizationPayload()
        => new(new GatewayOrganizationSettingsPayload(
            state.OrganizationSettings.Name,
            state.OrganizationSettings.Industry));

    public void SaveOrganization(JsonElement body)
    {
        string? name = body.TryGetProperty("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(nameEl.GetString()) ? null : nameEl.GetString()!.Trim())
            : state.OrganizationSettings.Name;
        string? industry = body.TryGetProperty("industry", out JsonElement industryEl) && industryEl.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(industryEl.GetString()) ? null : industryEl.GetString()!.Trim())
            : state.OrganizationSettings.Industry;

        state.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace.org.name"] = name ?? string.Empty,
            ["workspace.org.industry"] = industry ?? string.Empty,
        });

        state.OrganizationSettings = new GatewayOrganizationSettings { Name = name, Industry = industry };
    }

    private JsonObject GetPersonaDefinitions()
    {
        string? raw = LoadRawJsonSetting(personaDefinitionsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return GatewayPersonaProfileMapper.CreateDefaultDefinitions();
        }

        try
        {
            JsonObject parsed = JsonNode.Parse(raw) as JsonObject ?? GatewayPersonaProfileMapper.CreateDefaultDefinitions();
            return GatewayPersonaProfileMapper.ValidateAndNormalizeDefinitions(parsed);
        }
        catch (JsonException)
        {
            return GatewayPersonaProfileMapper.CreateDefaultDefinitions();
        }
        catch (ArgumentException)
        {
            return GatewayPersonaProfileMapper.CreateDefaultDefinitions();
        }
    }

    public string? ReadSettingJson(string key)
        => LoadRawJsonSetting(key);

    public void WriteSettingJson(string key, string rawJson)
        => SaveRawJsonSetting(key, rawJson);

    private string? LoadRawJsonSetting(string key)
    {
        using var connection = new SqliteConnection(state.Database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM system_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);

        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            return null;
        }

        string raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                return doc.RootElement.ValueKind == JsonValueKind.String
                    ? doc.RootElement.GetString()
                    : raw[1..^1];
            }
            catch (JsonException)
            {
                return raw[1..^1];
            }
        }

        return raw;
    }

    private static void AppendRawSettings(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach ((string key, string value) in source)
        {
            target[key] = value;
        }
    }

    private static Dictionary<string, string> BuildProviderPathPolicyRawSettings(JsonElement body)
    {
        var defaults = new GatewayProviderPathPolicySettings();

        double toolNativePromotion = body.GetDoubleProperty(
            "toolNativePromotionThreshold",
            body.GetDoubleProperty("tool_native_promotion_threshold", defaults.ToolNativePromotionThreshold));
        int toolMinObservations = body.GetIntProperty(
            "toolMinObservations",
            body.GetIntProperty("tool_min_observations", defaults.ToolMinObservations));
        double toolNativeDemotion = body.GetDoubleProperty(
            "toolNativeDemotionThreshold",
            body.GetDoubleProperty("tool_native_demotion_threshold", defaults.ToolNativeDemotionThreshold));
        double structuredJsonPromotion = body.GetDoubleProperty(
            "structuredJsonStrictPromotionThreshold",
            body.GetDoubleProperty("structured_json_strict_promotion_threshold", defaults.StructuredJsonStrictPromotionThreshold));
        int structuredJsonMinObs = body.GetIntProperty(
            "structuredJsonMinObservations",
            body.GetIntProperty("structured_json_min_observations", defaults.StructuredJsonMinObservations));
        double visionEnable = body.GetDoubleProperty(
            "visionEnableThreshold",
            body.GetDoubleProperty("vision_enable_threshold", defaults.VisionEnableThreshold));
        int observationDecayDays = body.GetIntProperty(
            "observationDecayWindowDays",
            body.GetIntProperty("observation_decay_window_days", defaults.ObservationDecayWindowDays));
        bool fallbackEnabled = body.GetBoolProperty(
            "providerPathFallbackModeEnabled",
            body.GetBoolProperty("provider_path_fallback_mode_enabled", defaults.ProviderPathFallbackModeEnabled));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider_path.tool_native_promotion_threshold"] = toolNativePromotion.ToString(CultureInfo.InvariantCulture),
            ["provider_path.tool_min_observations"] = toolMinObservations.ToString(CultureInfo.InvariantCulture),
            ["provider_path.tool_native_demotion_threshold"] = toolNativeDemotion.ToString(CultureInfo.InvariantCulture),
            ["provider_path.structured_json_strict_promotion_threshold"] = structuredJsonPromotion.ToString(CultureInfo.InvariantCulture),
            ["provider_path.structured_json_min_observations"] = structuredJsonMinObs.ToString(CultureInfo.InvariantCulture),
            ["provider_path.vision_enable_threshold"] = visionEnable.ToString(CultureInfo.InvariantCulture),
            ["provider_path.observation_decay_window_days"] = observationDecayDays.ToString(CultureInfo.InvariantCulture),
            ["provider_path.fallback_mode_enabled"] = fallbackEnabled ? "true" : "false",
        };
    }

    private static Dictionary<string, string> BuildMemoryRawSettings(JsonElement body)
    {
        var defaults = new GatewayMemorySettings();

        bool embeddingsEnabled = body.GetBoolProperty(
            "embeddingsEnabled",
            body.GetBoolProperty("embeddings_enabled", defaults.EmbeddingsEnabled));
        double persistThreshold = body.GetDoubleProperty(
            "factPersistThreshold",
            body.GetDoubleProperty("fact_persist_threshold", defaults.FactPersistThreshold));
        double injectThreshold = body.GetDoubleProperty(
            "factInjectThreshold",
            body.GetDoubleProperty("fact_inject_threshold", defaults.FactInjectThreshold));
        int maxInjectedFacts = body.GetIntProperty(
            "maxInjectedFacts",
            body.GetIntProperty("max_injected_facts", defaults.MaxInjectedFacts));
        int injectionTokenBudget = body.GetIntProperty(
            "injectionTokenBudget",
            body.GetIntProperty("injection_token_budget", defaults.InjectionTokenBudget));
        string sessionCompactionModelId = body.GetStringProperty(
            "sessionCompactionModelId",
            body.GetStringProperty("session_compaction_model_id", defaults.SessionCompactionModelId ?? string.Empty));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memory.embeddings_enabled"] = embeddingsEnabled ? "true" : "false",
            ["memory.fact_persist_threshold"] = persistThreshold.ToString(CultureInfo.InvariantCulture),
            ["memory.fact_inject_threshold"] = injectThreshold.ToString(CultureInfo.InvariantCulture),
            ["memory.max_injected_facts"] = maxInjectedFacts.ToString(CultureInfo.InvariantCulture),
            ["memory.injection_token_budget"] = injectionTokenBudget.ToString(CultureInfo.InvariantCulture),
            ["memory.session_compaction_model_id"] = sessionCompactionModelId.Trim(),
        };
    }

    private static JsonObject? CreateSystemSettingsBody(JsonElement body)
    {
        JsonObject payload = new();

        if (body.TryGetProperty("system", out JsonElement system) && system.ValueKind == JsonValueKind.Object)
        {
            payload["system"] = JsonNode.Parse(system.GetRawText());
        }

        if (body.TryGetProperty("kautuka", out JsonElement kautuka) && kautuka.ValueKind == JsonValueKind.Object)
        {
            payload["kautuka"] = JsonNode.Parse(kautuka.GetRawText());
        }

        return payload.Count > 0 ? payload : null;
    }

    private static JsonObject NormalizePersonaDefinitionsPayload(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Persona definitions payload must be a JSON object.", nameof(body));
        }

        return GatewayPersonaProfileMapper.ValidateAndNormalizeDefinitions(
            JsonObject.Create(body) ?? GatewayPersonaProfileMapper.CreateDefaultDefinitions());
    }

    private void SaveRawJsonSetting(string key, string rawJson)
    {
        using var connection = new SqliteConnection(state.Database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO system_settings (key, value_json, updated_at) VALUES ($key, $valueJson, $updatedAt) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$valueJson", rawJson);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

}
