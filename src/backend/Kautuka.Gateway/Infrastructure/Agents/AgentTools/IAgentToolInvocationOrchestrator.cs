using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Infrastructure.Agents.AgentTools;

internal interface IAgentToolInvocationOrchestrator
{
    /// <summary>
    /// Invokes <paramref name="targetAgentId"/> headlessly with <paramref name="message"/>
    /// on behalf of <paramref name="callerAgentId"/>. Returns a structured tool-style result.
    /// </summary>
    ValueTask<object?> InvokeAsync(
        string callerAgentId,
        string targetAgentId,
        string message,
        string? userId,
        string callerSessionId,
        AgentToolCallerContextState callerContext,
        CancellationToken ct,
        TimeSpan maxRunDuration = default);
}
