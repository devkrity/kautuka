namespace Kautuka.Gateway.Domain.Execution;

internal sealed record AgentToolInvocationContextState(
    string CallerSessionId,
    string CallerAgentId,
    string? UserId,
    string? CallerRunId = null,
    string? CallerToolCallId = null,
    string? RootRunId = null);

internal static class AgentToolInvocationContext
{
    private static readonly AsyncLocal<AgentToolInvocationContextState?> current = new();

    public static AgentToolInvocationContextState? Current => current.Value;

    public static IDisposable Push(
        string callerSessionId,
        string callerAgentId,
        string? userId,
        string? callerRunId = null,
        string? callerToolCallId = null,
        string? rootRunId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerAgentId);

        AgentToolInvocationContextState? previous = current.Value;
        current.Value = new AgentToolInvocationContextState(
            callerSessionId,
            callerAgentId,
            userId,
            callerRunId,
            callerToolCallId,
            rootRunId);
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(AgentToolInvocationContextState? previous) : IDisposable
    {
        public void Dispose()
        {
            current.Value = previous;
        }
    }
}
