// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Infrastructure/Gateway/GatewayJsonModels.cs | Namespace: Kautuka.Tui.Infrastructure.Gateway
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kautuka.Tui.Infrastructure.Gateway;

public sealed record AuthLoginRequest(string Username, string Password);

public sealed record AuthLoginResponse(string AccessToken, string TokenType, GatewayUserPayload User);

public sealed record GatewayBooleanSuccessResponse(bool Success);

public sealed record GatewayDeletedResponse(bool Deleted);

public sealed record GatewayErrorResponse(string Error);

public sealed record GatewayAutopilotSchedule(
    string Id,
    string OwnerUserId,
    string TargetType,
    string TargetId,
    string CronExpression,
    string TimeZoneId,
    string Mode,
    bool RequiresPreRunApproval,
    string SchedulePromptContext,
    bool NotifyUiInbox,
    bool NotifyChannels,
    bool IsPaused,
    string CreatedAt,
    string UpdatedAt,
    string? NextDueAt,
    string? LastEvaluatedAt,
    string? LeaseOwner,
    string? LeaseExpiresAt);

public sealed record GatewayScheduledRun(
    string Id,
    string ScheduleId,
    string OwnerUserId,
    string TargetType,
    string TargetId,
    string Status,
    string TriggerType,
    string ScheduledFor,
    string? SessionId,
    string? RuntimeRunId,
    string? PreRunApprovalRequestId,
    string? InputJson,
    string? OutputJson,
    string? Error,
    string CreatedAt,
    string UpdatedAt,
    string? StartedAt,
    string? EndedAt);

public sealed record GatewayAutopilotNotification(
    string Id,
    string ScheduledRunId,
    string OwnerUserId,
    string ChannelType,
    string Destination,
    string Status,
    string? PayloadJson,
    string CreatedAt,
    string? SentAt,
    string? Error,
    bool IsRead,
    string? ReadAt);

public sealed record GatewayProviderTypeItem(string Type, string FriendlyName, bool RequiresApiKey, bool RequiresBaseUrl);

public sealed record GatewayProviderTypesResponse(IReadOnlyList<GatewayProviderTypeItem> ProviderTypes);

public sealed record GatewayProvidersResponse(IReadOnlyList<GatewayProviderListItem> Providers);

public sealed record GatewayProviderListItem(string Id, string Type, JsonObject Settings, string CreatedAt, bool HasApiKey);

public sealed record GatewayProviderResponse(GatewayProviderListItem Provider);

public sealed record GatewayProviderSaveRequest(string Id, string Type, JsonObject Settings);

public sealed record GatewayModelsResponse(IReadOnlyList<GatewayModelListItem> Models);

public sealed record GatewayModelCapabilities(bool? ToolCalling, bool? Streaming, bool? ReasoningOnlyOutput, bool? StructuredJson, string ProbedAt);

public sealed record GatewayModelListItem(string Id, string ProviderId, string ModelName, JsonObject Settings, GatewayModelCapabilities? Capabilities, string CreatedAt);

public sealed record GatewayModelResponse(GatewayModelListItem Model);

public sealed record GatewayModelSaveRequest(string Id, string ProviderId, string ModelName, JsonObject Settings);

public sealed record GatewayDiscoveredModelItem(string Id, string DisplayName, string? Description);

public sealed record GatewayDiscoveredModelsResponse(IReadOnlyList<GatewayDiscoveredModelItem> Models);

public sealed record GatewayModelCapabilitiesResponse(GatewayModelCapabilities Capabilities);

public sealed record GatewayConnectorsResponse(IReadOnlyList<GatewayConnectorListItem> Connectors);

public sealed record GatewayConnectorListItem(
    string Id,
    string Name,
    string Description,
    Uri? IconUrl,
    string? PresetId,
    string Transport,
    Uri? HttpUrl,
    string? ContainerImage,
    string? ContainerCommand,
    IReadOnlyList<string> ContainerArgs,
    string? ContainerSidecarName,
    string? BundlePath,
    string? ServerType,
    int? ReadinessTimeoutSeconds,
    int? JitCooldownSeconds,
    Uri? HealthcheckUrl,
    int? HealthcheckIntervalMilliseconds,
    bool Enabled,
    bool IsInternalSystemConnector,
    int? ToolCount,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> EnvKeys,
    string Scope,
    string? OwnerUserId,
    string CreatedAt,
    string UpdatedAt);

