using Kautuka.Gateway.Infrastructure.Observability;

namespace Kautuka.Gateway.Domain.Stores;

internal static class RunWaitReasons
{
    public const string Approval = "approval";
    public const string Child = "child";
    public const string ExternalInput = "external_input";
}

internal static class RunWaitStatuses
{
    public const string Ready = "ready";
    public const string Executing = "executing";
    public const string PausedAgain = "paused_again";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

internal sealed record RunWaitState(
    string WaitId,
    string ContinuationRunId,
    string? RunId,
    string? TargetType,
    string? TargetId,
    string? SessionId,
    string? AgentId,
    string? UserId,
    string WaitReason,
    string? CorrelationId,
    string SessionStateJson,
    string BehaviorJson,
    string ArtifactManifestJson,
    string? ToolCallId,
    string? ToolName,
    string? ApprovalRequestId,
    string? ChildSessionId,
    string? ParentRunId,
    string? ChildRunId,
    string? TraceParent,
    string? TraceState,
    string PausedAt,
    string? CompletedAt,
    string CanonicalEventId,
    string? RootRunId = null,
    string Status = RunWaitStatuses.Ready,
    int AttemptCount = 0,
    string? LeaseOwner = null,
    string? LeaseExpiresAt = null,
    string? FailureCode = null);

internal sealed record RunWaitStateCreateRequest(
    string ContinuationRunId,
    string? RunId,
    string? TargetType,
    string? TargetId,
    string? SessionId,
    string? AgentId,
    string? UserId,
    string? CorrelationId,
    string SessionStateJson,
    string BehaviorJson,
    string ArtifactManifestJson,
    string CanonicalEventId,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ApprovalRequestId = null,
    string? ChildSessionId = null,
    string? ParentRunId = null,
    string? ChildRunId = null,
    string? RootRunId = null,
    AgentRunHostCallTracer.AgentRunTraceContext? TraceContext = null);

internal interface IRunWaitStateStore
{
    RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request);

    RunWaitState PauseRunForChild(RunWaitStateCreateRequest request);

    RunWaitState? GetByWaitId(string waitId);

    RunWaitState? GetActiveByContinuationRunId(string continuationRunId);

    RunWaitState? GetActiveByCorrelationId(string correlationId);

    RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId);

    bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration);

    void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration);

    void ReleaseForRetry(string waitId, string leaseOwner);

    void MarkPausedAgain(string waitId);

    /// <summary>
    /// Returns an executing approval wait to ready without requiring the original lease owner.
    /// Used when nested child HITL keeps the parent tool open but must release the outer wait.
    /// </summary>
    void ReleaseExecutingWait(string waitId);

    /// <summary>Settles an active wait. Repeated terminal cleanup preserves a closed wait's outcome; deleted waits remain deleted.</summary>
    void MarkCompleted(string waitId);

    /// <summary>Settles an active wait as failed. Does not overwrite an already closed wait or its original failure.</summary>
    void MarkFailed(string waitId, string failureCode);

    IReadOnlyList<RunWaitState> ListRecoverable();

    void DeleteBySession(string sessionId);

    /// <summary>
    /// Persists one packed-tool result on the wait envelope so a sibling decision can
    /// execute immediately without resuming the model. Default is a no-op for test fakes.
    /// </summary>
    void PutPackedToolResult(string waitId, string requestId, string resultJson, bool journaledStart)
    {
    }
}
