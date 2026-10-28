namespace Kautuka.ChannelContracts.Channels;

/// <summary>
/// Fact contract versions for channel facts.
/// </summary>
public static class ChannelFactContractVersions
{
    public const string SelectedSession = "kautuka.selected-session/v1";
    public const string ActiveSessions = "kautuka.active-sessions/v1";
    public const string Queue = "kautuka.queue/v1";
    public const string ApprovalRequest = "kautuka.approval-request/v1";
    public const string ApprovalResolution = "kautuka.approval-resolution/v1";
    public const string PromptProvenance = "kautuka.prompt-provenance/v1";
    public const string QueueViewRevision = "kautuka.queue-view-revision/v1";
    public const string SessionFactsEnvelope = "kautuka.session-facts/v1";
}

/// <summary>
/// Selected session fact — conversation identity and session selection state.
/// </summary>
public sealed record ChannelSelectedSessionFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    string? SessionId,
    string? SessionLabel,
    string? AgentDisplayName,
    long Revision,
    string? TargetType = null,
    string? TargetId = null,
    string? AgentColor = null);

/// <summary>
/// Active session entry within ChannelActiveSessionsFact.
/// </summary>
public sealed record ChannelActiveSessionEntry(
    string SessionId,
    string Label,
    string RunLabel,
    string? DistinctionSuffix = null);

/// <summary>
/// Active sessions fact — list of sessions for the conversation/thread.
/// </summary>
public sealed record ChannelActiveSessionsFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    long Revision,
    IReadOnlyList<ChannelActiveSessionEntry> Sessions);

/// <summary>
/// Queue entry within ChannelQueueFact.
/// </summary>
public sealed record ChannelQueueEntry(
    string TurnId,
    string SessionId,
    int Order,
    string Preview,
    string? AgentDisplayName = null,
    string? SessionLabel = null,
    string? AgentColor = null);

/// <summary>
/// Queue fact — waiting turns for the conversation/thread.
/// </summary>
public sealed record ChannelQueueFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    long Revision,
    int TotalCount,
    IReadOnlyList<ChannelQueueEntry> Queue);

/// <summary>
/// Approval request fact — HITL approval request details.
/// </summary>
public sealed record ChannelApprovalRequestFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    string RequestId,
    string ToolName,
    string? ArgumentsJson,
    string? UserMessage,
    string? OriginatingSessionId,
    string? OriginatingRootRunId,
    string? PromptText,
    string InteractionId,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Approval resolution fact — HITL approval decision outcome.
/// </summary>
public sealed record ChannelApprovalResolutionFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    string RequestId,
    string Decision,
    string? ResolvedByUserId,
    string? Reason,
    string? TransportMessageId);

/// <summary>
/// Prompt provenance fact — tracks prompt lineage for message threading.
/// </summary>
public sealed record ChannelPromptProvenanceFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    string PromptSessionId,
    string PromptRootRunId,
    string? PromptText,
    string TransportMessageId);

/// <summary>
/// Queue view revision fact — queue view state revision for invalidation.
/// </summary>
public sealed record ChannelQueueViewRevisionFact(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    long Revision,
    string Signature);

/// <summary>
/// Action seed for ChannelHost to render as buttons — opaque capability tokens minted by Gateway.
/// </summary>
public sealed record ChannelCardActionSeed(
    string ActionId,
    string Kind,        // "remove" | "clear" | "page" | "sessions" | "new"
    string? SessionId,
    string? TurnId,
    int? Offset,
    string Token);

/// <summary>
/// Session facts envelope — single outbound intent carrying all session facts for one revision.
/// </summary>
public sealed record ChannelSessionFactsEnvelope(
    string Version,
    string ConversationId,
    string? ConversationThreadId,
    string View,
    int PageOffset,
    bool BringToFront,
    long Revision,
    ChannelSelectedSessionFact? Selected,
    ChannelActiveSessionsFact? Active,
    ChannelQueueFact? QueueFacts,
    IReadOnlyList<ChannelCardActionSeed> ActionsSeed);
