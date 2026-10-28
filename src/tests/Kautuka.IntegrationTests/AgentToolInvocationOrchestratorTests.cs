using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Agents.AgentTools;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AgentToolInvocationOrchestratorTests
{
    [Fact]
    public async Task InvokeAsyncRejectsAgentToolNestingBeyondLeaf()
    {
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultEvents("should not run")),
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();
        AgentToolCallerContextState callerContext = AgentToolCallerContext.Current!;
        using IDisposable childInvocationScope = AgentToolInvocationContext.Push(
            "session-parent-child",
            "agent-parent-child",
            "user-1",
            "run-parent-child",
            "call-parent-child",
            "run-root");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InvokeAsync(
            "agent-a",
            "agent-t",
            "hello",
            "user-1",
            "session-caller-1",
            callerContext,
            CancellationToken.None).AsTask());

        Assert.Contains("leaf-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsyncAwaitsInnerApprovalAndReturnsChildResult()
    {
        var agentToolApproval = new GatewayChatApprovalRequest(
            RequestId: "apr-agent-tool-1",
            ToolCallId: "call-agent-tool-1",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            RunId: "run-agent-tool-1",
            CheckpointId: "checkpoint-agent-tool-1");

        var chatGateway = new FakeChatGatewayService(
            AgentToolEvents(agentToolApproval),
            approvalEvents: AgentToolResultEvents("rolled 7"));
        var invocationLinks = new RecordingAgentToolInvocationLinkStore();
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeAgentToolApprovalWaiter(),
            invocationLinkStore: invocationLinks);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("rolled 7", payload.GetProperty("result").GetString());
        Assert.NotNull(chatGateway.LastApprovalResponse);
        Assert.Equal(agentToolApproval.RequestId, chatGateway.LastApprovalResponse.RequestId);
        Assert.Contains(invocationLinks.Links, link =>
            string.Equals(link.ApprovalRequestId, agentToolApproval.RequestId, StringComparison.Ordinal)
            && string.Equals(link.RootRunId, "run-parent-1", StringComparison.Ordinal));
        Assert.False(AgentToolCallerContext.TryTakePendingApproval(
            "run-parent-1",
            "call-parent-1",
            out _));
    }

    [Fact]
    public async Task InvokeAsyncAwaitsNestedApprovalWithoutParentPendingApprovalResult()
    {
        var nestedApproval = new GatewayChatApprovalRequest(
            RequestId: "apr-nested-active-1",
            ToolCallId: "call-nested-active-1",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            RunId: "run-child-active-1",
            CheckpointId: "checkpoint-child-active-1");
        var invocationLinks = new RecordingAgentToolInvocationLinkStore();
        var chatGateway = new FakeChatGatewayService(
            AgentToolEvents(nestedApproval),
            approvalEvents: AgentToolResultEvents("nested done"));
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeAgentToolApprovalWaiter(),
            invocationLinkStore: invocationLinks);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("nested done", payload.GetProperty("result").GetString());
        Assert.False(AgentToolCallerContext.TryTakePendingApproval(
            "run-parent-1",
            "call-parent-1",
            out _));
        Assert.Contains(invocationLinks.Links, link =>
            string.Equals(link.ApprovalRequestId, nestedApproval.RequestId, StringComparison.Ordinal)
            && string.Equals(link.CallerToolCallId, "call-parent-1", StringComparison.Ordinal)
            && string.Equals(link.ChildRunId, nestedApproval.RunId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsyncStartsInnerAgentRunWithoutApprovalReplayLookup()
    {
        var lifecycle = new FakeSessionLifecycleService();
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultEvents("agent tool final result")),
            new FakeAgentToolApprovalWaiter(),
            lifecycle);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("agent tool final result", payload.GetProperty("result").GetString());
        Assert.Equal("agent_tool", lifecycle.LastCreateOriginator);
    }

    [Fact]
    public async Task InvokeAsyncRecordsChildCompletionBeforeMarkingCompleted()
    {
        var invocationStore = new TrackingAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var publisher = new FakeRuntimeEventPublisher();
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultEvents("agent tool final result")),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore,
            runtimeEventPublisher: publisher);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            "agent-a",
            "agent-t",
            "hello",
            "user-1",
            "session-caller-1",
            AgentToolCallerContext.Current!,
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("agent tool final result", Assert.IsType<JsonElement>(result).GetProperty("result").GetString());
        Assert.Equal(["record_child_completion", "claim_parent_completion", "mark_completed"], invocationStore.OperationOrder);
        Assert.NotNull(invocationStore.Get("invocation-test-1")?.ChildResultJson);
        Assert.Contains("agent tool final result", invocationStore.Get("invocation-test-1")!.ChildResultJson!, StringComparison.Ordinal);
        Assert.Equal(AgentToolInvocationStates.Completed, invocationStore.Get("invocation-test-1")?.State);
        Assert.Null(publisher.LastEventId);
    }

    [Fact]
    public async Task InvokeAsyncPassesOriginalToolRequestMessageWithoutCallerContextWrapping()
    {
        var chatGateway = new FakeChatGatewayService(AgentToolResultEvents("agent tool final result"));
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        _ = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "d787",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("d787", chatGateway.LastMessage);
    }

    [Fact]
    public async Task InvokeAsyncPassesCurrentTraceContextToInnerAgentRun()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? parentActivity = source.StartActivity("agent (caller) run [chat]", ActivityKind.Internal);
        Assert.NotNull(parentActivity);
        AgentRunHostCallTracer.MarkAgentRun(parentActivity, "run-parent-1");

        var chatGateway = new FakeChatGatewayService(AgentToolResultEvents("agent tool final result"));
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        _ = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        AgentRequestTraceContext traceContext = Assert.IsType<AgentRequestTraceContext>(chatGateway.LastRequestContext?.Trace);
        Assert.Equal(parentActivity.Id, traceContext.TraceParent);
        Assert.Equal(parentActivity.TraceId.ToString(), traceContext.TraceId);
        Assert.Equal(parentActivity.SpanId.ToString(), traceContext.ParentSpanId);
    }

    [Fact]
    public async Task InvokeAsyncUsesStreamedAgentToolOutputWhenFinalResultIsEmpty()
    {
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolTokenOnlyResultEvents("agent tool streamed result")),
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("agent tool streamed result", payload.GetProperty("result").GetString());
    }

    [Fact]
    public async Task InvokeAsyncReturnsStructuredFinalResponseWhenAgentFinishesWithoutAssistantText()
    {
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultOnlyEvents("agent tool inner tool result")),
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("agent tool inner tool result", payload.GetProperty("result").GetString());
    }

    [Fact]
    public async Task InvokeAsyncPropagatesChildRunFailureInsteadOfReturningSuccessfulToolResult()
    {
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolFailedEvents()),
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).AsTask()).ConfigureAwait(true);

        Assert.Contains("failed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsyncRejectsFinishedChildRunWithUnresolvedToolFailure()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(
                AgentToolResultEvents("unused"),
                runtimeEvents: AsNaradEvents(ChildToolFailureThenFinishedEvents())),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).AsTask()).ConfigureAwait(true);

        Assert.Contains("run_skill_script", error.Message, StringComparison.Ordinal);
        Assert.Equal("child_tool_failed", invocationStore.Get("invocation-test-1")?.FailureCode);
    }

    [Fact]
    public async Task InvokeAsyncAllowsExplicitSameToolRecoveryBeforeChildFinishes()
    {
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(
                AgentToolResultEvents("unused"),
                runtimeEvents: AsNaradEvents(ChildToolFailureThenRecoveryEvents())),
            new FakeAgentToolApprovalWaiter());
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("recovered result", payload.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ContinuationCollectorRejectsFinishedChildRunWithUnresolvedToolFailure()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var collector = new AaatChildContinuationCollector(invocationStore, new NoOpGatewayApprovalStore());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(
            AsNaradEvents(ChildToolFailureThenFinishedEvents()),
            CreateInvocationLink(),
            "session-agent-tool-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("run_skill_script", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationCollectorAllowsExplicitSameToolRecoveryBeforeChildFinishes()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var collector = new AaatChildContinuationCollector(invocationStore, new NoOpGatewayApprovalStore());

        AaatChildContinuationResult result = await collector.CollectAsync(
            AsNaradEvents(ChildToolFailureThenRecoveryEvents()),
            CreateInvocationLink(),
            "session-agent-tool-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("recovered result", result.OutputText);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("cancelled")]
    public async Task ContinuationCollectorPreservesExpectedNegativeApprovalForExactToolCall(
        string terminalOutcome)
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var collector = new AaatChildContinuationCollector(
            invocationStore,
            new NoOpGatewayApprovalStore(),
            expectedNegativeApprovalToolCallId: "tool-call-failed");

        AaatChildContinuationResult result = await collector.CollectAsync(
            AsNaradEvents(ChildToolFailureThenFinishedEvents(
                "The child agent approval was denied.",
                terminalOutcome)),
            CreateInvocationLink(),
            "session-agent-tool-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("The child agent approval was denied.", result.OutputText);
    }

    [Fact]
    public async Task ContinuationCollectorRejectsUnrelatedFailureAfterNegativeApproval()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var collector = new AaatChildContinuationCollector(
            invocationStore,
            new NoOpGatewayApprovalStore(),
            expectedNegativeApprovalToolCallId: "tool-call-denied");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(
            AsNaradEvents(ChildToolFailureThenFinishedEvents("The child agent approval was denied.")),
            CreateInvocationLink(),
            "session-agent-tool-1",
            CancellationToken.None)).ConfigureAwait(true);

        Assert.Contains("run_skill_script", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuationCollectorWaitsForRunPausedAfterNestedApprovalRequest()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var collector = new AaatChildContinuationCollector(invocationStore, new NoOpGatewayApprovalStore());

        AaatChildContinuationResult result = await collector.CollectAsync(
            AsNaradEvents(NestedApprovalThenPausedEvents()),
            CreateInvocationLink(),
            "session-agent-tool-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(result.PendingApprovalEvent);
        Assert.Equal(NaradEventTypes.InterruptRequested, result.PendingApprovalEvent.EventType);
    }

    [Fact]
    public async Task InvokeAsyncPersistsCancellationAsDistinctTerminalOutcome()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultEvents("unused")),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: cancellation.Token).AsTask()).ConfigureAwait(true);

        Assert.Equal("cancelled", invocationStore.Get("invocation-test-1")?.FailureCode);
    }

    [Fact]
    public async Task InvokeAsyncPersistsTimeoutAsDistinctTerminalOutcome()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolTimeoutEvents()),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();

        await Assert.ThrowsAsync<TimeoutException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).AsTask()).ConfigureAwait(true);

        Assert.Equal("timed_out", invocationStore.Get("invocation-test-1")?.FailureCode);
    }

    [Fact]
    public async Task InvokeAsyncDoesNotApplyMaxRunDurationToNestedHitlWait()
    {
        var agentToolApproval = new GatewayChatApprovalRequest(
            RequestId: "apr-agent-tool-timeout-hitl",
            ToolCallId: "call-agent-tool-timeout-hitl",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            RunId: "run-agent-tool-timeout-hitl",
            CheckpointId: "checkpoint-agent-tool-timeout-hitl");
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var waiter = new FakeAgentToolApprovalWaiter(delay: TimeSpan.FromMilliseconds(250));
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(
                AgentToolEvents(agentToolApproval),
                approvalEvents: AgentToolResultEvents("rolled 7")),
            waiter,
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None,
            maxRunDuration: TimeSpan.FromMilliseconds(50)).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("rolled 7", payload.GetProperty("result").GetString());
        Assert.Equal(AgentToolInvocationStates.Completed, invocationStore.Get("invocation-test-1")?.State);
        Assert.Equal("completed", invocationStore.Get("invocation-test-1")?.ChildOutcome);
        Assert.Null(invocationStore.Get("invocation-test-1")?.FailureCode);
        Assert.False(waiter.LastWaitToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InvokeAsyncTimesOutActiveChildWorkUsingMaxRunDuration()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(HangUntilCancelled(TestContext.Current.CancellationToken)),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None,
            maxRunDuration: TimeSpan.FromMilliseconds(50)).AsTask()).ConfigureAwait(true);

        Assert.Equal("timed_out", invocationStore.Get("invocation-test-1")?.FailureCode);
        Assert.Equal(AgentToolInvocationStates.Failed, invocationStore.Get("invocation-test-1")?.State);
    }

    [Fact]
    public async Task InvokeAsyncRestoresRemainingMaxRunFromWaitStateCheckpointAfterNestedHitl()
    {
        var nestedApproval = new GatewayChatApprovalRequest(
            RequestId: "apr-child-clock-restore",
            ToolCallId: "call-child-clock-restore",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            RunId: "run-child-clock-restore",
            CheckpointId: "checkpoint-child-clock-restore",
            ContinuationRunId: "continuation-child-clock-restore");
        TimeSpan maxRun = TimeSpan.FromMilliseconds(800);
        var waitStateStore = new CheckpointRunWaitStateStore(new RunWaitState(
            "wait-child-clock-1",
            nestedApproval.ContinuationRunId!,
            nestedApproval.RunId,
            "agent",
            "agent-t",
            "session-agent-tool-1",
            "agent-t",
            "user-1",
            RunWaitReasons.Approval,
            nestedApproval.RequestId,
            "{}",
            RunClockEnvelope.Wrap("{}", maxRun),
            "[]",
            nestedApproval.ToolCallId,
            nestedApproval.ToolName,
            nestedApproval.RequestId,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-child-clock-1"));
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(
                AgentToolEvents(nestedApproval),
                approvalEvents: HangUntilCancelled(TestContext.Current.CancellationToken)),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore,
            waitStateStore: waitStateStore);
        using IDisposable callerScope = PushTestCallerContext();
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None,
            maxRunDuration: maxRun).AsTask()).ConfigureAwait(true);

        elapsed.Stop();
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromMilliseconds(400),
            $"Child max-run remaining should restore from wait-state, not restart. Took {elapsed.Elapsed.TotalMilliseconds}ms.");
        Assert.Equal("timed_out", invocationStore.Get("invocation-test-1")?.FailureCode);
        Assert.Equal(AgentToolInvocationStates.Failed, invocationStore.Get("invocation-test-1")?.State);
    }

    [Fact]
    public async Task InvokeAsyncWithZeroMaxRunDurationDoesNotKillActiveChildWork()
    {
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(DelayedResultEvents("still running", TimeSpan.FromMilliseconds(150))),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None,
            maxRunDuration: TimeSpan.Zero).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("still running", payload.GetProperty("result").GetString());
        Assert.Equal(AgentToolInvocationStates.Completed, invocationStore.Get("invocation-test-1")?.State);
        Assert.Null(invocationStore.Get("invocation-test-1")?.FailureCode);
    }

    [Fact]
    public async Task InvokeAsyncLeavesWaitingOnChildWhenCallerCancelsDuringNestedHitlWait()
    {
        var agentToolApproval = new GatewayChatApprovalRequest(
            RequestId: "apr-agent-tool-cancel-hitl",
            ToolCallId: "call-agent-tool-cancel-hitl",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            RunId: "run-agent-tool-cancel-hitl",
            CheckpointId: "checkpoint-agent-tool-cancel-hitl");
        var invocationStore = new InMemoryAgentToolInvocationStore(new NoOpAgentToolInvocationLinkStore());
        var waiter = new FakeAgentToolApprovalWaiter(delay: Timeout.InfiniteTimeSpan);
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolEvents(agentToolApproval)),
            waiter,
            invocationStore: invocationStore);
        using IDisposable callerScope = PushTestCallerContext();
        using var cancellation = new CancellationTokenSource();

        Task<object?> invoke = orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: cancellation.Token).AsTask();
        await waiter.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoke).ConfigureAwait(true);
        Assert.Equal(AgentToolInvocationStates.WaitingOnChild, invocationStore.Get("invocation-test-1")?.State);
        Assert.Null(invocationStore.Get("invocation-test-1")?.FailureCode);
    }

    [Fact]
    public async Task InvokeAsyncSurfacesInnerApprovalFromCanonicalInterruptBody()
    {
        const string approvalId = "apr-canonical-1";
        var approvalStore = new InterruptObservationApprovalStore(new GatewayToolApprovalRequest(
            approvalId,
            "session-agent-tool-1",
            "run-child-1",
            null,
            "agent-t",
            "user-1",
            "call-skill",
            "run_skill_script",
            "skill",
            null,
            "{}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            null));
        NaradPersistedEvent interrupt = NaradStreamObservation.CreateSynthetic(
            "evt-canonical-interrupt",
            NaradEventTypes.InterruptRequested,
            "session-agent-tool-1",
            "run-child-1",
            "run-child-1",
            NaradEventBodies.InterruptRequested(
                approvalId,
                "wait:apr-canonical-1",
                "approval",
                ["approve", "deny"],
                toolCallId: "call-skill",
                input: JsonDocument.Parse("""{"toolCallId":"call-skill","toolName":"run_skill_script","args":{}}""").RootElement));
        NaradPersistedEvent paused = NaradStreamObservation.CreateSynthetic(
            "evt-canonical-pause",
            NaradEventTypes.RunPaused,
            "session-agent-tool-1",
            "run-child-1",
            "run-child-1",
            NaradEventBodies.RunPaused("wait:apr-canonical-1", "interrupt", approvalId));

        var invocationLinks = new RecordingAgentToolInvocationLinkStore();
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(
                AgentToolResultEvents("unused"),
                approvalEvents: AgentToolResultEvents("canonical resumed"),
                runtimeEvents: SingleEventStream(interrupt, paused)),
            new FakeAgentToolApprovalWaiter(),
            invocationLinkStore: invocationLinks,
            approvalStore: approvalStore);
        using IDisposable callerScope = PushTestCallerContext();

        object? result = await orchestrator.InvokeAsync(
            callerAgentId: "agent-a",
            targetAgentId: "agent-t",
            message: "hello",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.Current!,
            ct: CancellationToken.None).ConfigureAwait(true);

        JsonElement payload = Assert.IsType<JsonElement>(result);
        Assert.Equal("canonical resumed", payload.GetProperty("result").GetString());
        Assert.Contains(invocationLinks.Links, link =>
            string.Equals(link.ApprovalRequestId, approvalId, StringComparison.Ordinal)
            && string.Equals(link.ChildAgentId, "agent-t", StringComparison.Ordinal));
        Assert.False(AgentToolCallerContext.TryTakePendingApproval(
            "run-parent-1",
            "call-parent-1",
            out _));
    }

    private static async IAsyncEnumerable<NaradPersistedEvent> SingleEventStream(params NaradPersistedEvent[] events)
    {
        foreach (NaradPersistedEvent evt in events)
        {
            yield return evt;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class InterruptObservationApprovalStore(GatewayToolApprovalRequest request) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) { }
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId)
            => string.Equals(requestId, request.Id, StringComparison.Ordinal) ? request : null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    [Fact]
    public async Task InvokeAsyncUsesExplicitCallerContextWhenAgentToolsOverlap()
    {
        var invocationLinks = new RecordingAgentToolInvocationLinkStore();
        var invocationStore = new InMemoryAgentToolInvocationStore(invocationLinks);
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(AgentToolResultEvents("rolled 24")),
            new FakeAgentToolApprovalWaiter(),
            invocationStore: invocationStore);

        using IDisposable rollerScope = AgentToolCallerContext.Push(
            "run-parent-roller",
            "continuation-roller",
            "call-roller",
            "invoke_agent_roller",
            "session-caller-1",
            "agent-parent",
            "user-1",
            rootRunId: "run-parent-roller");
        using IDisposable homeScope = AgentToolCallerContext.Push(
            "run-parent-home",
            "continuation-home",
            "call-home",
            "invoke_agent_home_assistant",
            "session-caller-1",
            "agent-parent",
            "user-1",
            rootRunId: "run-parent-home");

        _ = await orchestrator.InvokeAsync(
            callerAgentId: "agent-parent",
            targetAgentId: "agent-roller",
            message: "roll a d100",
            userId: "user-1",
            callerSessionId: "session-caller-1",
            callerContext: AgentToolCallerContext.TryGetRegistered("session-caller-1", "call-roller")!,
            ct: CancellationToken.None).ConfigureAwait(true);

        AgentToolInvocation invocation = Assert.IsType<AgentToolInvocation>(
            invocationStore.GetByParentToolCall("run-parent-roller", "call-roller"));
        Assert.Equal("call-roller", invocation.ParentToolCallId);
        Assert.Equal("invoke_agent_roller", invocation.ParentToolName);
        Assert.Equal("continuation-roller", invocation.ParentContinuationRunId);
        Assert.Equal("run-parent-roller", invocation.ParentRunId);
        Assert.Equal("agent-roller", invocation.ChildAgentId);
        Assert.Equal("run-parent-roller", invocation.RootRunId);
        Assert.False(string.IsNullOrWhiteSpace(invocation.ChildRunId));
        Assert.Equal("session-agent-tool-1", invocation.ChildSessionId);
        Assert.Equal(AgentToolInvocationStates.Completed, invocation.State);
    }

    private static AgentToolInvocationOrchestrator CreateOrchestrator(
        IChatGatewayService chatGateway,
        FakeAgentToolApprovalWaiter approvalWaiter,
        FakeSessionLifecycleService? lifecycle = null,
        IAgentToolInvocationLinkStore? invocationLinkStore = null,
        IAgentToolInvocationStore? invocationStore = null,
        IRuntimeEventPublisher? runtimeEventPublisher = null,
        IGatewayApprovalStore? approvalStore = null,
        IRunWaitStateStore? waitStateStore = null)
    {
        IAgentToolInvocationLinkStore links = invocationLinkStore ?? new NoOpAgentToolInvocationLinkStore();
        return new AgentToolInvocationOrchestrator(
            new Lazy<IChatGatewayService>(() => chatGateway),
            lifecycle ?? new FakeSessionLifecycleService(),
            invocationStore ?? new InMemoryAgentToolInvocationStore(links),
            approvalStore ?? new NoOpGatewayApprovalStore(),
            approvalWaiter,
            NullLogger<AgentToolInvocationOrchestrator>.Instance,
            runtimeEventPublisher ?? new FakeRuntimeEventPublisher(),
            waitStateStore ?? new NoOpRunWaitStateStore());
    }

    private sealed class FakeRuntimeEventPublisher(Action<string>? onPublish = null) : IRuntimeEventPublisher
    {
        private long sequence;

        public string? LastEventId { get; private set; }

        public bool LastCancellationCanBeCanceled { get; private set; }

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            onPublish?.Invoke(draft.EventType);
            LastEventId = draft.EventId;
            LastCancellationCanBeCanceled = cancellationToken.CanBeCanceled;
            long current = Interlocked.Increment(ref sequence);
            return ValueTask.FromResult(NaradTestEventFactory.FromDraft(draft, context, current, current));
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    private static IDisposable PushTestCallerContext()
        => AgentToolCallerContext.Push(
            "run-parent-1",
            "continuation-parent-1",
            "call-parent-1",
            "invoke_agent_target",
            "session-caller-1",
            "agent-a",
            "user-1",
            "run-parent-1");

    private static AgentToolInvocationLink CreateInvocationLink()
        => new(
            "approval-test-1",
            "session-caller-1",
            "2026-07-10T00:00:00Z",
            "2026-07-10T00:00:00Z",
            "event-test-1",
            CallerToolCallId: "call-parent-1",
            CallerToolName: "invoke_agent_target",
            CallerRunId: "run-parent-1",
            ChildRunId: "run-child-1",
            ChildAgentId: "agent-t",
            RootRunId: "run-parent-1",
            InvocationId: "invocation-test-1");

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolEvents(GatewayChatApprovalRequest approval)
    {
        // Production emits interrupt then run.paused. Tests may stop after interrupt; the
        // orchestrator must drain the stream (not cancel on interrupt) before WaitAsync.
        yield return approval;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolResultEvents(string response)
    {
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: response,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: []));

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> DelayedResultEvents(string response, TimeSpan delay)
    {
        await Task.Delay(delay, TestContext.Current.CancellationToken).ConfigureAwait(false);
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: response,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: []));
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolTokenOnlyResultEvents(string response)
    {
        yield return new GatewayChatTokenDelta(response, false);
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: string.Empty,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: []));

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolResultOnlyEvents(string response)
    {
        yield return new GatewayChatToolResult("tool-call-1", response);
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: string.Empty,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: [new GatewayToolCall("tool-call-1", "run_shell", "agent-t", "{}", response, "done", "superexec")]));

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolFailedEvents()
    {
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: "child failure detail",
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: [],
            Run: new GatewayRunState(GatewayRunPhases.Failed)));

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<NaradPersistedEvent> AsNaradEvents(
        IAsyncEnumerable<NaradPersistedEvent> events)
    {
        await foreach (NaradPersistedEvent evt in events.WithCancellation(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    private static async IAsyncEnumerable<NaradPersistedEvent> ChildToolFailureThenFinishedEvents(
        string finalResponse = "hallucinated result",
        string terminalOutcome = "error")
    {
        yield return NaradTestEventFactory.FromChatEvent(
            new GatewayChatToolCall("tool-call-failed", "run_skill_script", "{}", "tool"));
        yield return CreateToolTerminalEvent(terminalOutcome, "tool-call-failed", "run_skill_script");
        yield return NaradTestEventFactory.FromChatEvent(new GatewayChatTokenDelta(
            "I could not run the script, but here is a plausible answer.",
            IsThinking: false));
        yield return FinishedNaradEvent(finalResponse);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<NaradPersistedEvent> ChildToolFailureThenRecoveryEvents()
    {
        yield return NaradTestEventFactory.FromChatEvent(
            new GatewayChatToolCall("tool-call-failed", "run_skill_script", "{}", "tool"));
        yield return CreateToolTerminalEvent("error", "tool-call-failed", "run_skill_script");
        yield return NaradTestEventFactory.FromChatEvent(
            new GatewayChatToolCall("tool-call-retry", "run_skill_script", "{}", "tool"));
        yield return NaradTestEventFactory.FromChatEvent(
            new GatewayChatToolResult("tool-call-retry", "recovered result"));
        yield return FinishedNaradEvent("recovered result");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<NaradPersistedEvent> NestedApprovalThenPausedEvents()
    {
        yield return NaradTestEventFactory.FromChatEvent(new GatewayChatApprovalRequest(
            "approval-nested",
            "call-skill",
            "run_skill_script",
            "{}",
            "run-child-1",
            ToolType: "skill",
            SessionId: "session-agent-tool-1"));
        // run.paused is paired with interrupt from ApprovalRequest (Writer map); never StreamDone.
        string waitId = NaradEventBodies.ApprovalWaitId("approval-nested");
        yield return NaradTestEventFactory.FromBody(
            NaradEventTypes.RunPaused,
            NaradEventBodies.RunPaused(waitId, "interrupt", "approval-nested"),
            sessionId: "session-agent-tool-1",
            runId: "run-child-1",
            rootRunId: "run-child-1",
            sessionSequence: 2,
            rootSequence: 2);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static NaradPersistedEvent FinishedNaradEvent(string response)
        => NaradTestEventFactory.FromChatEvent(new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-t",
            SessionId: "session-agent-tool-1",
            Response: response,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: [])));

    private static NaradPersistedEvent CreateToolTerminalEvent(string outcome, string toolCallId, string toolName)
        => NaradTestEventFactory.FromBody(
            NaradEventTypes.ToolCallFinished,
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName,
                ["outcome"] = outcome,
                ["message"] = "simulated failure",
            }),
            "session-agent-tool-1",
            "run-child-1",
            "run-child-1",
            2,
            2);

    private static async IAsyncEnumerable<GatewayChatStreamEvent> HangUntilCancelled(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> AgentToolTimeoutEvents(bool throwTimeout = true)
    {
        await Task.Yield();
        if (throwTimeout)
        {
            throw new TimeoutException("simulated child timeout");
        }

        yield return new GatewayChatTokenDelta("unused", IsThinking: false);
    }

    private static ActivityListener CreateGatewayAgentRunListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, KautukaActivitySources.GatewayAgentRunSource, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FakeChatGatewayService(
        IAsyncEnumerable<GatewayChatStreamEvent> events,
        IAsyncEnumerable<GatewayChatStreamEvent>? approvalEvents = null,
        IAsyncEnumerable<NaradPersistedEvent>? runtimeEvents = null) : IChatGatewayService
    {
        public string? LastMessage { get; private set; }
        public AgentRequestContext? LastRequestContext { get; private set; }
        public ToolApprovalResponseInfo? LastApprovalResponse { get; private set; }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            System.Text.Json.JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessage = body.GetProperty("message").GetString();
            LastRequestContext = requestContext;

            if (runtimeEvents is not null)
            {
                await foreach (NaradPersistedEvent evt in runtimeEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    yield return evt;
                }

                yield break;
            }

            await foreach (NaradPersistedEvent evt in NaradTestEventFactory.ToNaradEvents(events, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastApprovalResponse = approvalResponse;
            await foreach (NaradPersistedEvent evt in NaradTestEventFactory.ToNaradEvents(approvalEvents ?? events, cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
        {
        }

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
        {
        }

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
        {
        }
    }

    private sealed class FakeAgentToolApprovalWaiter(
        HitlApprovalResolved? resolved = null,
        TimeSpan? delay = null) : IAgentToolApprovalWaiter
    {
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken LastWaitToken { get; private set; }

        public bool IsWaiting(string requestId) => delay is not null;

        public async ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken)
        {
            LastWaitToken = cancellationToken;
            WaitEntered.TrySetResult();
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            return resolved ?? new HitlApprovalResolved(
                requestId,
                Approved: true,
                Reason: null,
                DecidedByUserId: null,
                HitlResolutionSource.ApprovalApi);
        }
    }

    private sealed class FakeSessionLifecycleService : ISessionLifecycleService
    {
        public string? LastCreateOriginator { get; private set; }

        public ValueTask<GatewaySession> CreateSessionAsync(string targetType, string targetId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
        {
            LastCreateOriginator = originator;
            return ValueTask.FromResult(new GatewaySession(
                Id: "session-agent-tool-1",
                TargetType: targetType,
                TargetId: targetId,
                OwnerUserId: ownerUserId,
                Title: "Agent Tool",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Messages: [],
                Compacted: false,
                CompactSummary: null,
                Originator: originator));
        }

        public ValueTask<GatewaySession> EnsureSessionAsync(string targetType, string targetId, string sessionId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
            => ValueTask.FromResult(new GatewaySession(
                Id: sessionId,
                TargetType: targetType,
                TargetId: targetId,
                OwnerUserId: ownerUserId,
                Title: "Agent Tool",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Messages: [],
                Compacted: false,
                CompactSummary: null,
                Originator: originator));
    }

    private sealed class StaticPlatformConfigurationProvider(Kautuka.Gateway.Infrastructure.Configuration.PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public Kautuka.Gateway.Infrastructure.Configuration.PlatformConfiguration GetCurrent() => config;
    }

    private sealed class TrackingAgentToolInvocationStore(IAgentToolInvocationLinkStore links) : IAgentToolInvocationStore
    {
        private AgentToolInvocation? invocation;

        public List<string> OperationOrder { get; } = [];

        public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            invocation ??= new AgentToolInvocation(
                "invocation-test-1",
                request.ParentRunId,
                request.ParentSessionId,
                request.ParentToolCallId,
                request.ParentToolName,
                request.ChildRunId,
                request.ChildSessionId,
                request.ChildAgentId,
                request.RootRunId,
                AgentToolInvocationStates.WaitingOnChild,
                now,
                now,
                request.ParentContinuationRunId);
            return invocation;
        }

        public AgentToolInvocation? Get(string invocationId) => invocation;
        public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId) => invocation;
        public AgentToolInvocation? GetByChildRunId(string childRunId)
            => invocation is not null && string.Equals(invocation.ChildRunId, childRunId, StringComparison.Ordinal)
                ? invocation
                : null;
        public IReadOnlyList<AgentToolInvocation> ListRecoverable() => invocation is null ? [] : [invocation];
        public void RecordApprovalLink(AgentToolInvocationLink link) => links.Upsert(link);

        public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome)
        {
            OperationOrder.Add("record_child_completion");
            invocation = invocation! with
            {
                State = AgentToolInvocationStates.ParentCompletionPending,
                ChildResultJson = childResultJson,
                ChildOutcome = childOutcome,
                ChildCompletedAt = DateTimeOffset.UtcNow.ToString("O"),
            };
        }

        public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration)
        {
            if (invocation is null || invocation.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.CompletingParent)
            {
                return false;
            }

            OperationOrder.Add("claim_parent_completion");
            invocation = invocation with { State = AgentToolInvocationStates.CompletingParent, CompletionLeaseOwner = leaseOwner };
            return true;
        }

        public void MarkWaitingOnChild(string invocationId, string childRunId)
            => invocation = invocation! with { State = AgentToolInvocationStates.WaitingOnChild, ChildRunId = childRunId };

        public void MarkCompleted(string invocationId, string leaseOwner)
        {
            OperationOrder.Add("mark_completed");
            invocation = invocation! with { State = AgentToolInvocationStates.Completed };
        }

        public bool TrySealCompleted(string invocationId)
        {
            OperationOrder.Add("seal_completed");
            if (invocation is null
                || invocation.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.Failed
                || string.IsNullOrWhiteSpace(invocation.ChildOutcome))
            {
                return false;
            }

            invocation = invocation with { State = AgentToolInvocationStates.Completed };
            return true;
        }

        public void MarkFailed(string invocationId, string failureCode)
            => invocation = invocation! with { State = AgentToolInvocationStates.Failed, FailureCode = failureCode };
    }

    private sealed class InMemoryAgentToolInvocationStore(IAgentToolInvocationLinkStore links) : IAgentToolInvocationStore
    {
        private AgentToolInvocation? invocation;

        public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            invocation ??= new AgentToolInvocation(
                "invocation-test-1",
                request.ParentRunId,
                request.ParentSessionId,
                request.ParentToolCallId,
                request.ParentToolName,
                request.ChildRunId,
                request.ChildSessionId,
                request.ChildAgentId,
                request.RootRunId,
                AgentToolInvocationStates.WaitingOnChild,
                now,
                now,
                request.ParentContinuationRunId);
            return invocation;
        }

        public AgentToolInvocation? Get(string invocationId) => invocation;
        public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId) => invocation;
        public AgentToolInvocation? GetByChildRunId(string childRunId)
            => invocation is not null && string.Equals(invocation.ChildRunId, childRunId, StringComparison.Ordinal)
                ? invocation
                : null;
        public IReadOnlyList<AgentToolInvocation> ListRecoverable() => invocation is null ? [] : [invocation];
        public void RecordApprovalLink(AgentToolInvocationLink link) => links.Upsert(link);

        public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome)
            => invocation = invocation! with
            {
                State = AgentToolInvocationStates.ParentCompletionPending,
                ChildResultJson = childResultJson,
                ChildOutcome = childOutcome,
                ChildCompletedAt = DateTimeOffset.UtcNow.ToString("O"),
            };

        public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration)
        {
            if (invocation is null || invocation.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.CompletingParent)
            {
                return false;
            }

            invocation = invocation with { State = AgentToolInvocationStates.CompletingParent, CompletionLeaseOwner = leaseOwner };
            return true;
        }

        public void MarkWaitingOnChild(string invocationId, string childRunId)
            => invocation = invocation! with { State = AgentToolInvocationStates.WaitingOnChild, ChildRunId = childRunId };

        public void MarkCompleted(string invocationId, string leaseOwner)
            => invocation = invocation! with { State = AgentToolInvocationStates.Completed };

        public bool TrySealCompleted(string invocationId)
        {
            if (invocation is null
                || invocation.State is AgentToolInvocationStates.Completed or AgentToolInvocationStates.Failed
                || string.IsNullOrWhiteSpace(invocation.ChildOutcome))
            {
                return false;
            }

            invocation = invocation with { State = AgentToolInvocationStates.Completed };
            return true;
        }

        public void MarkFailed(string invocationId, string failureCode)
            => invocation = invocation! with { State = AgentToolInvocationStates.Failed, FailureCode = failureCode };
    }

    private sealed class NoOpRunWaitStateStore : IRunWaitStateStore
    {
        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId) => null;
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId) => null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId) => null;
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId) => null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => false;
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }
        public void ReleaseForRetry(string waitId, string leaseOwner) { }
        public void MarkPausedAgain(string waitId) { }
        public void ReleaseExecutingWait(string waitId) { }
        public void MarkCompleted(string waitId) { }
        public void MarkFailed(string waitId, string failureCode) { }
        public IReadOnlyList<RunWaitState> ListRecoverable() => [];
        public void DeleteBySession(string sessionId) { }
    }

    private sealed class CheckpointRunWaitStateStore(RunWaitState state) : IRunWaitStateStore
    {
        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId)
            => string.Equals(state.WaitId, waitId, StringComparison.Ordinal) ? state : null;
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId)
            => string.Equals(state.ContinuationRunId, continuationRunId, StringComparison.Ordinal) ? state : null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId)
            => string.Equals(state.CorrelationId, correlationId, StringComparison.Ordinal) ? state : null;
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId) => null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => false;
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }
        public void ReleaseForRetry(string waitId, string leaseOwner) { }
        public void MarkPausedAgain(string waitId) { }
        public void ReleaseExecutingWait(string waitId) { }
        public void MarkCompleted(string waitId) { }
        public void MarkFailed(string waitId, string failureCode) { }
        public IReadOnlyList<RunWaitState> ListRecoverable() => [state];
        public void DeleteBySession(string sessionId) { }
    }

    private sealed class NoOpAgentToolInvocationLinkStore : IAgentToolInvocationLinkStore
    {
        public void Upsert(AgentToolInvocationLink link) { }

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId) => null;

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId) => [];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId) => null;
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId) => [];
        public bool HasLinksForSession(string sessionId) => false;
    }

    private sealed class RecordingAgentToolInvocationLinkStore : IAgentToolInvocationLinkStore
    {
        private readonly List<AgentToolInvocationLink> links = [];

        public IReadOnlyList<AgentToolInvocationLink> Links => links;

        public void Upsert(AgentToolInvocationLink link) => links.Add(link);

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
            => links.LastOrDefault(link => string.Equals(link.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal));

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
            => [.. links.Where(link => string.Equals(link.CallerSessionId, callerSessionId, StringComparison.Ordinal))];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
            => links.LastOrDefault(link => string.Equals(link.ChildSessionId, childSessionId, StringComparison.Ordinal));
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
            => [.. links.Where(link => string.Equals(link.RootRunId, rootRunId, StringComparison.Ordinal))];
        public bool HasLinksForSession(string sessionId)
            => links.Any(link => string.Equals(link.CallerSessionId, sessionId, StringComparison.Ordinal));
    }

    private sealed class NoOpGatewayApprovalStore : IGatewayApprovalStore
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

}
