using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{
    internal static bool RequiresExecutionDependencies(ToolApprovalResponseInfo? approvalResponse)
        => approvalResponse is null
            || (approvalResponse.Approved && !approvalResponse.StopRun);

    internal static async Task<AgentExecutionReadiness?> GetExecutionReadinessFailureAsync(
        ToolApprovalResponseInfo? approvalResponse,
        IAgentExecutionReadinessService readinessService,
        CancellationToken cancellationToken)
    {
        if (!RequiresExecutionDependencies(approvalResponse))
            return null;

        AgentExecutionReadiness readiness = await readinessService.CheckAsync(cancellationToken).ConfigureAwait(false);
        return readiness.Ready ? null : readiness;
    }

    private async Task<NaradPersistedEvent[]?> CreateReadinessFailureEventsAsync(
        ToolApprovalResponseInfo? approvalResponse,
        string agentId,
        string sessionId,
        NaradEventContext runtimeEventContext,
        AssistantMessageLifecycle messageLifecycle,
        AgentExecutionEventWriter eventWriter,
        CancellationToken cancellationToken)
    {
        // Terminal control-plane decisions remain resolvable during an outage. Every path that
        // can execute an assistant is guarded here before runtime construction.
        AgentExecutionReadiness? readiness = await GetExecutionReadinessFailureAsync(
            approvalResponse,
            executionReadinessService,
            cancellationToken).ConfigureAwait(false);
        if (readiness is null)
            return null;

        // Domain terminal through Writer — not a second Narad invent path.
        GatewayChatStreamDone failedDone = new(new GatewayChatResult(
            "agent",
            agentId,
            sessionId,
            readiness.Message,
            false,
            new GatewayMetrics(0, 0, 0),
            Run: new GatewayRunState(
                GatewayRunPhases.Failed,
                ErrorCode: readiness.Code,
                ErrorMessage: readiness.Message)));

        IReadOnlyList<NaradPersistedEvent> mapped = await eventWriter
            .MapAsync(failedDone, runtimeEventContext, messageLifecycle, cancellationToken)
            .ConfigureAwait(false);

        if (mapped.Count == 0)
        {
            return null;
        }

        return mapped.ToArray();
    }
}