public sealed record GatewayRuntimeContainerItem(
    string Name,
    string Image,
    string Status,
    string Role);

public sealed record GatewayRuntimeContainersResponse(
    bool Ok,
    string Message,
    IReadOnlyList<GatewayRuntimeContainerItem> Containers);

public sealed record GatewayPkgsProxyPopularPackage(
    string Ecosystem,
    string Name,
    long Hits,
    long SizeBytes);

public sealed record GatewayPkgsProxyRecentPackage(
    string Ecosystem,
    string Name,
    string Version,
    string CachedAt,
    long SizeBytes);

public sealed record GatewayPkgsProxyCacheStatsResponse(
    bool Ok,
    string Message,
    long Packages,
    long Versions,
    long Artifacts,
    long TotalSizeBytes,
    long TotalHits,
    IReadOnlyDictionary<string, long> Ecosystems,
    IReadOnlyList<GatewayPkgsProxyPopularPackage> Popular,
    IReadOnlyList<GatewayPkgsProxyRecentPackage> Recent);

public sealed record GatewayRuntimeUpdateOperationItem(
    string OperationType,
    string CompletedAtUtc,
    string? FromVersion,
    string ToVersion,
    bool Succeeded,
    string? ErrorMessage);

public sealed record GatewayApplianceHealthItem(
    string? InstalledVersion,
    string? LastUpdateAppliedAtUtc,
    string? SidecarsStagedAtUtc,
    int? SidecarCount);

public sealed record GatewayEmptyRequest();

public sealed record GatewayUsersResponse(IReadOnlyList<GatewayUserListItem> Users);

public sealed record GatewayUserResponse(GatewayUserListItem User);

public sealed record GatewayUserPayload(
    string Id,
    string Username,
    string? Email,
    string? DisplayName,
    string Role,
    bool IsActive,
    bool CanImportConnectors,
    bool CanImportSkills);

public sealed record GatewayUserListItem(
    string Id,
    string Username,
    string? Email,
    string? DisplayName,
    string Role,
    bool IsActive,
    bool CanImportConnectors,
    bool CanImportSkills,
    string? ChannelDeviceId,
    bool ChannelBotTokenSet,
    string? ChannelDefaultTargetType,
    string? ChannelDefaultTargetId,
    string? ChannelHitlTetherMode);

public sealed record GatewayModelDiscoveryRequest(string ProviderId);

public sealed record GatewayAgentSkillAttachmentPayload(
    string SkillId,
    [property: JsonConverter(typeof(GatewaySkillAttachmentModeJsonConverter))] string Mode);

public sealed record GatewayAgentSessionPayload(
    bool HistoryEnabled,
    int MaxHistoryRuns,
    bool SummariesEnabled,
    string? SummaryModelId,
    bool MemoryFactExtractionEnabled);

public sealed record GatewayAgentSandboxPayload(
    bool Enabled,
    IReadOnlyList<string> AllowedLanguages,
    string MaxMemory,
    int MaxTimeout);

public sealed record GatewayApprovalConfigPayload(
    IReadOnlyList<string> RequireMcpIds,
    int TimeoutSeconds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RequireMcpFunctionFilters,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DisabledMcpFunctions,
    IReadOnlyList<string> AgentToolIds,
    IReadOnlyList<string> ApprovalRequiredAgentToolIds);

public sealed record GatewayAgentToolExposurePayload(bool ExposedAsTool, string Description, int InvocationTimeoutSeconds);

public sealed record GatewayAgentRuntimePayload(
    int? MaxRunDurationSeconds,
    int? HitlExpirySeconds,
    string? HitlExpiryAction);

public sealed record GatewayAgentMcpBindingPayload(string ConnectorId, string Context);

