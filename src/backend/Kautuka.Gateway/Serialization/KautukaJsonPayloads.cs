using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Serialization;

internal sealed record GatewaySlackPairingStatusResponse(bool Paired, string? Reason);

internal sealed record GatewayChatResponse(
    string Response,
    string SessionId,
    bool Compacted,
    GatewayMetrics Metrics,
    GatewayRunState? Run = null,
    GatewayActivityEntry[]? ActivityEntries = null);

internal sealed record GatewaySessionsResponse(GatewaySession[] Sessions);

internal sealed record GatewayRunsResponse(GatewayRun[] Runs);

internal sealed record GatewayUsageRunsResponse(
    GatewayUsageRunListItem[] Items,
    int Limit,
    int Offset,
    long TotalCount,
    string Scope);

internal sealed record GatewayUsageSummaryResponse(
    string Scope,
    string GroupBy,
    GatewayUsageTotalsPayload Totals,
    GatewayUsageSummaryBucket[] Buckets);

internal sealed record GatewayUsageRunListItem(
    string RunId,
    string? SessionId,
    string? UserId,
    string? UsernameSnapshot,
    string? UserDisplayNameSnapshot,
    string? AgentId,
    string? AgentNameSnapshot,
    string? ModelId,
    string? ModelNameSnapshot,
    string? ProviderId,
    string? ProviderNameSnapshot,
    string RunStatus,
    string? SourceChannel,
    string RunStartedAt,
    string? FirstModelOutputAt,
    string RunEndedAt,
    int TotalDurationMs,
    int? FirstTokenLatencyMs,
    int? TokenIn,
    int? TokenOut,
    int? TotalTokens,
    int? ProviderRequestCount,
    int? ModelTurnCount,
    int? AssistantTurnCount,
    int? ToolCallCount,
    string? CostBasis,
    decimal? InputCost,
    decimal? OutputCost,
    decimal? TotalCost,
    string? CostCurrency,
    string? PricingSource,
    string UsageDate);

internal sealed record GatewayUsageTotalsPayload(
    long RunCount,
    long ProviderRequestCount,
    long AssistantTurnCount,
    long ModelTurnCount,
    long ToolCallCount,
    long TokenIn,
    long TokenOut,
    long TotalTokens,
    long TotalDurationMs,
    long? AvgFirstTokenLatencyMs,
    decimal? InputCost,
    decimal? OutputCost,
    decimal? TotalCost);

internal sealed record GatewayUsageSummaryBucket(
    string Key,
    string Label,
    GatewayUsageTotalsPayload Totals);

internal sealed record GatewayRuntimeUpdateReleasePayload(
    string ReleaseId,
    string Version,
    string Channel,
    string ReleaseFingerprint,
    string? SourceUrl,
    string? PublishedAtUtc,
    string? Tag);

internal sealed record GatewayRuntimeUpdateOperationPayload(
    string OperationType,
    string CompletedAtUtc,
    string? FromVersion,
    string ToVersion,
    bool Succeeded,
    string? ErrorMessage);

internal sealed record GatewayRuntimeUpdateStatusResponse(
    bool Ok,
    string Message,
    string State,
    string? EffectiveReleaseChannel,
    string? EligibilityState,
    string? EligibilityReason,
    GatewayRuntimeUpdateReleasePayload? CurrentRelease,
    GatewayRuntimeUpdateReleasePayload? AvailableRelease,
    GatewayRuntimeUpdateReleasePayload? LastKnownGoodRelease,
    string? LastDiscoveryAttemptUtc,
    string? LastDiscoverySuccessUtc,
    string ManagerVersion,
    bool HostReady,
    int ActiveExecCount,
    string? LastError);

internal sealed record GatewayRuntimeManagerContainerItem(
    string Name,
    string Image,
    string Status,
    string Role);

internal sealed record GatewayRuntimeManagerContainersResponse(
    bool Ok,
    string Message,
    GatewayRuntimeManagerContainerItem[] Containers);

internal sealed record GatewayPkgsProxyPopularPackage(
    string Ecosystem,
    string Name,
    long Hits,
    long SizeBytes);

