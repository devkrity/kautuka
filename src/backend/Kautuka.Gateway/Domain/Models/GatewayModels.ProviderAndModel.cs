using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Models;

internal static class GatewayOwnershipScopes
{
    public const string System = "system";
    public const string User = "user";
}

internal sealed record GatewayProvider(string Id, string Name, string Type, JsonObject Settings, string CreatedAt);

internal sealed record GatewayProviderTestResult(bool Success, string Message);

internal sealed record GatewayModel(
    string Id,
    string ProviderId,
    string ModelName,
    JsonObject Settings,
    GatewayModelCapabilities? Capabilities,
    string CreatedAt);

internal sealed record GatewayModelCapabilities(
    bool? ToolCalling,
    bool? Streaming,
    bool? ReasoningOnlyOutput,
    bool? StructuredJson,
    bool? AgentConversations,
    string? ProbeError,
    string ProbedAt);

internal sealed record GatewayAgent(
    string Id,
    string Name,
    string ModelId,
    string SystemPrompt,
    string SuperExecMode,
    JsonObject Session,
    JsonObject Planning,
    JsonObject Sandbox,
    AgentSkillAttachmentDefinition[] SkillAttachments,
    string OwnerUserId,
    string CreatedAt,
    JsonObject? ApprovalConfig = null,
    JsonObject? ToolExposure = null,
    string Scope = GatewayOwnershipScopes.User,
    bool Enabled = true,
    JsonObject? Persona = null,
    GatewayAgentMcpBinding[]? McpBindings = null,
    JsonObject? Runtime = null,
    long Revision = 0);

internal sealed record GatewayAgentMcpBinding(
    string ConnectorId,
    string Context = "OwnContext");

internal sealed record GatewayMcpServer(
    string Id,
    string Name,
    string Description,
    string IconUrl,
    string? PresetId,
    string Transport,
    string? HttpUrl,
    string? ContainerImage,
    string? ContainerCommand,
    string[] ContainerArgs,
    string? ContainerSidecarName,
    string? BundlePath,
    string? ServerType,
    int? ReadinessTimeoutSeconds,
    int? JitCooldownSeconds,
    string? HealthcheckUrl,
    int? HealthcheckIntervalMilliseconds,
    bool Enabled,
    bool IsInternalSystemConnector,
    string CreatedAt,
    string UpdatedAt,
    string Scope,
    string? OwnerUserId = null,
    int? ToolCount = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyList<string>? EnvKeys = null,
    string? RuntimeArtifactType = null,
    string? RuntimeArtifactSourceDigest = null,
    string? RuntimeArtifactBuildStatus = null,
    string? McpProtocolVersion = null);

internal sealed record GatewaySkill(
    string Id,
    string Name,
    string Description,
    string PackageHash,
    string Status,
    string PackagePath,
    bool HasResources,
    bool HasScripts,
    bool Enabled,
    IReadOnlyList<string>? EnvKeys,
    string ImportTimestamp,
    Dictionary<string, string> SourceMetadata,
    string Scope,
    string? OwnerUserId = null);

internal sealed record GatewayUser(
    string Id,
    string Username,
    string Password,
    string Role,
    bool IsActive,
    string? Email,
    string? DisplayName,
    bool CanImportConnectors = false,
    bool CanImportSkills = false,
    bool HasPassword = true);