public sealed record GatewayAgentListItem(
    string Id,
    string Scope,
    bool Enabled,
    string Name,
    string ModelId,
    string SystemPrompt,
    string SuperExecMode,
    IReadOnlyList<GatewayAgentMcpBindingPayload> McpBindings,
    IReadOnlyList<GatewayAgentSkillAttachmentPayload> SkillAttachments,
    GatewayAgentSessionPayload Session,
    GatewayAgentSandboxPayload Sandbox,
    string CreatedAt,
    GatewayApprovalConfigPayload ApprovalConfig,
    JsonObject Persona,
    GatewayAgentToolExposurePayload ToolExposure,
    string OwnerUserId,
    GatewayAgentRuntimePayload? Runtime = null,
    IReadOnlyList<string>? CallerAgentIds = null);

public sealed record GatewayAgentsResponse(IReadOnlyList<GatewayAgentListItem> Agents);

public sealed record GatewayAgentResponse(GatewayAgentListItem Agent);

public sealed record GatewayAgentUpsertRequest(
    string Name,
    string ModelId,
    string SystemPrompt,
    string SuperExecMode,
    IReadOnlyList<GatewayAgentMcpBindingPayload> McpBindings,
    IReadOnlyList<GatewayAgentSkillAttachmentPayload>? SkillAttachments,
    GatewayAgentSessionPayload Session,
    GatewayAgentSandboxPayload Sandbox,
    GatewayApprovalConfigPayload ApprovalConfig,
    JsonObject Persona,
    GatewayAgentToolExposurePayload ToolExposure,
    GatewayAgentRuntimePayload? Runtime = null);

public sealed record GatewaySessionCreateRequest(string TargetType, string TargetId);

public sealed record GatewayMetrics(int TokenIn, int TokenOut, int ElapsedMs);

public sealed record GatewayAgentStep(string Agent, string Response);

public sealed record GatewayToolCall(string ToolCallId, string ToolName, string? AgentId, string? ArgumentsJson, string? ResultJson, string Status);

public sealed record GatewayConversationAsset(
    string Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Origin,
    string Disposition,
    Uri DownloadUrl,
    string CreatedAt);

public sealed record GatewayMessage(
    string Id,
    string Role,
    string Content,
    string Timestamp,
    bool Compact,
    string? Thinking,
    GatewayMetrics? Metrics,
    IReadOnlyList<GatewayAgentStep>? AgentSteps,
    IReadOnlyList<GatewayToolCall>? ToolCalls,
    string? Source,
    IReadOnlyList<GatewayConversationAsset>? Assets);

public sealed record GatewaySession(
    string Id,
    string TargetType,
    string TargetId,
    string OwnerUserId,
    string Title,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<GatewayMessage> Messages,
    bool Compacted,
    string? CompactSummary);

public sealed record GatewaySessionsResponse(IReadOnlyList<GatewaySession> Sessions);

public sealed record GatewayNaradCapabilitiesResponse(
    string ProtocolVersion,
    long CurrentSessionSequence);

public sealed record GatewayNaradMessageIntentRequest(string Message, long? ExpectedRevision);

public sealed record GatewayNaradIntentResponse(
    string Status,
    string? Detail,
    long? CurrentRevision,
    long? CurrentSessionSequence = null);

public sealed record GatewayNaradAuthorityEvent(
    string Type,
    string? EventId,
    long? SessionSequence,
    string? RootRunId,
    string? RunId,
    string? Delta,
    string? Content,
    string? Message,
    string? MessageId,
    string? ToolCallId,
    string? ToolName,
    string? Outcome,
    string? Operation,
    string? Status,
    string? Kind,
    string? Title);

public sealed record GatewayUserInviteRequest(string Username, string Password, string Role, string? Email);

public sealed record GatewayUserRoleUpdateRequest(bool IsAdmin);

public sealed record GatewayUserActivationUpdateRequest(bool IsActive);

public sealed record GatewayUserImportPermissionsUpdateRequest(bool CanImportConnectors, bool CanImportSkills);