internal sealed record GatewayPkgsProxyRecentPackage(
    string Ecosystem,
    string Name,
    string Version,
    string CachedAt,
    long SizeBytes);

internal sealed record GatewayPkgsProxyCacheStatsResponse(
    bool Ok,
    string Message,
    long Packages,
    long Versions,
    long Artifacts,
    long TotalSizeBytes,
    long TotalHits,
    IReadOnlyDictionary<string, long> Ecosystems,
    GatewayPkgsProxyPopularPackage[] Popular,
    GatewayPkgsProxyRecentPackage[] Recent);

internal sealed record GatewayApplianceHealthPayload(
    string? InstalledVersion,
    string? LastUpdateAppliedAtUtc,
    string? SidecarsStagedAtUtc,
    int? SidecarCount);

internal sealed record GatewayToolsResponse(object[] Tools);

internal sealed record GatewaySavedResponse(bool Saved);

internal sealed record GatewayDeletedResponse(bool Deleted);

internal sealed record GatewayProviderTypeItem(
    string Type,
    string FriendlyName,
    bool RequiresApiKey,
    bool RequiresBaseUrl,
    bool SupportsHttpTimeoutSeconds);

internal sealed record GatewayProviderTypesResponse(GatewayProviderTypeItem[] ProviderTypes);

internal sealed record GatewayProviderListItem(
    string Id,
    string Type,
    JsonObject Settings,
    string CreatedAt,
    bool HasApiKey);

internal sealed record GatewayProvidersResponse(GatewayProviderListItem[] Providers);

internal sealed record GatewayProviderResponse(GatewayProviderListItem Provider);

internal sealed record GatewayModelListItem(
    string Id,
    string ProviderId,
    string ModelName,
    JsonObject Settings,
    GatewayModelCapabilities? Capabilities,
    string CreatedAt);

internal sealed record GatewayModelsResponse(GatewayModelListItem[] Models);

internal sealed record GatewayModelResponse(GatewayModelListItem Model);

internal sealed record GatewayModelCandidateProbeResponse(
    GatewayModelCapabilities Capabilities,
    string? WarningMessage);

internal sealed record GatewayDiscoveredModelItem(
    string Id,
    string DisplayName,
    string? Description,
    bool? SupportsChat,
    bool? SupportsEmbeddings,
    string[]? CapabilityTags);

internal sealed record GatewayDiscoveredModelsResponse(GatewayDiscoveredModelItem[] Models);

internal sealed record GatewayModelCapabilitiesResponse(GatewayModelCapabilities Capabilities);

internal sealed class GatewayAgentSkillAttachmentPayload
{
    public string SkillId { get; init; } = string.Empty;

    public string Mode { get; init; } = "disabled";
}

internal sealed class GatewayAgentSessionPayload
{
    public bool HistoryEnabled { get; init; } = true;

    public int MaxHistoryRuns { get; init; } = 5;

    public bool SummariesEnabled { get; init; } = true;

    public string? SummaryModelId { get; init; }

    public bool MemoryFactExtractionEnabled { get; init; } = true;
}

internal sealed class GatewayAgentPlanningPayload
{
    public bool Enabled { get; init; }

    public bool RequireSignoff { get; init; }
}

internal sealed class GatewayAgentSandboxPayload
{
    public bool Enabled { get; init; } = true;

    public string[] AllowedLanguages { get; init; } = [];

    public string MaxMemory { get; init; } = "2g";

    public int MaxTimeout { get; init; } = 120;
}

internal sealed class GatewayAgentMcpBindingPayload
{
    public string ConnectorId { get; init; } = string.Empty;

    public string Context { get; init; } = "OwnContext";
}

internal sealed record GatewayAgentFileListEntry(
    string Name,
    string Path,
    string EntryType,
    long? SizeBytes,
    DateTimeOffset ModifiedAt,
    int? FileCount);

internal sealed record GatewayAgentFileListResponse(
    string AgentId,
    string Path,
    GatewayAgentFileListEntry[] Entries,
    int TotalFileCount,
    long TotalSizeBytes);

