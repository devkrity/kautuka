using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Domain.Hitl;

internal interface IApprovalResolutionSideEffectApplier
{
    ValueTask ApplyAsync(ApprovalResolutionEventWork work, CancellationToken cancellationToken);
}

/// <summary>
/// Applies durable root-scoped stop and trust side effects idempotently from staged resolution outbox rows.
/// </summary>
internal sealed class ApprovalResolutionSideEffectApplier(
    IRunStopCommandService runStopCommandService) : IApprovalResolutionSideEffectApplier
{
    public async ValueTask ApplyAsync(ApprovalResolutionEventWork work, CancellationToken cancellationToken)
    {
        if (work.StopRun
            && !string.IsNullOrWhiteSpace(work.Context.RootRunId)
            && !string.IsNullOrWhiteSpace(work.StopThreadId))
        {
            await runStopCommandService.RequestStopAsync(
                new RunStopCommand(
                    work.Context.AgentId,
                    work.StopThreadId,
                    work.Context.RootRunId,
                    work.Context.UserId,
                    work.Actor ?? work.Context.UserId,
                    work.StopChannel ?? "system"),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
