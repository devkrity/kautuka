using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class NaradConsumerCutoverTests
{
    [Fact]
    public void BuildTerminalDoneReadsStampedResponseAndKeepsRequestAgentTarget()
    {
        // Simulate post-Narad run.finished body: stamped text only — not a GatewayChatStreamDone envelope.
        NaradPersistedEvent finished = NaradStreamObservation.CreateSynthetic(
            "evt-finished-response",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["response"] = "rolled a 4",
                ["result"] = new JsonObject { ["response"] = "rolled a 4" },
            }, KautukaJsonContext.Default.JsonObject));

        GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(
            finished,
            "agent",
            "agent-1786000128846",
            "session-1");

        Assert.Equal("agent", done.Result.TargetType);
        Assert.Equal("agent-1786000128846", done.Result.TargetId);
        Assert.Equal("rolled a 4", done.Result.Response);
        Assert.Equal(GatewayRunPhases.Completed, done.Result.Run?.Phase);
    }

    [Fact]
    public void BuildTerminalDoneReadsUsageFromNaradEnvelope()
    {
        NaradPersistedEvent finished = NaradStreamObservation.CreateSynthetic(
            "evt-finished",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["usage"] = new JsonObject
                {
                    ["promptTokens"] = 11,
                    ["completionTokens"] = 7,
                },
            }, KautukaJsonContext.Default.JsonObject));

        GatewayChatStreamDone done = NaradStreamObservation.BuildTerminalDone(finished, "agent", "agent-1", "session-1");

        Assert.Equal(GatewayRunPhases.Completed, done.Result.Run?.Phase);
        Assert.Equal(11, done.Result.Metrics?.TokenIn);
        Assert.Equal(7, done.Result.Metrics?.TokenOut);
    }

    [Fact]
    public void ChildRunToolOutcomeTrackerTreatsFailedThenSuccessfulSiblingAsResolved()
    {
        var tracker = new ChildRunToolOutcomeTracker();
        tracker.Observe(CreateToolEvent("call-1", NaradEventTypes.ToolCallStarted, new JsonObject { ["toolCallId"] = "call-1", ["toolName"] = "search" }));
        tracker.Observe(CreateToolEvent("call-2", NaradEventTypes.ToolCallStarted, new JsonObject { ["toolCallId"] = "call-2", ["toolName"] = "search" }));
        tracker.Observe(CreateToolEvent("call-2", NaradEventTypes.ToolCallFinished, new JsonObject { ["toolCallId"] = "call-2", ["outcome"] = "error" }));
        tracker.Observe(CreateToolEvent("call-3", NaradEventTypes.ToolCallStarted, new JsonObject { ["toolCallId"] = "call-3", ["toolName"] = "search" }));
        tracker.Observe(CreateToolEvent("call-3", NaradEventTypes.ToolCallFinished, new JsonObject { ["toolCallId"] = "call-3", ["outcome"] = "success" }));

        Assert.False(tracker.HasUnresolvedFailures);
    }

    [Fact]
    public void InterruptResolvedPayloadUsesStableInterruptAndWaitIds()
    {
        JsonElement body = NaradEventBodies.InterruptResolved(
            "request-1",
            "wait:request-1",
            "approve",
            NaradProtectedActorProvenance.FromSource("telegram"));

        Assert.Equal("request-1", body.GetProperty("interruptId").GetString());
        Assert.Equal("wait:request-1", body.GetProperty("waitId").GetString());
        Assert.Equal("approve", body.GetProperty("decision").GetString());
        Assert.Equal("telegram", body.GetProperty("actor").GetProperty("source").GetString());
        Assert.False(body.TryGetProperty("approved", out _));
    }

    [Fact]
    public async Task NestedChildCollectorTreatsInterruptAsAuthoritativeWithoutPauseForward()
    {
        var collector = new AaatChildContinuationCollector(new NoOpInvocationStore(), new NoOpApprovalStore());
        NaradPersistedEvent interrupt = CreateToolEvent(
            "evt-interrupt",
            NaradEventTypes.InterruptRequested,
            new JsonObject { ["interruptId"] = "nested-1", ["waitId"] = "wait:nested-1" });
        NaradPersistedEvent childPause = NaradStreamObservation.CreateSynthetic(
            "evt-pause",
            NaradEventTypes.RunPaused,
            "child-session",
            "child-run",
            "root-run",
            JsonSerializer.SerializeToElement(new JsonObject { ["waitId"] = "wait:nested-1" }, KautukaJsonContext.Default.JsonObject));

        async IAsyncEnumerable<NaradPersistedEvent> Stream()
        {
            yield return interrupt;
            yield return childPause;
        }

        AaatChildContinuationResult result = await collector.CollectAsync(
            Stream(),
            new AgentToolInvocationLink(
                "nested-1",
                "parent-session",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                "evt-interrupt",
                CallerToolCallId: "parent-tool",
                ChildSessionId: "child-session",
                ChildRunId: "child-run"),
            "child-session",
            CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(result.PendingApprovalEvent);
        Assert.Equal(NaradEventTypes.InterruptRequested, result.PendingApprovalEvent!.EventType);
    }

    private sealed class NoOpInvocationStore : IAgentToolInvocationStore
    {
#pragma warning disable CA1822 // Interface members cannot be static.
        public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request) => throw new NotSupportedException();
        public AgentToolInvocation? Get(string invocationId) => null;
        public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId) => null;
        public AgentToolInvocation? GetByChildRunId(string _) => null;
        public IReadOnlyList<AgentToolInvocation> ListRecoverable() => [];
        public void RecordApprovalLink(AgentToolInvocationLink link) { }
        public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome) { }
        public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration) => throw new NotSupportedException();
        public void MarkWaitingOnChild(string invocationId, string childRunId) { }
        public void MarkCompleted(string invocationId, string leaseOwner) { }
        public bool TrySealCompleted(string invocationId) => false;
        public void MarkFailed(string invocationId, string failureCode) { }
#pragma warning restore CA1822
    }

    private sealed class NoOpApprovalStore : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) { }
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    private static NaradPersistedEvent CreateToolEvent(string eventId, string eventType, JsonObject body)
        => NaradStreamObservation.CreateSynthetic(
            eventId,
            eventType,
            "session-1",
            "run-1",
            "run-1",
            JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject));
}