internal sealed record GatewayAgentFileSearchResponse(
    string AgentId,
    string Query,
    GatewayAgentFileListEntry[] Entries);

internal sealed record GatewayAgentFilesDisabledResponse(
    string AgentId,
    string ReasonCode,
    string Message);

internal sealed record GatewayAgentFileUploadResponse(
    string AgentId,
    string Path,
    string FileName,
    string ContentType,
    long SizeBytes);

internal sealed record GatewayAgentFileCreateFolderResponse(
    string AgentId,
    string Path,
    string Name);

internal sealed class GatewayAgentFileCreateFolderRequest
{
    public string Path { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

internal sealed class GatewayAgentFileDeleteRequest
{
    public string Path { get; init; } = string.Empty;
}

internal sealed class GatewayApprovalConfigPayload
{
    public string[] RequireMcpIds { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 300;

    public Dictionary<string, string[]> RequireMcpFunctionFilters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string[]> DisabledMcpFunctions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] AgentToolIds { get; init; } = [];

    public string[] ApprovalRequiredAgentToolIds { get; init; } = [];
}

internal sealed class GatewayAgentToolExposurePayload
{
    public bool ExposedAsTool { get; init; }

    public string Description { get; init; } = string.Empty;

    public int InvocationTimeoutSeconds { get; init; } = 60;
}

internal sealed class GatewayAgentRuntimePayload
{
    public int? MaxRunDurationSeconds { get; init; }

    public int? HitlExpirySeconds { get; init; }

    public string? HitlExpiryAction { get; init; }

    public bool? AllowMultipleToolCalls { get; init; }

    public bool? AllowConcurrentInvocation { get; init; }

    public int? MaxParallelTools { get; init; }
}

internal sealed class GatewayAgentUpsertRequest
{
    public string Name { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string SystemPrompt { get; init; } = string.Empty;

    public string SuperExecMode { get; init; } = "disabled";

    public GatewayAgentMcpBindingPayload[] McpBindings { get; init; } = [];

    public GatewayAgentSkillAttachmentPayload[]? SkillAttachments { get; init; }

    public GatewayAgentSessionPayload? Session { get; init; }

    public GatewayAgentPlanningPayload? Planning { get; init; }

    public GatewayAgentSandboxPayload? Sandbox { get; init; }

    public GatewayApprovalConfigPayload? ApprovalConfig { get; init; }

    public JsonObject? Persona { get; init; }

    public GatewayAgentToolExposurePayload? ToolExposure { get; init; }

    public GatewayAgentRuntimePayload? Runtime { get; init; }
}

internal sealed class GatewayUserRoleUpdateRequest
{
    public bool IsAdmin { get; init; }
}

internal sealed class GatewayUserActivationUpdateRequest
{
    public bool IsActive { get; init; }
}

internal sealed class GatewayUserImportPermissionsUpdateRequest
{
    public bool CanImportConnectors { get; init; }

    public bool CanImportSkills { get; init; }
}

internal sealed class GatewayUserProfileUpdateRequest
{
    public string? Email { get; init; }

    public string? DisplayName { get; init; }
}

internal sealed record ExternalLoginProvidersResponse(bool Google, bool Microsoft);

internal sealed class ExternalLoginStartRequest
{
    public string Provider { get; init; } = string.Empty;

    public string? MicrosoftAccountType { get; init; }

    public string Intent { get; init; } = "login";
}

internal sealed record ExternalLoginStartResponse(string StartUrl);

internal sealed class ExternalLoginCompleteRequest
{
    public string Code { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string BrokerTicket { get; init; } = string.Empty;

    public string? InviteCode { get; init; }

    public string? SetupToken { get; init; }
}

internal sealed class GatewayUserPasswordUpdateRequest
{
    public string CurrentPassword { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
}

internal sealed class GatewayConnectorUpsertRequest
{
    public string? Scope { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? IconUrl { get; init; }

    public string? PresetId { get; init; }

    public string? Transport { get; init; }

    public string? HttpUrl { get; init; }

    public string? LocalCommand { get; init; }

    public string[]? LocalArgs { get; init; }

    public string? ContainerImage { get; init; }

    public string? ContainerCommand { get; init; }

    public string[]? ContainerArgs { get; init; }

    public string? ContainerSidecarName { get; init; }

    public string? BundlePath { get; init; }

    public string? ServerType { get; init; }

    public string? RuntimeArtifactType { get; init; }

    public string? RuntimeArtifactSourceDigest { get; init; }

    public string? RuntimeArtifactBuildStatus { get; init; }

    public int? ReadinessTimeoutSeconds { get; init; }

    public int? JitCooldownSeconds { get; init; }

    public string? HealthcheckUrl { get; init; }

    public int? HealthcheckIntervalMilliseconds { get; init; }

    public string? McpProtocolVersion { get; init; }

    public string[]? EnvKeys { get; init; }

    public bool? Enabled { get; init; }

    public int? ToolCount { get; init; }

    public string[]? ToolNames { get; init; }
}

internal sealed class GatewayConnectorTestRequest
{
    public string Transport { get; init; } = "local";

    public string? HttpUrl { get; init; }

    public string? LocalCommand { get; init; }

    public string[]? LocalArgs { get; init; }

    public string? ContainerImage { get; init; }

    public string? ContainerCommand { get; init; }

    public string[]? ContainerArgs { get; init; }

    public string? ContainerSidecarName { get; init; }

    public string? BundlePath { get; init; }

    public string? ServerType { get; init; }

    public int? ReadinessTimeoutSeconds { get; init; }

    public int? JitCooldownSeconds { get; init; }

    public string? HealthcheckUrl { get; init; }

    public int? HealthcheckIntervalMilliseconds { get; init; }

    public string? McpProtocolVersion { get; init; }

    public string[]? EnvKeys { get; init; }

    public Dictionary<string, string>? TestEnv { get; init; }

    public string? Id { get; init; }

    [JsonIgnore]
    public bool AllowPrivateDestination { get; init; }
}

internal sealed record GatewayAgentListItem(
    string Id,
    string Scope,
    bool Enabled,
    string Name,
    string ModelId,
    string SystemPrompt,
    string SuperExecMode,
    GatewayAgentMcpBindingPayload[] McpBindings,
    AgentSkillAttachmentDefinition[] SkillAttachments,
    GatewayAgentSessionPayload Session,
    GatewayAgentPlanningPayload Planning,
    GatewayAgentSandboxPayload Sandbox,
    string CreatedAt,
    GatewayApprovalConfigPayload ApprovalConfig,
    JsonObject Persona,
    GatewayAgentToolExposurePayload ToolExposure,
    string OwnerUserId,
    GatewayAgentRuntimePayload? Runtime = null,
    string[]? CallerAgentIds = null,
    long Revision = 0);

internal sealed record GatewayAgentsResponse(GatewayAgentListItem[] Agents);

internal sealed record GatewayAgentResponse(GatewayAgentListItem Agent);

internal sealed record GatewayAgentRevisionListItem(
    long Revision,
    string CreatedAt,
    string[] ChangedSettingIds,
    string[] ChangedSettingNames);

internal sealed record GatewayAgentRevisionsResponse(
    string AgentId,
    long CurrentRevision,
    GatewayAgentRevisionListItem[] Revisions);

internal sealed record GatewayAgentRevisionChange(
    string SettingId,
    string SettingName,
    JsonElement? Before,
    JsonElement? After);

internal sealed record GatewayAgentRevisionDiffResponse(
    string AgentId,
    long? FromRevision,
    long ToRevision,
    string CreatedAt,
    GatewayAgentRevisionChange[] Changes);

internal sealed record GatewayToolPolicyDecisionProjection(
    bool Allowed,
    string ReasonCode,
    string Message);

internal sealed record GatewayToolPolicyCategoryProjection(
    string Category,
    GatewayToolPolicyDecisionProjection Exposure,
    GatewayToolPolicyDecisionProjection Execution);

internal sealed record GatewayAgentToolPolicyProjectionResponse(
    string AgentId,
    GatewayToolPolicyCategoryProjection[] Categories);

internal sealed record GatewayObservabilitySettingsPayload(
    bool IncludeBinaryContent,
    bool CaptureMessageContent);

internal sealed record GatewayUpdateSettingsPayload(
    string ReleaseChannel);

internal sealed record GatewayRuntimeGroundingSettingsPayload(
    string Locale,
    string TimeZone);

internal sealed record GatewayRuntimeLimitsSettingsPayload(
    int ModelTimeoutSeconds,
    int MaxRunDurationSeconds,
    int HitlExpirySeconds,
    string HitlExpiryAction,
    bool AllowMultipleToolCalls = true,
    bool AllowConcurrentInvocation = false,
    int MaxParallelTools = 0);

internal sealed record GatewaySystemSettingsPayload(
    string LogLevel,
    GatewayObservabilitySettingsPayload Observability,
    GatewayUpdateSettingsPayload Updates,
    GatewayRuntimeGroundingSettingsPayload RuntimeGrounding,
    GatewayRuntimeLimitsSettingsPayload RuntimeLimits);

internal sealed record GatewayWorkspaceSettingsPayload(
    string BaseRoot);

internal sealed record GatewaySandboxSettingsPayload(
    string? Image,
    int DefaultCpus,
    string? DefaultMemory,
    int DefaultTimeout);

internal sealed record GatewayDocumentIngestionSettingsPayload(
    bool Enabled,
    string InternalParserConnectorId,
    string InternalParserToolName,
    int ParserTimeoutSeconds,
    string ParseRequiredExtensions,
    bool PdfImageFallbackEnabled,
    bool VisionRouteAllowed);

internal sealed record GatewayKautukaInfrastructureSettingsPayload(
    GatewayWorkspaceSettingsPayload Workspace,
    GatewaySandboxSettingsPayload Sandbox,
    GatewayDocumentIngestionSettingsPayload DocumentIngestion);

internal sealed record GatewaySystemSettingsResponse(
    GatewaySystemSettingsPayload System,
    GatewayKautukaInfrastructureSettingsPayload Kautuka);

internal sealed record GatewayAdvancedSettingsResponse(
    GatewaySystemSettingsPayload System,
    GatewayKautukaInfrastructureSettingsPayload Kautuka,
    JsonObject PersonaDefinitions,
    GatewayProviderPathPolicySettingsResponse ProviderPathPolicy,
    GatewayMemorySettingsResponse Memory,
    GatewayProviderCatalogItem[] ProviderTypeCatalog);

internal sealed record GatewayOrganizationSettingsPayload(string? Name, string? Industry);

internal sealed record GatewayOrganizationSettingsResponse(GatewayOrganizationSettingsPayload Organization);

internal sealed record GatewayProviderPathPolicySettingsResponse(
    double ToolNativePromotionThreshold,
    int ToolMinObservations,
    double ToolNativeDemotionThreshold,
    double StructuredJsonStrictPromotionThreshold,
    int StructuredJsonMinObservations,
    double VisionEnableThreshold,
    int ObservationDecayWindowDays,
    bool ProviderPathFallbackModeEnabled);

internal sealed record GatewayMemorySettingsResponse(
    bool EmbeddingsEnabled,
    double FactPersistThreshold,
    double FactInjectThreshold,
    int MaxInjectedFacts,
    int InjectionTokenBudget,
    string? SessionCompactionModelId);

internal sealed record GatewayProviderCatalogItem(
    string Type,
    string FriendlyName,
    string? DefaultBaseUrl,
    int? DefaultHttpTimeoutSeconds,
    bool RequiresApiKey,
    bool RequiresBaseUrl,
    bool SupportsHttpTimeoutSeconds,
    bool IsBuiltIn,
    int SeedVersion);

internal sealed record GatewayPersonaDefinitionsResponse(JsonObject Definitions);

internal sealed record GatewayPersonaProfilesResponse(JsonObject Profiles);

internal sealed record GatewayChannelIdentityLinkPayload(
    string Id,
    string RegistrationId,
    string ProviderKey,
    string ExternalUserId,
    string CreatedAt);

internal sealed record GatewayChannelProviderFieldPayload(
    string FieldKey,
    string Label,
    string? Description,
    string FieldType,
    bool Required,
    bool Secret,
    string? DefaultValue,
    string? ValidationHint,
    string? Group,
    string[] EnumValues);

internal sealed record GatewayChannelProviderPayload(
    string ProviderKey,
    string DisplayName,
    string SchemaVersion,
    string CapabilityVersion,
    GatewayChannelProviderFieldPayload[] Fields);

internal sealed record GatewayUserChannelPayload(
    string ProviderKey,
    string DisplayName,
    string? RegistrationId,
    string? ExternalUserId,
    string? DefaultTargetType,
    string? DefaultTargetId,
    string? HitlTetherMode,
    JsonObject Settings,
    string[] ConfiguredSecretFieldKeys,
    GatewayChannelIdentityLinkPayload[] IdentityLinks,
    string Status);

internal sealed record GatewayUserListItem(
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
    string[] ChannelRegistrationIds,
    GatewayChannelTetherModePayload[] ChannelTetherModes,
    GatewayChannelIdentityLinkPayload[] ChannelIdentityLinks,
    string[] ExternalProviders);

internal sealed record GatewayChannelTetherModePayload(string RegistrationId, string ProviderKey, string HitlTetherMode);

internal sealed record GatewayUsersResponse(GatewayUserListItem[] Users);

internal sealed record GatewayUserResponse(GatewayUserListItem User, string? InviteCode = null);

internal sealed record GatewayChannelSetupPayload(
    bool Success,
    string Status,
    string? Detail);

internal sealed record GatewayChannelLifecyclePayload(
    bool Accepted,
    string Status,
    string Detail);

internal sealed record GatewayUserChannelSettingsResponse(
    GatewayUserListItem User,
    GatewayChannelSetupPayload? ChannelSetup);

internal sealed record GatewayUserChannelsResponse(
    GatewayUserListItem User,
    GatewayChannelProviderPayload[] Providers,
    GatewayUserChannelPayload[] Channels,
    GatewayChannelSetupPayload? ChannelSetup);

internal sealed record GatewayConnectorListItem(
    string Id,
    string Name,
    string Description,
    string? IconUrl,
    string? PresetId,
    string Transport,
    string? HttpUrl,
    string? ContainerImage,
    string? ContainerCommand,
    string[] ContainerArgs,
    string? ContainerSidecarName,
    string? BundlePath,
    string? ServerType,
    string? RuntimeArtifactType,
    string? RuntimeArtifactSourceDigest,
    string? RuntimeArtifactBuildStatus,
    int? ReadinessTimeoutSeconds,
    int? JitCooldownSeconds,
    string? HealthcheckUrl,
    int? HealthcheckIntervalMilliseconds,
    bool Enabled,
    bool IsInternalSystemConnector,
    int? ToolCount,
    string[] ToolNames,
    string[] EnvKeys,
    string Scope,
    string? OwnerUserId,
    string CreatedAt,
    string UpdatedAt,
    string? McpProtocolVersion = null);

internal sealed record GatewayConnectorsResponse(GatewayConnectorListItem[] Connectors);

internal sealed record GatewaySkillListItem(
    string Id,
    string Name,
    string Description,
    string PackageHash,
    string Status,
    string PackagePath,
    bool HasResources,
    bool HasScripts,
    bool Enabled,
    string[] EnvKeys,
    string ImportTimestamp,
    Dictionary<string, string> SourceMetadata,
    string Scope,
    string? OwnerUserId);

internal sealed record GatewaySkillsResponse(GatewaySkillListItem[] Skills);

internal sealed record GatewaySkillInspectionResponse(
    GatewaySkillListItem Skill,
    string InstructionsMarkdown,
    string[] ResourcePaths,
    string[] ScriptPaths);

internal sealed record GatewayBundleImportResponse(
    GatewaySkillInspectionResponse[] Skills);

internal sealed record GatewaySkillImportResult(
    string Type,
    GatewaySkillInspectionResponse? Single,
    GatewayBundleImportResponse? Bundle);

internal sealed record GatewayConnectorTestResponse(
    bool Success,
    string Message,
    int? ToolCount,
    string? PackageChecked,
    string[] ToolNames,
    Dictionary<string, string?> ToolDescriptions);

internal sealed record GatewayConnectorMcpbImportResponse(
    string Outcome,
    string Message,
    string ServerId,
    string ManifestVersion,
    string ServerType,
    GatewayConnectorListItem? Connector);

internal sealed record GatewayCatalogRecommendationItem(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string Publisher,
    string Version,
    bool IsAvailable,
    bool Enabled,
    bool CanInstall,
    string TrustKind,
    string? InstallDisabledReason);

internal sealed record GatewayCatalogRecommendationsResponse(
    GatewayCatalogRecommendationItem[] Connectors,
    GatewayCatalogRecommendationItem[] Skills,
    string? Message);

internal sealed record GatewayApprovalErrorResponse(string Error);

internal sealed record GatewayApprovalResolveResponse(bool Resolved, string Decision);

internal sealed record GatewayApprovalRunRegistrationResponse(bool Registered, string RunId);

internal sealed record GatewayMemoryFactPayload(
    string Id,
    string Title,
    string Content,
    string Category,
    string Source,
    double Confidence,
    string CreatedAt,
    string UpdatedAt,
    string? SessionId);

internal sealed record GatewayMemoriesResponse(GatewaySession[] Sessions, GatewayMemoryFactPayload[] Facts);

internal sealed record GatewayChatRunResponse(
    string Response,
    string SessionId,
    bool Compacted,
    GatewayMetrics Metrics,
    GatewayRunState? Run = null);

internal sealed record GatewayProviderTestResponse(
    bool Success,
    string Message);

internal sealed record CredentialCollectionResponse(
    string Scope,
    string[] Keys);

internal sealed record CredentialUpdateResponse(string Detail);

internal sealed record SetCredentialsRequest(
    Dictionary<string, string> Entries,
    string? HttpUrl = null);

/// <summary>Tells the UI which of a connector's declared env_keys are configured in system scope.</summary>
internal sealed record ConnectorCredentialStatusResponse(
    string ConnectorId,
    string[] ConfiguredKeys,
    string[] MissingKeys,
    string? HttpUrl = null);

internal sealed record ChannelDispatchOptionPayload(string Label, string Value);

internal sealed record ChannelDispatchInteractionPayload(
    string ChatId,
    string Prompt,
    ChannelDispatchOptionPayload[] Options,
    int TimeoutSeconds,
    string? SessionId);

internal sealed record ChannelInteractionDispatchResponse(
    bool Success,
    string Status,
    string Detail,
    string? InteractionId,
    string? RunId);

internal sealed record ChannelIngressChatRequest(
    string TargetType,
    string TargetId,
    string Message,
    string? SessionId,
    string Source,
    string? UserId,
    string[]? Assets = null);

internal sealed record ChatCompactRequest(
    string Message,
    string SessionId);

internal sealed record OllamaShowRequest(string Name);

internal sealed record SseStatePayload(string SessionId);

internal sealed record SseStateEnvelope(SseStatePayload State);

internal sealed record SseEmptyPayload([property: JsonIgnore(Condition = JsonIgnoreCondition.Always)] bool Empty = true);

internal sealed record SseThinkingDeltaPayload(string Kind, string Delta);

internal sealed record SseStreamEventEnvelope(SseThinkingDeltaPayload Event);

internal sealed record SseTextDeltaPayload(string Delta);

internal sealed record SseTextMessageEndPayload(
    string Text,
    SseStatePayload State,
    GatewayMetrics Metrics,
    GatewayRunState? Run = null);

internal sealed record SseRunErrorPayload(string Error);

internal sealed record GatewayParsedAssetStatusResponse(string AssetId, string SessionId, string Status, string ParserName, string ParserVersion, string? Warning, string? Error, string? UpdatedAt);

internal sealed record GatewayParsedAssetStatusesResponse(GatewayParsedAssetStatusResponse[] Assets);

internal sealed record GatewayConversationAssetsResponse(GatewayConversationAsset[] Assets);
