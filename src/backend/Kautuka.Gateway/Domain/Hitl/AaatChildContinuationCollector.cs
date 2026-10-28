using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed record AaatChildContinuationResult(
    string OutputText,
    NaradPersistedEvent? PendingApprovalEvent,
    NaradPersistedEvent? PauseEvent);

internal sealed class AaatChildContinuationCollector(
    IAgentToolInvocationStore invocationStore,
    IGatewayApprovalStore approvalStore,
    string? expectedNegativeApprovalToolCallId = null)
{
    private readonly StringBuilder output = new();
    private readonly ChildRunToolOutcomeTracker childToolOutcomes = new(expectedNegativeApprovalToolCallId);
    private GatewayChatResult? finalResult;
    private string? terminalEventType;
    private string? lastToolResult;

    public NaradPersistedEvent? PendingApprovalEvent { get; private set; }
    public NaradPersistedEvent? PauseEvent { get; private set; }

    public async Task<AaatChildContinuationResult> CollectAsync(
        IAsyncEnumerable<NaradPersistedEvent> stream,
        AgentToolInvocationLink invocationLink,
        string childSessionId,
        CancellationToken cancellationToken)
    {
        await foreach (NaradPersistedEvent evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            childToolOutcomes.Observe(evt);
            if (string.Equals(evt.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
            {
                GatewayChatApprovalRequest approvalRequest = NaradInterruptObservation.ResolveChatApprovalRequest(
                    evt,
                    approvalStore,
                    fallbackAgentId: invocationLink.ChildAgentId,
                    fallbackSessionId: childSessionId);
                string timestamp = DateTimeOffset.UtcNow.ToString("O");
                invocationStore.RecordApprovalLink(invocationLink with
                {
                    ApprovalRequestId = approvalRequest.RequestId,
                    ChildSessionId = childSessionId,
                    ChildRunId = approvalRequest.RunId ?? evt.RunId ?? NaradStreamObservation.ReadString(evt, "runId"),
                    CanonicalEventId = evt.EventId,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp,
                });
                PendingApprovalEvent = evt;
                continue;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.MessageTextDelta, StringComparison.Ordinal))
            {
                GatewayChatTokenDelta delta = NaradStreamObservation.ReadBodyPayload(evt, KautukaJsonContext.Default.GatewayChatTokenDelta);
                output.Append(delta.Delta);
            }

            if (string.Equals(evt.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal))
            {
                JsonElement body = NaradPersistedEventReader.ParseBody(evt);
                if (string.Equals(NaradPersistedEventReader.String(body, "outcome"), "success", StringComparison.Ordinal))
                {
                    GatewayChatToolResult toolResult = JsonSerializer.Deserialize(body, KautukaJsonContext.Default.GatewayChatToolResult)
                        ?? throw new InvalidOperationException("Tool result payload is invalid.");
                    if (!string.IsNullOrWhiteSpace(toolResult.ResultJson))
                    {
                        lastToolResult = toolResult.ResultJson;
                    }
                }

                continue;
            }

            if (evt.EventType is NaradEventTypes.RunFinished
                or NaradEventTypes.RunError
                or NaradEventTypes.RunPaused)
            {
                ObserveTerminal(evt);
                break;
            }
        }

        return ToResult();
    }

    public void ObserveTerminal(NaradPersistedEvent terminalEvent)
    {
        GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(
            terminalEvent,
            "agent",
            NaradStreamObservation.ReadString(terminalEvent, "agentId") ?? "agent",
            terminalEvent.SessionId);
        terminalEventType = terminalEvent.EventType;
        finalResult = done.Result;
        if (string.Equals(terminalEvent.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
        {
            PauseEvent = terminalEvent;
        }
    }

    public AaatChildContinuationResult ToResult()
    {
        if (string.Equals(terminalEventType, NaradEventTypes.RunError, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The child agent run failed and cannot complete the parent tool invocation.");
        }

        if (string.Equals(terminalEventType, NaradEventTypes.RunPaused, StringComparison.Ordinal)
            && PendingApprovalEvent is null)
        {
            throw new InvalidOperationException("The child agent run paused without producing an approval boundary.");
        }

        if (childToolOutcomes.HasUnresolvedFailures)
        {
            throw new InvalidOperationException(
                $"The child agent run finished with unresolved failed tool outcomes: {childToolOutcomes.DescribeUnresolvedFailures()}.");
        }

        string outputText = !string.IsNullOrWhiteSpace(finalResult?.Response)
            ? finalResult.Response
            : output.Length > 0
                ? output.ToString()
                : finalResult?.ToolCalls?
                    .LastOrDefault(static call => !string.IsNullOrWhiteSpace(call.ResultJson))?
                    .ResultJson
                    ?? lastToolResult
                    ?? string.Empty;

        return new AaatChildContinuationResult(outputText, PendingApprovalEvent, PauseEvent);
    }
}