public sealed record GatewayObservabilitySettingsPayload(bool IncludeBinaryContent, bool CaptureMessageContent);

public sealed record GatewayUpdateSettingsPayload(string ReleaseChannel);

public sealed record GatewayRuntimeGroundingSettingsPayload(string Locale, string TimeZone);

public sealed record GatewayRuntimeLimitsSettingsPayload(
    int ModelTimeoutSeconds,
    int MaxRunDurationSeconds,
    int HitlExpirySeconds,
    string HitlExpiryAction,
    bool AllowMultipleToolCalls = true,
    bool AllowConcurrentInvocation = false,
    int MaxParallelTools = 0);

public sealed record GatewaySystemSettingsPayload(
    string LogLevel,
    GatewayObservabilitySettingsPayload Observability,
    GatewayUpdateSettingsPayload? Updates = null,
    GatewayRuntimeGroundingSettingsPayload? RuntimeGrounding = null,
    GatewayRuntimeLimitsSettingsPayload? RuntimeLimits = null);

public sealed record GatewayWorkspaceSettingsPayload(string BaseRoot);

public sealed record GatewaySandboxSettingsPayload(string? Image, int DefaultCpus, string? DefaultMemory, int DefaultTimeout);

public sealed record GatewayDocumentIngestionSettingsPayload(
    bool Enabled,
    string InternalParserConnectorId,
    string InternalParserToolName,
    int ParserTimeoutSeconds,
    string ParseRequiredExtensions,
    bool PdfImageFallbackEnabled,
    bool VisionRouteAllowed);

public sealed record GatewayKautukaInfrastructureSettingsPayload(
    GatewayWorkspaceSettingsPayload Workspace,
    GatewaySandboxSettingsPayload Sandbox,
    GatewayDocumentIngestionSettingsPayload DocumentIngestion);

public sealed record GatewaySystemSettingsResponse(
    GatewaySystemSettingsPayload System,
    GatewayKautukaInfrastructureSettingsPayload Kautuka);

public sealed record GatewayOrganizationSettingsPayload(string? Name, string? Industry);

public sealed record GatewayOrganizationSettingsResponse(GatewayOrganizationSettingsPayload Organization);

public sealed record GatewayProviderPathPolicySettingsResponse(
    double ToolNativePromotionThreshold,
    int ToolMinObservations,
    double ToolNativeDemotionThreshold,
    double StructuredJsonStrictPromotionThreshold,
    int StructuredJsonMinObservations,
    double VisionEnableThreshold,
    int ObservationDecayWindowDays,
    bool ProviderPathFallbackModeEnabled);

public sealed record GatewayProviderPathPolicySettingsRequest(
    [property: JsonPropertyName("tool_native_promotion_threshold")] double ToolNativePromotionThreshold,
    [property: JsonPropertyName("tool_min_observations")] int ToolMinObservations,
    [property: JsonPropertyName("tool_native_demotion_threshold")] double ToolNativeDemotionThreshold,
    [property: JsonPropertyName("structured_json_strict_promotion_threshold")] double StructuredJsonStrictPromotionThreshold,
    [property: JsonPropertyName("structured_json_min_observations")] int StructuredJsonMinObservations,
    [property: JsonPropertyName("vision_enable_threshold")] double VisionEnableThreshold,
    [property: JsonPropertyName("observation_decay_window_days")] int ObservationDecayWindowDays,
    [property: JsonPropertyName("provider_path_fallback_mode_enabled")] bool ProviderPathFallbackModeEnabled);

public sealed record GatewayMemorySettingsResponse(
    double FactPersistThreshold,
    double FactInjectThreshold,
    int MaxInjectedFacts,
    int InjectionTokenBudget);

public sealed record GatewayMemoryFactPayload(
    string Id,
    string Title,
    string Content,
    string Category,
    string Source,
    double Confidence,
    string CreatedAt,
    string UpdatedAt,
    string? SessionId);

public sealed record GatewayMemoriesResponse(IReadOnlyList<GatewaySession> Sessions, IReadOnlyList<GatewayMemoryFactPayload> Facts);

