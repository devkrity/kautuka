using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;

namespace Kautuka.Gateway.Domain.Models;

internal sealed record GatewayChannelRegistration(
    string Id,
    string ChannelType,
    string OwnerUserId,
    string ExternalBotId,
    bool IsActive,
    JsonObject Settings,
    string CreatedAt,
    string UpdatedAt);

internal sealed record GatewayChannelIdentityLink(
    string Id,
    string RegistrationId,
    string UserId,
    string ExternalUserId,
    string CreatedAt);

internal sealed record GatewayChannelTargetBinding(
    string Id,
    string RegistrationId,
    string TargetType,
    string TargetId,
    bool IsDefault,
    string CreatedAt);

internal sealed record GatewayChannelInteraction(
    string Id,
    string RegistrationId,
    string? UserId,
    string? TargetType,
    string? TargetId,
    string? SessionId,
    string? RunId,
    string ChatId,
    string? MessageThreadId,
    string Prompt,
    IReadOnlyList<GatewayChannelInteractionOption> Options,
    string Status,
    string CreatedAt,
    string ExpiresAt,
    string? ResolvedAt,
    string? ResolvedByExternalUserId,
    string? Resolution,
    string? ApprovalRequestId = null,
    long? SourceMessageId = null,
    string? SenderDisplayName = null,
    string? SourceTransportMessageId = null,
    string? ArgumentsJson = null,
    string? InteractionGroupId = null);

internal sealed record GatewayChannelInteractionOption(
    string Id,
    string Label,
    string Value,
    string Style = "default");

internal sealed record GatewaySession(
    string Id,
    string TargetType,
    string TargetId,
    string OwnerUserId,
    string Title,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<GatewayMessage> Messages,
    bool Compacted,
    string? CompactSummary,
    int? EstimatedContextTokens = null,
    [property: JsonIgnore] int? RawTranscriptEstimatedTokens = null,
    string? LastEstimatedAt = null,
    string? LastCompactedAt = null,
    string? LastCompactionReason = null,
    string? Originator = null,
    GatewayActiveRunState? ActiveRun = null,
    string? ArchivedAt = null,
    bool TitleUserSet = false);

internal sealed record GatewayActiveRunState(
    string RunId,
    string SessionId,
    string TargetType,
    string TargetId,
    string UserId,
    string? Message,
    long LastSequence,
    string StartedAt,
    string Status);

internal sealed record GatewayActiveRunSnapshot(
    GatewayActiveRunState ActiveRun,
    GatewayRunState? Run = null,
    IReadOnlyList<GatewayActivityEntry>? ActivityEntries = null,
    IReadOnlyList<GatewayToolApprovalRequest>? PendingApprovals = null);

internal sealed record GatewayChatSessionView(
    GatewayChatSessionViewSession Session,
    GatewayChatSessionRunView RunStatus,
    IReadOnlyList<GatewayChatTurnView> Turns,
    IReadOnlyList<ApprovalResponse> PendingApprovals);

internal sealed record GatewayChatSessionViewSession(
    string Id,
    string TargetType,
    string TargetId,
    string Title,
    string CreatedAt,
    string UpdatedAt,
    bool Compacted,
    string? CompactSummary,
    int? EstimatedContextTokens,
    string? LastEstimatedAt,
    string? LastCompactedAt,
    string? LastCompactionReason);

internal sealed record GatewayChatSessionRunView(
    string Phase,
    string Label,
    bool IsActive,
    string? ActiveRunId = null,
    long LastSequence = 0,
    string? StartedAt = null,
    string? WaitStateReason = null,
    string? ComposerLockReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GatewayChatSessionActiveRunView>? ActiveRuns = null);

internal sealed record GatewayChatSessionActiveRunView(
    string RunId,
    string Phase,
    string Label,
    bool IsActive,
    long LastSequence = 0,
    string? StartedAt = null);

