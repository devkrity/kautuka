namespace Kautuka.Gateway.Domain.Stores;

internal static class AgentToolInvocationStates
{
    public const string WaitingOnChild = "waiting_on_child";
    public const string ParentCompletionPending = "parent_completion_pending";
    public const string CompletingParent = "completing_parent";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

internal sealed record AgentToolInvocation(
    string InvocationId,
    string ParentRunId,
    string ParentSessionId,
    string ParentToolCallId,
    string ParentToolName,
    string ChildRunId,
    string ChildSessionId,
    string ChildAgentId,
    string RootRunId,
    string State,
    string CreatedAt,
    string UpdatedAt,
    string? ParentContinuationRunId = null,
    string? CompletionLeaseOwner = null,
    string? CompletionLeaseExpiresAt = null,
    string? CompletedAt = null,
    string? FailureCode = null,
    string? ChildResultJson = null,
    string? ChildOutcome = null,
    string? ChildCompletedAt = null);

internal sealed record AgentToolInvocationCreateRequest(
    string ParentRunId,
    string ParentSessionId,
    string ParentToolCallId,
    string ParentToolName,
    string ChildRunId,
    string ChildSessionId,
    string ChildAgentId,
    string RootRunId,
    string? ParentContinuationRunId);

internal interface IAgentToolInvocationStore
{
    AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request);

    AgentToolInvocation? Get(string invocationId);

    AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId);

    IReadOnlyList<AgentToolInvocation> ListRecoverable();

    void RecordApprovalLink(AgentToolInvocationLink link);

    void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome);

    bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration);

    void MarkWaitingOnChild(string invocationId, string childRunId);

    void MarkCompleted(string invocationId, string leaseOwner);

    /// <summary>
    /// Idempotent terminal seal when authority/journal already proves parent completion.
    /// Does not require the exclusive completion lease.
    /// </summary>
    bool TrySealCompleted(string invocationId);

    void MarkFailed(string invocationId, string failureCode);
}