public sealed record GatewayPersonaDefinitionsResponse(JsonObject Definitions);

public sealed record GatewayPersonaDefinitionsRequest(
    [property: JsonPropertyName("company_name")] string? CompanyName,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("styles")] IReadOnlyList<string> Styles,
    [property: JsonPropertyName("tones")] IReadOnlyList<string> Tones,
    [property: JsonPropertyName("verbosity_levels")] IReadOnlyList<string> VerbosityLevels);

public sealed record GatewayMemorySettingsRequest(
    [property: JsonPropertyName("fact_persist_threshold")] double FactPersistThreshold,
    [property: JsonPropertyName("fact_inject_threshold")] double FactInjectThreshold,
    [property: JsonPropertyName("max_injected_facts")] int MaxInjectedFacts,
    [property: JsonPropertyName("injection_token_budget")] int InjectionTokenBudget);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthLoginRequest))]
[JsonSerializable(typeof(AuthLoginResponse))]
[JsonSerializable(typeof(GatewayBooleanSuccessResponse))]
[JsonSerializable(typeof(GatewayDeletedResponse))]
[JsonSerializable(typeof(GatewayErrorResponse))]
[JsonSerializable(typeof(GatewayAutopilotSchedule))]
[JsonSerializable(typeof(GatewayAutopilotSchedule[]))]
[JsonSerializable(typeof(GatewayScheduledRun))]
[JsonSerializable(typeof(GatewayScheduledRun[]))]
[JsonSerializable(typeof(GatewayAutopilotNotification))]
[JsonSerializable(typeof(GatewayAutopilotNotification[]))]
[JsonSerializable(typeof(GatewayProviderTypeItem))]
[JsonSerializable(typeof(GatewayProviderTypesResponse))]
[JsonSerializable(typeof(GatewayProvidersResponse))]
[JsonSerializable(typeof(GatewayProviderListItem))]
[JsonSerializable(typeof(GatewayProviderResponse))]
[JsonSerializable(typeof(GatewayProviderSaveRequest))]
[JsonSerializable(typeof(GatewayModelCapabilities))]
[JsonSerializable(typeof(GatewayModelsResponse))]
[JsonSerializable(typeof(GatewayModelListItem))]
[JsonSerializable(typeof(GatewayModelResponse))]
[JsonSerializable(typeof(GatewayModelSaveRequest))]
[JsonSerializable(typeof(GatewayDiscoveredModelItem))]
[JsonSerializable(typeof(GatewayDiscoveredModelsResponse))]
[JsonSerializable(typeof(GatewayModelCapabilitiesResponse))]
[JsonSerializable(typeof(GatewayConnectorsResponse))]
[JsonSerializable(typeof(GatewayConnectorListItem))]
[JsonSerializable(typeof(GatewayRuntimeContainerItem))]
[JsonSerializable(typeof(GatewayRuntimeContainersResponse))]
[JsonSerializable(typeof(GatewayPkgsProxyPopularPackage))]
[JsonSerializable(typeof(GatewayPkgsProxyRecentPackage))]
[JsonSerializable(typeof(GatewayPkgsProxyCacheStatsResponse))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, long>))]
[JsonSerializable(typeof(GatewayRuntimeUpdateOperationItem))]
[JsonSerializable(typeof(GatewayRuntimeUpdateOperationItem[]))]
[JsonSerializable(typeof(GatewayApplianceHealthItem))]
[JsonSerializable(typeof(GatewayUsersResponse))]
[JsonSerializable(typeof(GatewayUserResponse))]
[JsonSerializable(typeof(GatewayUserPayload))]
[JsonSerializable(typeof(GatewayUserListItem))]
[JsonSerializable(typeof(GatewayModelDiscoveryRequest))]
[JsonSerializable(typeof(GatewayAgentSkillAttachmentPayload))]
[JsonSerializable(typeof(GatewayAgentSessionPayload))]
[JsonSerializable(typeof(GatewayAgentSandboxPayload))]
[JsonSerializable(typeof(GatewayApprovalConfigPayload))]
[JsonSerializable(typeof(GatewayAgentToolExposurePayload))]
[JsonSerializable(typeof(GatewayAgentRuntimePayload))]
[JsonSerializable(typeof(GatewayAgentListItem))]
[JsonSerializable(typeof(GatewayAgentsResponse))]
[JsonSerializable(typeof(GatewayAgentResponse))]
[JsonSerializable(typeof(GatewayAgentUpsertRequest))]
[JsonSerializable(typeof(GatewaySessionCreateRequest))]
[JsonSerializable(typeof(GatewayMetrics))]
[JsonSerializable(typeof(GatewayAgentStep))]
[JsonSerializable(typeof(GatewayToolCall))]
[JsonSerializable(typeof(GatewayConversationAsset))]
[JsonSerializable(typeof(GatewayMessage))]
[JsonSerializable(typeof(GatewaySession))]
[JsonSerializable(typeof(GatewaySessionsResponse))]
[JsonSerializable(typeof(GatewayNaradCapabilitiesResponse))]
[JsonSerializable(typeof(GatewayNaradMessageIntentRequest))]
[JsonSerializable(typeof(GatewayNaradIntentResponse))]
[JsonSerializable(typeof(GatewayEmptyRequest))]
[JsonSerializable(typeof(GatewayUserInviteRequest))]
[JsonSerializable(typeof(GatewayUserRoleUpdateRequest))]
[JsonSerializable(typeof(GatewayUserActivationUpdateRequest))]
[JsonSerializable(typeof(GatewayUserImportPermissionsUpdateRequest))]
[JsonSerializable(typeof(GatewayObservabilitySettingsPayload))]
[JsonSerializable(typeof(GatewayUpdateSettingsPayload))]
[JsonSerializable(typeof(GatewayRuntimeGroundingSettingsPayload))]
[JsonSerializable(typeof(GatewayRuntimeLimitsSettingsPayload))]
[JsonSerializable(typeof(GatewaySystemSettingsPayload))]
[JsonSerializable(typeof(GatewayWorkspaceSettingsPayload))]
[JsonSerializable(typeof(GatewaySandboxSettingsPayload))]
[JsonSerializable(typeof(GatewayDocumentIngestionSettingsPayload))]
[JsonSerializable(typeof(GatewayKautukaInfrastructureSettingsPayload))]
[JsonSerializable(typeof(GatewaySystemSettingsResponse))]
[JsonSerializable(typeof(GatewayOrganizationSettingsPayload))]
[JsonSerializable(typeof(GatewayOrganizationSettingsResponse))]
[JsonSerializable(typeof(GatewayProviderPathPolicySettingsResponse))]
[JsonSerializable(typeof(GatewayProviderPathPolicySettingsRequest))]
[JsonSerializable(typeof(GatewayMemorySettingsResponse))]
[JsonSerializable(typeof(GatewayMemoryFactPayload))]
[JsonSerializable(typeof(GatewayMemoryFactPayload[]))]
[JsonSerializable(typeof(GatewayMemoriesResponse))]
[JsonSerializable(typeof(GatewayMemorySettingsRequest))]
[JsonSerializable(typeof(GatewayPersonaDefinitionsResponse))]
[JsonSerializable(typeof(GatewayPersonaDefinitionsRequest))]
[JsonSerializable(typeof(JsonObject))]
public sealed partial class GatewayJsonContext : JsonSerializerContext;

internal sealed class GatewaySkillAttachmentModeJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => Normalize(reader.GetString()),
            JsonTokenType.Number when reader.TryGetInt32(out int numericValue) => numericValue switch
            {
                1 => "trusted",
                2 => "require_approval",
                _ => "disabled",
            },
            JsonTokenType.Null => "disabled",
            _ => throw new JsonException($"Unsupported skill attachment mode token '{reader.TokenType}'."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(Normalize(value));

    private static string Normalize(string? value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "1" or "TRUSTED" => "trusted",
            "2" or "REQUIREAPPROVAL" or "REQUIRE_APPROVAL" or "REQUIRE-APPROVAL" => "require_approval",
            _ => "disabled",
        };
}
