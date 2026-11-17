using System.Collections.Concurrent;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ApprovedToolExecution
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> active = new(StringComparer.Ordinal);

    public static async Task<string> RunAsync(
        IGatewayApprovalStore store, string requestId, string agentId, string sessionId, string? userId,
        string toolName, string? argumentsJson, Func<Task<string>> invoke, CancellationToken cancellationToken)
    {
        GatewayToolApprovalRequest? request = store.GetById(requestId);
        if (request is null || request.AgentId != agentId || request.SessionId != sessionId || request.UserId != userId
            || request.ToolName != toolName || request.ArgumentsJson != argumentsJson
            || (request.Decision != "approved" && request.Decision != "auto_approved"))
            throw new InvalidOperationException("The saved approval does not match this tool call. Start a new run.");

        var own = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> current = active.GetOrAdd(requestId, own);
        if (!ReferenceEquals(current, own)) return await current.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string result;
            if (!store.TryClaimToolExecution(requestId))
                result = store.GetToolExecutionResult(requestId) ?? PackedToolApproval.CreateOutcomeResultJson(
                    toolName, "error", "The call may have already run. Its outcome is unknown; check the result and start a new run.");
            else
            {
                result = await invoke().ConfigureAwait(false);
                if (!store.SaveToolExecutionResult(requestId, result))
                    throw new InvalidOperationException("The tool result could not be saved. Its outcome is unknown; check the result and start a new run.");
            }
            own.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            own.TrySetException(ex);
            throw;
        }
        finally { active.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(requestId, own)); }
    }
}
