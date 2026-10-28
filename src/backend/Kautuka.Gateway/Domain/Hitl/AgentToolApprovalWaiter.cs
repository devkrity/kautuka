using System.Collections.Concurrent;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

internal interface IAgentToolApprovalWaiter
{
    bool IsWaiting(string requestId);

    ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken);
}

internal sealed class AgentToolApprovalWaiter(IGatewayApprovalStore approvalStore) : IAgentToolApprovalWaiter, INotificationHandler<HitlApprovalResolved>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<HitlApprovalResolved>> pending = new(StringComparer.Ordinal);

    public bool IsWaiting(string requestId)
        => !string.IsNullOrWhiteSpace(requestId) && pending.ContainsKey(requestId);

    public async ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var completion = new TaskCompletionSource<HitlApprovalResolved>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException($"An agent-tool approval waiter already exists for request '{requestId}'.");
        }

        try
        {
            GatewayToolApprovalRequest? alreadyResolved = approvalStore.GetById(requestId);
            if (alreadyResolved?.Decision is { Length: > 0 } decision)
            {
                bool expired = string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase);
                bool stopRun = string.Equals(decision, "stopped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(alreadyResolved.Reason, HitlExpiryReasons.Abort, StringComparison.Ordinal)
                    || string.Equals(alreadyResolved.Reason, "stopped", StringComparison.OrdinalIgnoreCase);
                return new HitlApprovalResolved(
                    requestId,
                    string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase),
                    alreadyResolved.Reason,
                    alreadyResolved.DecidedBy,
                    expired ? HitlResolutionSource.SystemTimeout : HitlResolutionSource.ApprovalApi,
                    alreadyResolved.SessionId,
                    string.IsNullOrWhiteSpace(alreadyResolved.AgentId) ? null : "agent",
                    alreadyResolved.AgentId,
                    alreadyResolved.UserId,
                    StopRun: stopRun);
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(requestId, out _);
        }
    }

    public ValueTask Handle(HitlApprovalResolved notification, CancellationToken cancellationToken)
    {
        if (pending.TryGetValue(notification.RequestId, out TaskCompletionSource<HitlApprovalResolved>? completion))
        {
            completion.TrySetResult(notification);
        }

        return ValueTask.CompletedTask;
    }
}
