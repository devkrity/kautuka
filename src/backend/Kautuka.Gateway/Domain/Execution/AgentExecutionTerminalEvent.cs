using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record AgentExecutionTerminalEvent(string EventType, JsonElement Payload, string ResumePhase)
{
    public static bool IsWaitingOnChildBoundary(GatewayChatStreamDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        return string.Equals(done.Result.Run?.Phase, GatewayRunPhases.Executing, StringComparison.Ordinal)
            && string.Equals(done.Result.Run?.WaitStateReason, RunWaitReasons.Child, StringComparison.Ordinal);
    }

    public static AgentExecutionTerminalEvent From(GatewayChatStreamDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (IsWaitingOnChildBoundary(done))
        {
            throw new InvalidOperationException(
                "AAAT waiting_on_child StreamDone is not a run terminal; parent tool call remains open.");
        }

        string? phase = done.Result.Run?.Phase;
        // Pause is authored by GatewayChatApprovalRequest (single HITL) or GatewayChatRunPaused
        // (packed HITL). AwaitingToolApproval StreamDone is silenced — never a second lifecycle.
        if (string.Equals(phase, GatewayRunPhases.AwaitingToolApproval, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "AwaitingToolApproval StreamDone is not a run terminal; pause already came from GatewayChatApprovalRequest.");
        }

        JsonElement? usage = BuildUsage(done.Result.Metrics);
        bool stopped = string.Equals(phase, GatewayRunPhases.Stopped, StringComparison.Ordinal);
        if (stopped)
        {
            return new AgentExecutionTerminalEvent(
                NaradEventTypes.RunCancelled,
                NaradEventBodies.RunCancelled(
                    "user_stopped",
                    "This run was stopped by the user.",
                    usage),
                GatewayRuntimeResumePhases.Completed);
        }

        if (phase is GatewayRunPhases.Failed or GatewayRunPhases.Aborted)
        {
            string code = done.Result.Run?.ErrorCode
                ?? (phase == GatewayRunPhases.Aborted ? "run_aborted" : "run_failed");
            string message = done.Result.Run?.ErrorMessage
                ?? (phase == GatewayRunPhases.Aborted ? "Run aborted." : "Run failed.");
            return new AgentExecutionTerminalEvent(
                NaradEventTypes.RunError,
                NaradEventBodies.RunError(code, message, usage),
                GatewayRuntimeResumePhases.Completed);
        }

        // Carry Result.Response on the terminal body the way pre-Narad StreamDone did, so channel
        // hitl can deliver the final reply without depending solely on in-process text buffers.
        string? finalResponse = string.IsNullOrWhiteSpace(done.Result.Response)
            ? null
            : done.Result.Response;
        return new AgentExecutionTerminalEvent(
            NaradEventTypes.RunFinished,
            NaradEventBodies.RunFinished(usage, finalResponse),
            GatewayRuntimeResumePhases.Completed);
    }

    private static JsonElement? BuildUsage(GatewayMetrics metrics)
    {
        if (metrics.TokenIn <= 0 && metrics.TokenOut <= 0 && metrics.ElapsedMs <= 0)
        {
            return null;
        }

        return NaradEventBodies.Usage(
            Math.Max(0, metrics.TokenIn),
            Math.Max(0, metrics.TokenOut),
            metrics.ElapsedMs > 0 ? metrics.ElapsedMs : null);
    }
}
