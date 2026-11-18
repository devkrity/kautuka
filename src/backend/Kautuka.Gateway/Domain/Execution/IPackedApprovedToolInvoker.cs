namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Invokes one already-decided packed tool without asking the model to continue.
/// Agent-as-tool calls are not independent: they need the parent run running and a
/// journaled <c>tool.call.started</c> first.
/// </summary>
internal interface IPackedApprovedToolInvoker
{
    ValueTask<string> InvokePinnedAsync(string agentId, string sessionId, string? userId, string toolName, string? argumentsJson, EffectiveToolingSnapshot? snapshot, CancellationToken cancellationToken, string? connectorId = null)
        => InvokeAsync(agentId, sessionId, userId, toolName, argumentsJson, cancellationToken);
    ValueTask<string> InvokeAsync(
        string agentId,
        string sessionId,
        string? userId,
        string toolName,
        string? argumentsJson,
        CancellationToken cancellationToken);
}
