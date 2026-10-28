using System.Collections.Concurrent;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record AgentToolCallerContextState(
    string CallerRunId,
    string? CallerContinuationRunId,
    string CallerToolCallId,
    string CallerToolName,
    string CallerSessionId,
    string CallerAgentId,
    string? UserId,
    string? RootRunId = null,
    string? PreExecSessionStateJson = null,
    string? PreExecBehaviorJson = null);

internal sealed record AgentToolApprovalSuspension(
    GatewayChatApprovalRequest ApprovalRequest,
    AgentToolCallerContextState CallerContext);

internal static class AgentToolCallerContext
{
    private static readonly AsyncLocal<AgentToolCallerContextState?> current = new();
    private static readonly ConcurrentDictionary<string, AgentToolCallerContextState> activeByCallerSessionAndCall = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, AgentToolApprovalSuspension> pendingApprovalsByCallerRunAndCall = new(StringComparer.Ordinal);

    public static AgentToolCallerContextState? Current => current.Value;

    public static AgentToolCallerContextState? TryGetRegistered(string callerSessionId, string callerToolCallId)
    {
        return !string.IsNullOrWhiteSpace(callerSessionId)
            && !string.IsNullOrWhiteSpace(callerToolCallId)
            && activeByCallerSessionAndCall.TryGetValue(BuildSessionCallKey(callerSessionId, callerToolCallId), out AgentToolCallerContextState? exact)
                ? exact
                : null;
    }

    public static void RegisterPendingApproval(
        GatewayChatApprovalRequest approvalRequest,
        AgentToolCallerContextState callerContext)
    {
        ArgumentNullException.ThrowIfNull(approvalRequest);
        ArgumentNullException.ThrowIfNull(callerContext);

        string key = BuildRunCallKey(callerContext.CallerRunId, callerContext.CallerToolCallId);
        // Continuation replay can reach the same AAAT boundary twice; replace the prior
        // process-local suspension instead of failing the parent tool invocation.
        pendingApprovalsByCallerRunAndCall[key] = new AgentToolApprovalSuspension(approvalRequest, callerContext);
    }

    public static bool TryTakePendingApproval(
        string callerRunId,
        string callerToolCallId,
        out AgentToolApprovalSuspension? suspension)
        => pendingApprovalsByCallerRunAndCall.TryRemove(
            BuildRunCallKey(callerRunId, callerToolCallId),
            out suspension);

    public static IDisposable Push(
        string callerRunId,
        string? callerContinuationRunId,
        string callerToolCallId,
        string callerToolName,
        string callerSessionId,
        string callerAgentId,
        string? userId,
        string? rootRunId = null,
        string? preExecSessionStateJson = null,
        string? preExecBehaviorJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerAgentId);

        AgentToolCallerContextState? previous = current.Value;
        var state = new AgentToolCallerContextState(
            callerRunId,
            callerContinuationRunId,
            callerToolCallId,
            callerToolName,
            callerSessionId,
            callerAgentId,
            userId,
            rootRunId,
            preExecSessionStateJson,
            preExecBehaviorJson);
        current.Value = state;
        activeByCallerSessionAndCall[BuildSessionCallKey(callerSessionId, callerToolCallId)] = state;
        return new RestoreScope(previous, state);
    }

    private static string BuildSessionCallKey(string callerSessionId, string callerToolCallId)
        => $"{callerSessionId}\u001f{callerToolCallId}";

    private static string BuildRunCallKey(string callerRunId, string callerToolCallId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerToolCallId);
        return $"{callerRunId}\u001f{callerToolCallId}";
    }

    private sealed class RestoreScope(AgentToolCallerContextState? previous, AgentToolCallerContextState state) : IDisposable
    {
        public void Dispose()
        {
            current.Value = previous;
            string callerCallKey = BuildSessionCallKey(state.CallerSessionId, state.CallerToolCallId);
            if (activeByCallerSessionAndCall.TryGetValue(callerCallKey, out AgentToolCallerContextState? currentForCall)
                && ReferenceEquals(currentForCall, state))
            {
                activeByCallerSessionAndCall.TryRemove(callerCallKey, out _);
            }
        }
    }
}
