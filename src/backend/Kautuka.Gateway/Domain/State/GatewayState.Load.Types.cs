namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal record DbProvider(string Id, string Name, string Type, string SettingsJson, string CreatedAt);
    internal record DbModel(string Id, string ProviderId, string ModelName, string SettingsJson, string? CapabilitiesJson, string CreatedAt);
    internal record DbAgent(string StorageLocatorKey, string Id, string Scope, int Enabled, string OwnerUserId, string CreatedAt);
    internal record DbMcpServer(string Id, string Name, string Description, string IconUrl, string? PresetId, string Transport, string? HttpUrl, string? EncryptedEnvJson, string EnvKeysJson, string? ContainerImage, string? ContainerCommand, string ContainerArgsJson, string? ContainerSidecarName, int? ReadinessTimeoutSeconds, int? JitCooldownSeconds, string? HealthcheckUrl, int? HealthcheckIntervalMilliseconds, int Enabled, int IsInternalSystemConnector, string Scope, string? OwnerUserId, string CreatedAt, string UpdatedAt, int? ToolCount, string? ToolNamesJson, string? BundlePath = null, string? ServerType = null, string? RuntimeArtifactType = null, string? RuntimeArtifactSourceDigest = null, string? RuntimeArtifactBuildStatus = null, string? McpProtocolVersion = null);
    internal record DbSkill(string Id, string Name, string Description, string PackageHash, string Status, string PackagePath, int HasResources, int HasScripts, int Enabled, string EnvKeysJson, string ImportTimestamp, string SourceMetadataJson, string Scope, string? OwnerUserId);
    internal record DbUser(string Username, string UserId, string Role, string Password, int IsActive, string? Email, string? DisplayName, int CanImportConnectors, int CanImportSkills);
    internal record DbSession(string Id, string TargetType, string TargetId, string OwnerUserId, string? Title, string MessagesJson, int Compacted, string? CompactSummary, string CreatedAt, string UpdatedAt, int? EstimatedContextTokens, int? RawTranscriptEstimatedTokens, string? LastEstimatedAt, string? LastCompactedAt, string? LastCompactionReason);
    internal record DbMemoryFact(string Id, string TargetType, string TargetId, string OwnerUserId, string Title, string Content, string Category, string Source, double Confidence, string CreatedAt, string UpdatedAt, string? SessionId);
    internal record DbChannelRegistration(string Id, string ChannelType, string? OwnerUserId, string ExternalBotId, int IsActive, string SettingsJson, string CreatedAt, string UpdatedAt);
    internal record DbChannelIdentityLink(string Id, string RegistrationId, string UserId, string ExternalUserId, string CreatedAt);
    internal record DbChannelTargetBinding(string Id, string RegistrationId, string TargetType, string TargetId, int IsDefault, string CreatedAt);
    internal record DbChannelInteraction(string Id, string RegistrationId, string? UserId, string? TargetType, string? TargetId, string? SessionId, string? RunId, string ChatId, string? MessageThreadId, string Prompt, string? SenderDisplayName, string OptionsJson, string Status, string CreatedAt, string ExpiresAt, string? ResolvedAt, string? ResolvedByExternalUser, string? Resolution, string? ApprovalRequestId, string? InteractionGroupId, long? ChannelMessageId, string? TransportMessageId);
}
