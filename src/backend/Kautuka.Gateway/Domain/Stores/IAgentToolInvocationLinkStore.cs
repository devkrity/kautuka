namespace Kautuka.Gateway.Domain.Stores;

internal sealed record AgentToolInvocationLink(
    string ApprovalRequestId,
    string CallerSessionId,
    string CreatedAt,
    string UpdatedAt,
    string CanonicalEventId,
    string? CallerToolCallId = null,
    string? CallerToolName = null,
    string? CallerContinuationRunId = null,
    string? ChildSessionId = null,
    string? CallerRunId = null,
    string? ChildRunId = null,
    string? ChildAgentId = null,
    string? RootRunId = null,
    string? InvocationId = null);

internal interface IAgentToolInvocationLinkStore
{
    void Upsert(AgentToolInvocationLink link);

    AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId);

    IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId);

    AgentToolInvocationLink? GetByCallerSessionAndToolCallId(string callerSessionId, string callerToolCallId)
        => ListByCallerSession(callerSessionId)
            .LastOrDefault(link => string.Equals(link.CallerToolCallId, callerToolCallId, StringComparison.Ordinal));

    AgentToolInvocationLink? GetByChildSessionId(string childSessionId);

    IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId);

    bool HasLinksForSession(string sessionId);
}