internal sealed record GatewayChatTurnView(
    string Id,
    int Sequence,
    GatewayChatTurnMessageView? User,
    GatewayChatTurnMessageView? Assistant,
    GatewayRunState? Run = null,
    IReadOnlyList<GatewayActivityEntry>? ActivityEntries = null,
    IReadOnlyList<GatewayExecutedAction>? ExecutedActions = null,
    GatewayMetrics? Metrics = null,
    string? TraceId = null);

internal sealed record GatewayChatTurnMessageView(
    string Id,
    string Role,
    string Content,
    string Timestamp,
    string? Thinking = null,
    IReadOnlyList<GatewayConversationAsset>? Assets = null);

internal sealed record GatewayConversationAsset(
    string Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Origin,
    string Disposition,
    string DownloadUrl,
    string CreatedAt);

internal sealed record GatewayMessage(
    string Id,
    string Role,
    string Content,
    string Timestamp,
    bool Compact,
    string? Thinking,
    GatewayMetrics? Metrics,
    IReadOnlyList<GatewayToolCall>? ToolCalls = null,
    string? Source = null,
    IReadOnlyList<GatewayConversationAsset>? Assets = null,
    bool Internal = false,
    int Sequence = 0,
    string? TraceId = null,
    GatewayRunState? Run = null,
    IReadOnlyList<GatewayExecutedAction>? ExecutedActions = null,
    IReadOnlyList<GatewayActivityEntry>? ActivityEntries = null);

internal sealed record GatewayMetrics(
    int TokenIn,
    int TokenOut,
    int ElapsedMs);

internal sealed record GatewayUsageTurn(
    int TokenIn,
    int TokenOut);

internal static class GatewayRunPhases
{
    public const string Executing = "executing";
    public const string AwaitingToolApproval = "awaiting_tool_approval";
    public const string Completed = "completed";
    public const string Aborted = "aborted";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
}

