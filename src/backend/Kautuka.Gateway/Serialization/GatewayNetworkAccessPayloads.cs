using System.Diagnostics.CodeAnalysis;

namespace Kautuka.Gateway.Serialization;

internal sealed record NetworkAccessRequestDto(
    string Id,
    string? UserId,
    string? UserDisplayName,
    string? UserPronoun,
    string? AgentId,
    string? AgentDisplayName,
    string? ConnectorId,
    string? ConnectorDisplayName,
    bool Systemwide,
    string Attribution,
    string Destination,
    string DestKind,
    int Port,
    string ActorKind,
    string FirstSeenUtc,
    string LastSeenUtc,
    long Attempts,
    string Status,
    string Origin,
    string? Purpose = null,
    bool? CompatibilityMode = null,
    bool? WaitingForAdmin = null,
    bool? OwnerCanApproveOwn = null,
    bool? OwnerCanAllowAllAgents = null);

internal sealed record UserPrivilegesSummaryDto(
    bool CanApproveOwn,
    bool CanAllowAllAgents);

internal sealed record NetworkAccessBrakeDto(
    bool UserPaused,
    string? UserPausedBy,
    bool UserLockedByAdmin,
    bool SystemPaused,
    string? SystemPausedBy);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record NetworkRequestsResponseDto(
    long Revision,
    NetworkAccessRequestDto[] Requests,
    UserPrivilegesSummaryDto Privileges,
    NetworkAccessBrakeDto Brake);

internal sealed record NetworkAccessRuleDto(
    string Id,
    string ScopeKind,
    string? UserId,
    string? UserDisplayName,
    string? AgentId,
    string? AgentDisplayName,
    string? ConnectorId,
    string? ConnectorDisplayName,
    string Destination,
    string DestKind,
    int Port,
    bool Allowed,
    bool AllowLan,
    string Source,
    bool Paused,
    string SummaryText,
    string DecidedBy,
    string DecidedAtUtc,
    bool OpenInternetBadge);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record NetworkRulesResponseDto(
    long Revision,
    NetworkAccessRuleDto[] Rules);

internal sealed record RequestDecisionDto(
    bool Allow,
    string? Scope,
    bool? AllowLan,
    long ExpectedRevision);

internal sealed record RequestDecisionResponseDto(
    long Revision,
    NetworkAccessRuleDto? Rule,
    NetworkAccessRequestDto? Request);

internal sealed record CreateOrUpdateRuleDto(
    string ScopeKind,
    string? UserId,
    string? AgentId,
    string? ConnectorId,
    string Destination,
    string DestKind,
    int Port,
    bool Allowed,
    bool? AllowLan,
    long ExpectedRevision);

internal sealed record NetworkAccessActivityDto(
    string TimestampUtc,
    string? UserId,
    string? UserDisplayName,
    string? AgentId,
    string? AgentDisplayName,
    string? ConnectorId,
    string? ConnectorDisplayName,
    string Destination,
    int Port,
    string Decision,
    string Reason,
    bool OpenInternetBadge,
    bool CompatibilityMode);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record NetworkActivityResponseDto(
    NetworkAccessActivityDto[] Entries);

internal sealed record UserNetworkPrivilegesDto(
    string UserId,
    bool CanApproveOwn,
    bool CanAllowAllAgents,
    string? SetBy = null,
    string? SetAtUtc = null);

internal sealed record UpdateUserNetworkPrivilegesDto(
    bool CanApproveOwn,
    bool CanAllowAllAgents);

internal sealed record UpdateUserBrakeDto(
    string? UserId,
    bool On);

internal sealed record UpdateSystemBrakeDto(
    bool On);

internal sealed record ImageCompatRequestDto(
    string ImageDigest,
    string? ConnectorId,
    string? ConnectorDisplayName,
    string FirstSeenUtc,
    string? Reason = null);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record ImageCompatProfileDto(
    string ImageDigest,
    string? ConnectorId,
    string? ConnectorDisplayName,
    string[] Relaxations,
    string GrantedBy,
    string GrantedAtUtc,
    string? Reason = null);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record ImageCompatResponseDto(
    ImageCompatRequestDto[] PendingRequests,
    ImageCompatProfileDto[] Profiles);

[SuppressMessage("Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "DTO record matches JSON array serialization directly.")]
internal sealed record CreateCompatProfileDto(
    string? ConnectorId,
    string? ConnectorDisplayName,
    string[] Relaxations,
    string? Reason = null);