internal sealed record GatewayRunState(
    string Phase,
    string? WaitStateReason = null,
    string? ContinuationRunId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

internal static class GatewayRuntimeRecordClasses
{
    public const string Message = "Message";
    public const string ToolCallEvent = "ToolCallEvent";
    public const string ActivityEntry = "ActivityEntry";
    public const string AuditEntry = "AuditEntry";
    public const string Approval = "Approval";
    public const string WaitState = "WaitState";
    public const string Checkpoint = "Checkpoint";
    public const string RunSnapshot = "RunSnapshot";
}

internal static class GatewayRuntimeResumePhases
{
    public const string Original = "original";
    public const string Paused = "paused";
    public const string Resumed = "resumed";
    public const string Completed = "completed";
}

internal sealed record GatewayExecutedAction(
    string Id,
    string Kind,
    string Name,
    string Status,
    IReadOnlyList<string>? ToolCallIds = null,
    string? ApprovalRequestId = null,
    string? ContinuationRunId = null,
    string? ArgumentsJson = null,
    string? ResultJson = null,
    string? StartedAt = null,
    string? CompletedAt = null);

internal sealed record GatewayActivityEntry(
    string Id,
    int Sequence,
    string Kind,
    string Status,
    string? ActionKind = null,
    string? Name = null,
    IReadOnlyList<string>? ToolCallIds = null,
    string? ToolCallId = null,
    string? ApprovalRequestId = null,
    string? ArgumentsJson = null,
    string? ResultJson = null,
    string? StartedAt = null,
    string? CompletedAt = null,
    string? RequestedAt = null,
    string? DecidedAt = null,
    string? ResolvedVia = null,
    int? CheckpointIndex = null,
    string? CheckpointId = null,
    string? CheckpointBoundaryType = null,
    string? CheckpointCreatedAt = null,
    bool CheckpointResumable = false,
    string? ParentToolCallId = null);

internal sealed record GatewayToolCall(
    string ToolCallId,
    string ToolName,
    string? AgentId,
    string? ArgumentsJson,
    string? ResultJson,
    string Status,
    string ActionKind,
    string? ParentToolCallId = null,
    string? StartedAt = null,
    string? CompletedAt = null,
    [property: JsonIgnore] long AttemptOrdinal = 0,
    [property: JsonIgnore] long? TerminalOrdinal = null);

internal sealed record GatewayRun(
    string Id,
    string UserId,
    string TargetType,
    string TargetId,
    string? SessionId,
    string? InteractionId,
    string Status,
    Dictionary<string, object?> Input,
    Dictionary<string, object?> Output,
    EffectiveRunBehavior? EffectiveBehavior,
    string? Error,
    string StartedAt,
    string EndedAt,
    int DurationMs,
    string? TraceId = null);

internal sealed record GatewayMemoryFact(
    string Id,
    string TargetType,
    string TargetId,
    string OwnerUserId,
    string Title,
    string Content,
    string Category,
    string Source,
    double Confidence,
    string CreatedAt,
    string UpdatedAt,
    string? SessionId = null);

internal sealed record GatewayChatResult(
    string TargetType,
    string TargetId,
    string SessionId,
    string Response,
    bool Compacted,
    GatewayMetrics Metrics,
    IReadOnlyList<GatewayToolCall>? ToolCalls = null,
    string? Thinking = null,
    IReadOnlyList<GatewayConversationAsset>? Assets = null,
    EffectiveRunBehavior? EffectiveBehavior = null,
    GatewayCapturedUsageMetrics? Usage = null,
    string? TraceId = null,
    GatewayRunState? Run = null,
    IReadOnlyList<GatewayExecutedAction>? ExecutedActions = null,
    IReadOnlyList<GatewayActivityEntry>? ActivityEntries = null,
    IReadOnlyList<GatewayUsageTurn>? UsageTurns = null);

internal abstract record GatewayChatStreamEvent;

internal sealed record GatewayChatSessionReady(string SessionId) : GatewayChatStreamEvent;

internal sealed record GatewayChatTokenDelta(string Delta, bool IsThinking) : GatewayChatStreamEvent;

internal sealed record GatewayChatToolCall(
    string ToolCallId,
    string ToolName,
    string? ArgumentsJson,
    string ActionKind,
    string? ParentToolCallId = null,
    string? NestedAgentId = null,
    string? NestedSessionId = null,
    string? ParentMessageId = null) : GatewayChatStreamEvent;

internal sealed record GatewayChatToolResult(string ToolCallId, string? ResultJson) : GatewayChatStreamEvent;

internal sealed record GatewayChatViewOpened(string ToolCallId, string ViewJson) : GatewayChatStreamEvent;

internal sealed record GatewayChatToolFailure(
    string ToolCallId,
    string? ToolName,
    string Code,
    string Message) : GatewayChatStreamEvent
{
    public const string ExecutionFailedCode = "tool_execution_failed";
    public const string ExecutionFailedMessage = "Tool execution failed.";

    public static GatewayChatToolFailure ExecutionFailed(string toolCallId, string? toolName)
        => new(toolCallId, toolName, ExecutionFailedCode, ExecutionFailedMessage);
}

/// <summary>Domain: root/nested run admitted. Maps to <c>run.started</c>.</summary>
internal sealed record GatewayChatRunStarted(
    string Trigger,
    string? StageTransitionId = null,
    string? ParentRunId = null,
    string? ParentToolCallId = null) : GatewayChatStreamEvent;

/// <summary>Domain: user turn recorded for the session. Maps to <c>message.user.recorded</c>.</summary>
internal sealed record GatewayChatUserMessageRecorded(
    string MessageId,
    string Content,
    IReadOnlyList<string>? AttachmentIds = null) : GatewayChatStreamEvent;

/// <summary>
/// Domain: open assistant text stream (messageId from lifecycle). Maps to <c>message.text.started</c>.
/// Prefer omitting this when first non-thinking token can open the stream; used for early/deny paths.
/// </summary>
internal sealed record GatewayChatMessageTextStarted : GatewayChatStreamEvent;

internal sealed record ContinuationArtifactManifestEntry(
    string AssetId,
    string FileName,
    string ContentType);

internal sealed record GatewayChatContinuationCheckpoint(
    string CheckpointId,
    string ContinuationRunId,
    int CheckpointIndex,
    string BoundaryType,
    string ResumeToken,
    IReadOnlyList<ContinuationArtifactManifestEntry> ArtifactManifest) : GatewayChatStreamEvent;

internal sealed record GatewayChatContinuationResumed(
    string CheckpointId,
    string ContinuationRunId,
    int CheckpointIndex,
    string BoundaryType,
    string WaitId) : GatewayChatStreamEvent;

/// <summary>
/// One blocking interrupt pause for a run. Packed HITL still uses a single pause;
/// per-tool cards live in the approval store.
/// </summary>
internal sealed record GatewayChatRunPaused(string WaitId, string InterruptId) : GatewayChatStreamEvent;

internal sealed record PackedApprovalInterruptMember(
    string RequestId,
    string ToolCallId,
    string ToolName,
    string? ArgumentsJson,
    string ToolType);

internal sealed record GatewayChatApprovalRequest(
    string RequestId,
    string ToolCallId,
    string ToolName,
    string? ArgumentsJson,
    string? RunId = null,
    string? CheckpointId = null,
    string? McpServerId = null,
    string ToolType = "mcp",
    string? ContinuationRunId = null,
    string? OuterSessionId = null,
    string? OuterAgentId = null,
    string? TraceId = null,
    string? ExpiresAt = null,
    string? AgentId = null,
    string? SessionId = null,
    bool PauseRun = true,
    IReadOnlyList<PackedApprovalInterruptMember>? Members = null) : GatewayChatStreamEvent;

internal sealed record GatewayChatRunPhase(
    string Phase,
    string? Reason = null) : GatewayChatStreamEvent;

/// <summary>
/// A persisted tool approval request stored in tool_approval_requests.
/// Decision is null when pending, "approved", "denied", or "expired" when resolved.
/// </summary>
internal sealed record GatewayToolApprovalRequest(
    string Id,
    string? SessionId,
    string? RunId,
    string? CheckpointId,
    string? AgentId,
    string? UserId,
    string ToolCallId,
    string ToolName,
    string ToolType,
    string? McpServerId,
    string? ArgumentsJson,
    string RequestedAt,
    string? Decision,
    string? DecidedBy,
    string? DecidedAt,
    string? Reason,
    string? ExpiresAt,
    string? ChannelTetherMode = null,
    string? OuterSessionId = null,
    string? OuterAgentId = null,
    string? ResolutionSource = null,
    long? AgentDefinitionRevision = null,
    string Kind = "tool",
    string? WorkflowId = null,
    string? InstanceId = null,
    string? NodeId = null);

/// <summary>
/// A single callable action on an approval request (e.g. "Approve", "Deny", "Start Run").
/// Value maps to the resolution verb used when calling resolve endpoint or channel callback.
/// </summary>
internal sealed record ApprovalAction(
    string Label,
    string Value,
    string Style = "default",
    bool RequiresRunId = false);

internal static class ApprovalActionCatalog
{
    public const string Approve = "approve";
    public const string Deny = "deny";
    public const string RequestChanges = "request_changes";
    public const string StopRun = "stop_run";
    public const string Expired = "expired";

    /// <summary>
    /// Canonical HITL actions: approve (continue happy path), deny (tool outcome to LLM), stop run (terminate this run).
    /// Run-scoped "approve all" is intentionally not offered — configure tools/skills for long-running trust.
    /// </summary>
    public static ApprovalAction[] ForToolType(string toolType) =>
        string.Equals(toolType, "autopilot_prerun", StringComparison.Ordinal)
            ?
            [
                new("Start Run", Approve, "primary"),
                new("Stop Run", StopRun, "danger", RequiresRunId: true),
            ]
            :
            string.Equals(toolType, "workflow_review", StringComparison.Ordinal)
            ?
            [
                new("Approve", Approve, "primary"),
                new("Request changes", RequestChanges, "default"),
                new("Stop", StopRun, "danger"),
            ]
            :
            [
                new("Approve", Approve, "primary"),
                new("Deny", Deny, "danger"),
                new("Stop Run", StopRun, "danger", RequiresRunId: true),
            ];

    public static string Resolve(bool approved, bool expired, string? reason, bool stopRun, bool trustThisRun = false)
    {
        _ = trustThisRun; // obsolete; keep parameter so callers compile until fully removed
        return expired
            ? Expired
            : approved
                ? Approve
                : stopRun || string.Equals(reason, "stopped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase)
                    ? StopRun
                    : string.Equals(reason, RequestChanges, StringComparison.OrdinalIgnoreCase)
                        ? RequestChanges
                        : Deny;
    }

    public static bool TryMapDecision(string? decision, out bool approved, out bool stopRun, out string action)
    {
        approved = false;
        stopRun = false;
        action = Deny;
        if (string.IsNullOrWhiteSpace(decision))
        {
            return false;
        }

        if (string.Equals(decision, Approve, StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase))
        {
            approved = true;
            action = Approve;
            return true;
        }

        if (string.Equals(decision, RequestChanges, StringComparison.OrdinalIgnoreCase))
        {
            action = RequestChanges;
            return true;
        }

        if (string.Equals(decision, StopRun, StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "stop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            stopRun = true;
            action = StopRun;
            return true;
        }

        if (string.Equals(decision, Deny, StringComparison.OrdinalIgnoreCase)
            || string.Equals(decision, "denied", StringComparison.OrdinalIgnoreCase))
        {
            action = Deny;
            return true;
        }

        return false;
    }

    public static string DecisionFromFlags(bool approved, bool stopRun)
        => stopRun ? StopRun : approved ? Approve : Deny;

    public static string Label(string action)
        => action switch
        {
            Approve => "Approved",
            RequestChanges => "Changes requested",
            StopRun => "Stopped",
            Expired => "Expired",
            _ => "Denied",
        };

    public static string StorageDecision(string action)
        => action switch
        {
            Approve => "approved",
            RequestChanges => "request_changes",
            StopRun => "stopped",
            Expired => "expired",
            _ => "denied",
        };
}

/// <summary>
/// Wire-response DTO for approval requests. Extends the persisted record with a computed
/// Actions list so all consumers (UI, channels, TUI) render the same options without
/// each independently deciding which buttons apply per toolType.
/// </summary>
internal sealed record ApprovalResponse(
    string Id,
    string? SessionId,
    string? RunId,
    string? CheckpointId,
    string? AgentId,
    string? AgentName,
    string? UserId,
    string ToolCallId,
    string ToolName,
    string ToolType,
    string? McpServerId,
    string? ArgumentsJson,
    string RequestedAt,
    string? Decision,
    string? DecidedBy,
    string? DecidedAt,
    string? Reason,
    string? ExpiresAt,
    ApprovalAction[] Actions,
    string? OuterSessionId = null,
    string? OuterAgentId = null,
    string? OuterAgentName = null,
    string? DecidedByDisplayName = null,
    string? ResolutionSource = null,
    string? CallerToolCallId = null,
    string? CallerToolName = null,
    int? CheckpointIndex = null,
    string? CheckpointBoundaryType = null,
    string? CheckpointCreatedAt = null,
    bool CheckpointResumable = false,
    string Kind = "tool",
    string? WorkflowId = null,
    string? InstanceId = null,
    string? NodeId = null,
    int? RemainingChangeRequests = null)
{
    internal static ApprovalResponse From(
        GatewayToolApprovalRequest r,
        string? agentName = null,
        string? outerAgentName = null,
        string? decidedByDisplayName = null,
        string? resolutionSource = null,
        AgentToolInvocationLink? invocationLink = null,
        GatewayContinuationCheckpoint? checkpoint = null,
        ApprovalAction[]? actions = null,
        int? remainingChangeRequests = null) => new(
        r.Id, r.SessionId, r.RunId, r.CheckpointId, r.AgentId, agentName, r.UserId,
        r.ToolCallId, r.ToolName, r.ToolType, r.McpServerId, r.ArgumentsJson,
        r.RequestedAt, r.Decision, r.DecidedBy, r.DecidedAt, r.Reason, r.ExpiresAt,
        actions ?? ComputeActions(r.ToolType), r.OuterSessionId, r.OuterAgentId, outerAgentName,
        decidedByDisplayName, resolutionSource, invocationLink?.CallerToolCallId,
        invocationLink?.CallerToolName, checkpoint?.CheckpointIndex, checkpoint?.BoundaryType,
        checkpoint?.CreatedAt, checkpoint is not null,
        r.Kind, r.WorkflowId, r.InstanceId, r.NodeId, remainingChangeRequests);

    internal static ApprovalAction[] ComputeActions(string toolType)
        => ApprovalActionCatalog.ForToolType(toolType);
}

internal sealed record HitlApprovalStatItem(
    string ToolName,
    string ToolType,
    int Approved,
    int Denied,
    int AgentCount,
    string? McpServerId = null,
    string? SkillId = null,
    string? LastDeniedAt = null,
    long? AgentDefinitionRevision = null);

internal sealed record HitlApprovalStatsResponse(
    int WindowDays,
    string Since,
    HitlApprovalStatItem[] Items);

internal sealed record GatewayChatAgentTransition(string AgentId) : GatewayChatStreamEvent;

internal sealed record GatewayChatWorkflowEvent(string EventType, string? ExecutorId = null) : GatewayChatStreamEvent;

internal sealed record GatewayChatStreamDone(GatewayChatResult Result) : GatewayChatStreamEvent;

/// <summary>
/// Produce loop ended on a non-terminal boundary. Parent run stays <c>running</c>;
/// no Narad <c>run.paused</c>, <c>run.resumed</c>, or terminal is published.
/// Used for AAAT nested-child waits where only durable wait-state + open tool call remain.
/// </summary>
internal sealed record GatewayChatProduceBoundarySuspended(string BoundaryReason) : GatewayChatStreamEvent;


internal sealed class GatewaySystemSettings
{
    public string LogLevel { get; init; } = "WARNING";

    public string ReleaseChannel { get; init; } = "stable";

    public string Locale { get; init; } = "en-US";

    public string TimeZone { get; init; } = "UTC";

    public bool IncludeBinaryContent { get; init; } = true;

    public bool CaptureMessageContent { get; init; }

    public string WorkspaceBaseRoot { get; init; } = string.Empty;

    public string? SandboxImage { get; init; }

    public int SandboxDefaultCpus { get; init; }

    public string? SandboxDefaultMemory { get; init; }

    public int SandboxDefaultTimeout { get; init; }

    public bool DocumentIngestionEnabled { get; init; } = true;

    public string DocumentIngestionInternalParserConnectorId { get; init; } = "markitdown";

    public string DocumentIngestionInternalParserToolName { get; init; } = "convert_to_markdown";

    public int DocumentIngestionParserTimeoutSeconds { get; init; } = 30;

    public string DocumentIngestionParseRequiredExtensions { get; init; } = ".pdf,.docx,.pptx,.xlsx";

    public bool DocumentIngestionPdfImageFallbackEnabled { get; init; }

    public bool DocumentIngestionVisionRouteAllowed { get; init; }

    public int ModelTimeoutSeconds { get; init; } = 300;

    public int MaxRunDurationSeconds { get; init; }

    public int HitlExpirySeconds { get; init; }

    public string HitlExpiryAction { get; init; } = "abort";

    public bool AllowMultipleToolCalls { get; init; } = true;

    public bool AllowConcurrentInvocation { get; init; }

    public int MaxParallelTools { get; init; }

    public static GatewaySystemSettings FromConfiguration(PlatformConfiguration configuration) => new()
    {
        LogLevel = configuration.System.LogLevel,
        ReleaseChannel = "stable",
        Locale = configuration.System.Locale,
        TimeZone = configuration.System.TimeZone,
        IncludeBinaryContent = configuration.System.Observability.IncludeBinaryContent,
        CaptureMessageContent = configuration.System.Observability.CaptureMessageContent,
        WorkspaceBaseRoot = string.Empty,
        SandboxImage = configuration.Kautuka.Sandbox.Image,
        SandboxDefaultCpus = configuration.Kautuka.Sandbox.DefaultCpus,
        SandboxDefaultMemory = configuration.Kautuka.Sandbox.DefaultMemory,
        SandboxDefaultTimeout = configuration.Kautuka.Sandbox.DefaultTimeout,
        ModelTimeoutSeconds = 300,
        MaxRunDurationSeconds = 0,
        HitlExpirySeconds = 0,
        HitlExpiryAction = "abort",
        AllowMultipleToolCalls = true,
        AllowConcurrentInvocation = false,
        MaxParallelTools = 0,
    };
}

internal sealed class GatewayOrganizationSettings
{
    public string? Name { get; init; }
    public string? Industry { get; init; }
}

internal sealed class GatewayProviderPathPolicySettings
{
    public double ToolNativePromotionThreshold { get; init; } = 0.80;
    public int ToolMinObservations { get; init; } = 5;
    public double ToolNativeDemotionThreshold { get; init; } = 0.40;
    public double StructuredJsonStrictPromotionThreshold { get; init; } = 0.90;
    public int StructuredJsonMinObservations { get; init; } = 5;
    public double VisionEnableThreshold { get; init; } = 0.80;
    public int ObservationDecayWindowDays { get; init; } = 30;
    public bool ProviderPathFallbackModeEnabled { get; init; } = true;

    public static GatewayProviderPathPolicySettings FromConfiguration(PlatformConfiguration configuration)
    {
        var p = configuration.ProviderPathPolicy;
        return new GatewayProviderPathPolicySettings
        {
            ToolNativePromotionThreshold = p.ToolNativePromotionThreshold,
            ToolMinObservations = p.ToolMinObservations,
            ToolNativeDemotionThreshold = p.ToolNativeDemotionThreshold,
            StructuredJsonStrictPromotionThreshold = p.StructuredJsonStrictPromotionThreshold,
            StructuredJsonMinObservations = p.StructuredJsonMinObservations,
            VisionEnableThreshold = p.VisionEnableThreshold,
            ObservationDecayWindowDays = p.ObservationDecayWindowDays,
            ProviderPathFallbackModeEnabled = p.ProviderPathFallbackModeEnabled,
        };
    }
}

internal sealed class GatewayMemorySettings
{
    public bool EmbeddingsEnabled { get; init; }
    public double FactPersistThreshold { get; init; } = 0.70;
    public double FactInjectThreshold { get; init; } = 0.60;
    public int MaxInjectedFacts { get; init; } = 10;
    public int InjectionTokenBudget { get; init; } = 1024;
    public string? SessionCompactionModelId { get; init; }

    public static GatewayMemorySettings FromConfiguration(PlatformConfiguration configuration)
    {
        var memory = configuration.Memory;
        return new GatewayMemorySettings
        {
            EmbeddingsEnabled = memory.EmbeddingsEnabled,
            FactPersistThreshold = memory.FactPersistThreshold,
            FactInjectThreshold = memory.FactInjectThreshold,
            MaxInjectedFacts = memory.MaxInjectedFacts,
            InjectionTokenBudget = memory.InjectionTokenBudget,
            SessionCompactionModelId = memory.SessionCompactionModelId,
        };
    }
}
