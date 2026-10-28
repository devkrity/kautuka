using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class ExecutionCrashPointRecoveryTests
{
    [Fact]
    public async Task StartupRecoveryFailsInterruptedRootExactlyOnceAcrossRepeatedReconciliation()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        var context = new NaradEventContext(
            "agent-parent", "session-parent", "user-1", "root-run-interrupted", "root-run-interrupted");

        await using (ServiceProvider beforeRestart = harness.Restart())
        {
            await beforeRestart.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "interrupted-root-started",
                NaradEventTypes.RunStarted,
                Payload("started"),
                context,
                TestContext.Current.CancellationToken);
        }

        await using ServiceProvider provider = harness.Restart();
        ExecutionReconciliationService reconciler = provider.GetRequiredService<ExecutionReconciliationService>();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<NaradPersistedEvent> history = provider
            .GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "root-run-interrupted");
        NaradPersistedEvent failed = Assert.Single(
            history,
            static item => item.EventId == "recovery:root-run-interrupted:run-terminal");
        Assert.Equal(NaradEventTypes.RunError, failed.EventType);
        Assert.Equal("gateway_process_lost", NaradPersistedEventReader.String(failed, "code"));
        NaradPersistedEvent interruption = Assert.Single(
            history,
            static item => item.EventType == NaradEventTypes.MessageTextDelta);
        string? recoveryMessageId = NaradPersistedEventReader.String(interruption, "messageId");
        Assert.False(string.IsNullOrWhiteSpace(recoveryMessageId));
        string? interruptionText = NaradPersistedEventReader.String(interruption, "delta");
        Assert.Contains("process restarted", interruptionText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", interruptionText, StringComparison.OrdinalIgnoreCase);
        NaradPersistedEvent assistantStarted = Assert.Single(
            history,
            item => item.EventType == NaradEventTypes.MessageTextStarted
                && NaradPersistedEventReader.String(item, "messageId") == recoveryMessageId);
        NaradPersistedEvent assistantCompleted = Assert.Single(
            history,
            item => item.EventType == NaradEventTypes.MessageTextCompleted
                && NaradPersistedEventReader.String(item, "messageId") == recoveryMessageId);
        var orderedHistory = history.ToList();
        Assert.True(orderedHistory.IndexOf(assistantStarted) < orderedHistory.IndexOf(interruption));
        Assert.True(orderedHistory.IndexOf(interruption) < orderedHistory.IndexOf(assistantCompleted));
        Assert.True(orderedHistory.IndexOf(assistantCompleted) < orderedHistory.IndexOf(failed));
    }

    [Fact]
    public async Task StartupRecoveryCompletesDurableUserStopAsUserStoppedInsteadOfGatewayLoss()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        var context = new NaradEventContext(
            "agent-parent",
            "session-parent",
            "user-1",
            "root-run-stopped",
            "root-run-stopped",
            SourceChannel: "chat");

        await using (ServiceProvider beforeRestart = harness.Restart())
        {
            IRuntimeEventPublisher publisher = beforeRestart.GetRequiredService<IRuntimeEventPublisher>();
            await publisher.PublishWithIdAsync(
                "stopped-root-started",
                NaradEventTypes.RunStarted,
                Payload("started"),
                context,
                TestContext.Current.CancellationToken);
            await publisher.PublishWithIdAsync(
                "run-stop:root-run-stopped",
                NaradEventTypes.RunStopRequested,
                JsonSerializer.SerializeToElement(new
                {
                    actor = "Admin User",
                    channel = "chat",
                    reason = "user_requested",
                }),
            context,
            TestContext.Current.CancellationToken);
        }

        await using ServiceProvider afterRestart = harness.Restart();
        await afterRestart.GetRequiredService<ExecutionReconciliationService>()
            .ReconcileAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<NaradPersistedEvent> history = afterRestart
            .GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "root-run-stopped");
        NaradPersistedEvent failed = Assert.Single(
            history,
            static item => item.EventId == "recovery:root-run-stopped:run-terminal");
        Assert.Equal("user_stopped", NaradPersistedEventReader.String(failed, "code"));
        Assert.Equal("This run was stopped by the user.", NaradPersistedEventReader.String(failed, "message"));
        Assert.DoesNotContain("process restarted", NaradPersistedEventReader.String(failed, "message") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        NaradPersistedEvent activity = Assert.Single(
            history,
            static item => item.EventType == NaradEventTypes.MessageTextDelta);
        Assert.Contains("Admin User", NaradPersistedEventReader.String(activity, "delta") ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(history, static item => item.EventType == NaradEventTypes.RunStopRequested);
        Assert.Single(history, static item => item.EventType == NaradEventTypes.RunCancelled);
    }

    [Fact]
    public async Task StartupRecoveryFailsInterruptedAaatInvocationAndRootWithoutDurableWait()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        string invocationId;

        await using (ServiceProvider beforeRestart = harness.Restart())
        {
            IAgentToolInvocationStore invocations = beforeRestart.GetRequiredService<IAgentToolInvocationStore>();
            AgentToolInvocation invocation = invocations.CreateWaiting(InvocationRequest());
            invocationId = invocation.InvocationId;
            await beforeRestart.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "interrupted-aaat-root-started",
                NaradEventTypes.RunStarted,
                Payload("started"),
                new NaradEventContext(
                    "agent-parent", "parent-session-1", "user-1", "root-run-1", "root-run-1"),
                TestContext.Current.CancellationToken);
        }

        await using ServiceProvider provider = harness.Restart();
        ExecutionReconciliationService reconciler = provider.GetRequiredService<ExecutionReconciliationService>();
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);
        await reconciler.ReconcileAsync(TestContext.Current.CancellationToken);

        IAgentToolInvocationStore invocationsAfter = provider.GetRequiredService<IAgentToolInvocationStore>();
        AgentToolInvocation failedInvocation = Assert.IsType<AgentToolInvocation>(invocationsAfter.Get(invocationId));
        Assert.Equal(AgentToolInvocationStates.Failed, failedInvocation.State);
        Assert.Equal("gateway_process_lost", failedInvocation.FailureCode);

        IReadOnlyList<NaradPersistedEvent> history = provider
            .GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "root-run-1");
        NaradPersistedEvent childTerminal = Assert.Single(
            history,
            item => item.EventId == "recovery:root-run-1:child-terminal:child-run-1");
        Assert.Equal(NaradEventTypes.RunError, childTerminal.EventType);
        Assert.Equal("gateway_process_lost", NaradPersistedEventReader.String(childTerminal, "code"));
        NaradPersistedEvent toolFailed = Assert.Single(
            history,
            item => item.EventId == $"aaat:{invocationId}:tool-terminal");
        Assert.Equal(NaradEventTypes.ToolCallFinished, toolFailed.EventType);
        Assert.Equal("gateway_process_lost", NaradPersistedEventReader.String(toolFailed, "code"));
        Assert.DoesNotContain(history, static item => item.EventType.Contains("agent.invocation.", StringComparison.Ordinal));
        Assert.Single(history, static item => item.EventId == "recovery:root-run-1:run-terminal");
    }

    [Fact]
    public async Task StartupRecoveryDoesNotFailInterruptedRootProtectedByDurableWait()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        harness.InsertApproval("approval-protected", "run-parent");

        await using ServiceProvider provider = harness.Restart();
        RunWaitState wait = provider.GetRequiredService<IRunWaitStateStore>()
            .PauseRunForApproval(WaitRequest("approval-protected", "event-paused"));
        var context = new NaradEventContext(
            "agent-parent", "session-parent", "user-1", "run-parent", "run-parent");
        await provider.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
            "protected-root-started",
            NaradEventTypes.RunStarted,
            Payload("started"),
            context,
            TestContext.Current.CancellationToken);

        await provider.GetRequiredService<ExecutionReconciliationService>()
            .ReconcileAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<NaradPersistedEvent> history = provider
            .GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "run-parent");
        Assert.DoesNotContain(history, static item => item.EventType == NaradEventTypes.RunError);
        Assert.DoesNotContain(
            history,
            static item => item.EventId == "recovery:run-parent:delivery-completed");
        Assert.Equal(RunWaitStatuses.Ready, provider.GetRequiredService<IRunWaitStateStore>().GetByWaitId(wait.WaitId)?.Status);
    }

    [Fact]
    public async Task StartupRecoveryFailsInterruptedRootWithResolvedChatApprovalWait()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        harness.InsertApproval("approval-resolved-chat", "run-parent");
        harness.ResolveApprovalAsChat("approval-resolved-chat");

        await using ServiceProvider provider = harness.Restart();
        IRunWaitStateStore waits = provider.GetRequiredService<IRunWaitStateStore>();
        RunWaitState wait = waits.PauseRunForApproval(WaitRequest("approval-resolved-chat", "event-paused-chat"));
        var context = new NaradEventContext(
            "agent-parent", "session-parent", "user-1", "run-parent", "run-parent");
        await provider.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
            "resolved-chat-root-started",
            NaradEventTypes.RunStarted,
            Payload("started"),
            context,
            TestContext.Current.CancellationToken);

        await provider.GetRequiredService<ExecutionReconciliationService>()
            .ReconcileAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<NaradPersistedEvent> history = provider
            .GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "run-parent");
        Assert.Single(history, static item => item.EventId == "recovery:run-parent:run-terminal");
        Assert.Single(history, static item => item.EventType == NaradEventTypes.RunError);
        RunWaitState failedWait = Assert.IsType<RunWaitState>(waits.GetByWaitId(wait.WaitId));
        Assert.Equal(RunWaitStatuses.Failed, failedWait.Status);
        Assert.Equal("gateway_process_lost", failedWait.FailureCode);
    }

    [Fact]
    public async Task ContinuationClaimCrashIsReclaimedAndCompletedByFreshProcess()
    {
        await using var harness = new CrashHarness();
        harness.InsertApproval("approval-claim", "child-run-1");
        ApprovalContinuationWork work = Work("approval-claim");

        await using (ServiceProvider first = harness.Restart())
        {
            IApprovalContinuationQueue queue = first.GetRequiredService<IApprovalContinuationQueue>();
            await queue.EnqueueAsync(work, TestContext.Current.CancellationToken);
            ApprovalContinuationLease crashed = await LeaseOneAsync(queue, "process-1");
            Assert.Equal(1, crashed.AttemptCount);
        }

        harness.Clock.Advance(TimeSpan.FromMinutes(3));
        await using (ServiceProvider recovered = harness.Restart())
        {
            IApprovalContinuationQueue queue = recovered.GetRequiredService<IApprovalContinuationQueue>();
            ApprovalContinuationLease lease = await LeaseOneAsync(queue, "process-2");
            Assert.Equal(2, lease.AttemptCount);
            await queue.CompleteAsync(lease, TestContext.Current.CancellationToken);
        }

        Assert.Equal("completed", harness.ReadContinuationStatus("approval-claim"));
    }

    [Fact]
    public async Task ResumedEventCrashRetainsWaitAndCompletesWithOneCanonicalEventAfterRestart()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        string waitId;
        var context = new NaradEventContext("agent-parent", "session-parent", "user-1", "run-parent", "run-parent");

        await using (ServiceProvider first = harness.Restart())
        {
            IRunWaitStateStore waits = first.GetRequiredService<IRunWaitStateStore>();
            RunWaitState wait = waits.PauseRunForApproval(WaitRequest("approval-resume", "event-paused"));
            waitId = wait.WaitId;
            Assert.True(waits.TryClaimExecution(waitId, "process-1", TimeSpan.FromMinutes(2)));
            await first.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "event-resumed",
                NaradEventTypes.RunResumed,
                Payload("resumed"),
                context,
                TestContext.Current.CancellationToken);
        }

        harness.ExpireWaitLease(waitId);
        await using (ServiceProvider recovered = harness.Restart())
        {
            IRunWaitStateStore waits = recovered.GetRequiredService<IRunWaitStateStore>();
            Assert.Equal(RunWaitStatuses.Executing, waits.GetByWaitId(waitId)?.Status);
            Assert.True(waits.TryClaimExecution(waitId, "process-2", TimeSpan.FromMinutes(1)));
            await recovered.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "event-resumed",
                NaradEventTypes.RunResumed,
                Payload("resumed"),
                context,
                TestContext.Current.CancellationToken);
            waits.MarkCompleted(waitId);
        }

        await using ServiceProvider verification = harness.Restart();
        Assert.Equal(RunWaitStatuses.Completed, verification.GetRequiredService<IRunWaitStateStore>().GetByWaitId(waitId)?.Status);
        Assert.Single(
            verification.GetRequiredService<IAgentDefinitionStore>().ListNaradRootHistoryFull("agent-parent", "run-parent"),
            static item => item.EventId == "event-resumed");
    }

    [Fact]
    public async Task TwoChildApprovalsAndTerminalCrashResumeOriginalParentExactlyOnce()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        harness.InsertAgent("agent-child");
        harness.InsertApproval("approval-1", "child-run-1");
        harness.InsertApproval("approval-2", "child-run-2");
        string invocationId;

        await using (ServiceProvider first = harness.Restart())
        {
            IAgentToolInvocationStore invocations = first.GetRequiredService<IAgentToolInvocationStore>();
            AgentToolInvocation invocation = invocations.CreateWaiting(InvocationRequest());
            invocationId = invocation.InvocationId;
            invocations.RecordApprovalLink(Link(invocationId, "approval-1", "child-run-1", "approval-event-1"));
        }

        await using (ServiceProvider second = harness.Restart())
        {
            await second.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "root-run-bootstrap",
                NaradEventTypes.RunStarted,
                Payload("bootstrap"),
                new NaradEventContext("agent-parent", "parent-session-1", "user-1", "root-run-1", "root-run-1"),
                TestContext.Current.CancellationToken);
            ApprovalResolutionResult resolved = await second.GetRequiredService<IApprovalResolutionService>().ResolveAsync(
                new ApprovalResolutionCommand("approval-1", true, null, "user-1", HitlResolutionSource.ApprovalApi),
                TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalResolutionStatus.Resolved, resolved.Status);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await second.GetRequiredService<IApprovalResolutionEventDispatcher>()
                .DispatchAsync("approval-1", TestContext.Current.CancellationToken);
            Assert.True(
                harness.ReadResolutionPublishedAt("approval-1") is not null,
                harness.DescribeResolutionEvent("approval-1"));
            Assert.Equal("pending", harness.ReadContinuationStatus("approval-1"));
            IAgentToolInvocationStore invocations = second.GetRequiredService<IAgentToolInvocationStore>();
            Assert.Equal(AgentToolInvocationStates.WaitingOnChild, invocations.Get(invocationId)?.State);
        }

        await using (ServiceProvider afterFirstResolution = harness.Restart())
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            IApprovalContinuationQueue queue = afterFirstResolution.GetRequiredService<IApprovalContinuationQueue>();
            ApprovalContinuationLease firstContinuation = await LeaseOneAsync(queue, "approval-1-worker");
            await queue.CompleteAsync(firstContinuation, TestContext.Current.CancellationToken);
            IAgentToolInvocationStore invocations = afterFirstResolution.GetRequiredService<IAgentToolInvocationStore>();
            invocations.RecordApprovalLink(Link(invocationId, "approval-2", "child-run-2", "approval-event-2"));
            Assert.Equal(AgentToolInvocationStates.WaitingOnChild, invocations.Get(invocationId)?.State);
        }

        await using (ServiceProvider fourth = harness.Restart())
        {
            ApprovalResolutionResult resolved = await fourth.GetRequiredService<IApprovalResolutionService>().ResolveAsync(
                new ApprovalResolutionCommand("approval-2", true, null, "user-1", HitlResolutionSource.ApprovalApi),
                TestContext.Current.CancellationToken);
            Assert.Equal(ApprovalResolutionStatus.Resolved, resolved.Status);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            await fourth.GetRequiredService<IApprovalResolutionEventDispatcher>()
                .DispatchAsync("approval-2", TestContext.Current.CancellationToken);
            Assert.NotNull(harness.ReadResolutionPublishedAt("approval-2"));
            Assert.Equal(
                AgentToolInvocationStates.WaitingOnChild,
                fourth.GetRequiredService<IAgentToolInvocationStore>().Get(invocationId)?.State);
        }

        await using (ServiceProvider terminalCrash = harness.Restart())
        {
            IApprovalContinuationQueue queue = terminalCrash.GetRequiredService<IApprovalContinuationQueue>();
            ApprovalContinuationLease secondContinuation = await LeaseOneAsync(queue, "approval-2-worker");
            await queue.CompleteAsync(secondContinuation, TestContext.Current.CancellationToken);
            IAgentToolInvocationStore invocations = terminalCrash.GetRequiredService<IAgentToolInvocationStore>();
            invocations.RecordChildCompletion(invocationId, "{\"result\":\"73\"}", "completed");
            Assert.True(invocations.TryClaimParentCompletion(invocationId, "process-2", TimeSpan.FromMinutes(2)));
            await PublishChildTerminalAsync(terminalCrash, "child-terminal");
        }

        harness.ExpireInvocationLease(invocationId);
        await using (ServiceProvider recovered = harness.Restart())
        {
            IAgentToolInvocationStore invocations = recovered.GetRequiredService<IAgentToolInvocationStore>();
            Assert.True(invocations.TryClaimParentCompletion(invocationId, "process-3", TimeSpan.FromMinutes(1)));
            await PublishChildTerminalAsync(recovered, "child-terminal");
            await recovered.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
                "parent-resumed",
                NaradEventTypes.RunResumed,
                Payload("parent-resumed"),
                ParentContext(),
                TestContext.Current.CancellationToken);
            invocations.MarkCompleted(invocationId, "process-3");
        }

        await using ServiceProvider verification = harness.Restart();
        AgentToolInvocation completed = Assert.IsType<AgentToolInvocation>(verification.GetRequiredService<IAgentToolInvocationStore>().Get(invocationId));
        Assert.Equal(AgentToolInvocationStates.Completed, completed.State);
        Assert.False(verification.GetRequiredService<IAgentToolInvocationStore>()
            .TryClaimParentCompletion(invocationId, "process-4", TimeSpan.FromMinutes(1)));
        IAgentDefinitionStore events = verification.GetRequiredService<IAgentDefinitionStore>();
        IReadOnlyList<NaradPersistedEvent> history = events.ListNaradRootHistoryFull("agent-parent", "root-run-1");
        Assert.Single(history, static item => item.EventId == "child-terminal");
        Assert.Single(history, static item => item.EventId == "parent-resumed");
        var links = new GatewayAgentToolInvocationLinkStore(harness.Database);
        Assert.Equal(invocationId, links.GetByApprovalRequestId("approval-1")?.InvocationId);
        Assert.Equal(invocationId, links.GetByApprovalRequestId("approval-2")?.InvocationId);
    }

    [Fact]
    public async Task StartupRecoveryFinalizesInvocationWhenParentTerminalPrecededStateWrite()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        harness.InsertAgent("agent-child");
        string invocationId;

        await using (ServiceProvider crashed = harness.Restart())
        {
            IAgentToolInvocationStore invocations = crashed.GetRequiredService<IAgentToolInvocationStore>();
            AgentToolInvocation invocation = invocations.CreateWaiting(InvocationRequest());
            invocationId = invocation.InvocationId;
            invocations.RecordChildCompletion(invocationId, "{\"result\":\"73\"}", "completed");
            Assert.True(invocations.TryClaimParentCompletion(invocationId, "crashed-worker", TimeSpan.FromMinutes(2)));
            IRuntimeEventPublisher publisher = crashed.GetRequiredService<IRuntimeEventPublisher>();
            var rootContext = new NaradEventContext(
                "agent-parent", "parent-session-1", "user-1", "root-run-1", "root-run-1",
                RootAgentId: "agent-parent");
            await publisher.PublishWithIdAsync(
                "parent-resumed-before-crash",
                NaradEventTypes.RunResumed,
                Payload("resumed"),
                rootContext,
                TestContext.Current.CancellationToken);
            await publisher.PublishWithIdAsync(
                "parent-finished-before-state-write",
                NaradEventTypes.RunFinished,
                Payload("finished"),
                rootContext,
                TestContext.Current.CancellationToken);
        }

        harness.ExpireInvocationLease(invocationId);
        await using ServiceProvider recovered = harness.Restart();
        await recovered.GetRequiredService<ExecutionReconciliationService>()
            .ReconcileAsync(TestContext.Current.CancellationToken);

        AgentToolInvocation completed = Assert.IsType<AgentToolInvocation>(
            recovered.GetRequiredService<IAgentToolInvocationStore>().Get(invocationId));
        Assert.Equal(AgentToolInvocationStates.Completed, completed.State);
        Assert.False(recovered.GetRequiredService<IAgentToolInvocationStore>()
            .TryClaimParentCompletion(invocationId, "duplicate-worker", TimeSpan.FromMinutes(1)));
        IReadOnlyList<NaradPersistedEvent> history = recovered.GetRequiredService<IAgentDefinitionStore>()
            .ListNaradRootHistoryFull("agent-parent", "root-run-1");
        Assert.Single(history, static item => item.EventId == "parent-finished-before-state-write");
    }

    [Fact]
    public async Task RuntimeOutboxCrashAfterMirrorIsRecoveredByFreshProcessWithoutDuplicateHistory()
    {
        await using var harness = new CrashHarness();
        harness.InsertAgent("agent-parent");
        var context = new NaradEventContext("agent-parent", "session-parent", "user-1", "run-outbox", "run-outbox");

        await using (ServiceProvider first = harness.Restart())
        {
            IAgentDefinitionStore store = first.GetRequiredService<IAgentDefinitionStore>();
            NaradPersistedEvent committed = store.AppendNaradAuthorityEvent(
                "agent-parent",
                "agent-parent",
                NaradDraft("outbox-terminal", NaradEventTypes.RunFinished, context));
            NaradRuntimeEventOutboxEntry lease = Assert.Single(store.ClaimNaradEventOutbox("agent-parent", "process-1", TimeSpan.FromMinutes(2), 1));
            store.MirrorNaradAuthorityEvent(lease.SourceStorageLocatorKey, lease.Event);
            store.MarkNaradEventMirrored("agent-parent", lease.Event.EventId, "process-1");
        }

        harness.ExpireRuntimeOutboxLease("agent-parent", "outbox-terminal");
        await using (ServiceProvider recovered = harness.Restart())
        {
            _ = recovered.GetRequiredService<IRuntimeEventPublisher>();
            IAgentDefinitionStore store = recovered.GetRequiredService<IAgentDefinitionStore>();
            await AssertEventuallyAsync(() => store.ListPendingNaradEventOutbox("agent-parent", 10).Count == 0);
            Assert.Single(store.ListNaradRootHistoryFull("agent-parent", "run-outbox"), static item => item.EventId == "outbox-terminal");
        }
    }

    private static async ValueTask<ApprovalContinuationLease> LeaseOneAsync(IApprovalContinuationQueue queue, string owner)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ApprovalContinuationLease> enumerator = queue.LeaseAllAsync(owner, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async ValueTask PublishChildTerminalAsync(IServiceProvider provider, string eventId)
        => await provider.GetRequiredService<IRuntimeEventPublisher>().PublishWithIdAsync(
            eventId,
            NaradEventTypes.RunFinished,
            Payload("child-finished"),
            new NaradEventContext(
                "agent-child", "child-session-1", "user-1", "child-run-2", "root-run-1",
                ParentRunId: "parent-run-1", ParentSessionId: "parent-session-1",
                ParentToolCallId: "parent-tool-call-1", RootAgentId: "agent-parent"),
            TestContext.Current.CancellationToken);

    private static AgentToolInvocationCreateRequest InvocationRequest() => new(
        "parent-run-1", "parent-session-1", "parent-tool-call-1", "invoke_agent_child",
        "child-run-1", "child-session-1", "agent-child", "root-run-1", "parent-continuation-1");

    private static AgentToolInvocationLink Link(string invocationId, string approvalId, string childRunId, string eventId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new AgentToolInvocationLink(
            approvalId, "parent-session-1", now, now, eventId, "parent-tool-call-1",
            "invoke_agent_child", "parent-continuation-1", "child-session-1", "parent-run-1",
            childRunId, "agent-child", "root-run-1", invocationId);
    }

    private static RunWaitStateCreateRequest WaitRequest(string approvalId, string eventId) => new(
        "continuation-1", "run-parent", "agent", "agent-parent", "session-parent", "agent-parent",
        "user-1", approvalId, "{}", "{}", "[]", eventId, ApprovalRequestId: approvalId,
        RootRunId: "run-parent");

    private static ApprovalContinuationWork Work(string approvalId) => new(
        approvalId, "child-session-1", "agent", "agent-child", "user-1", true,
        HitlResolutionSource.ApprovalApi, null, "user-1");

    private static NaradEventContext ParentContext() => new(
        "agent-parent", "parent-session-1", "user-1", "parent-run-1", "root-run-1",
        ParentToolCallId: "parent-tool-call-1", RootAgentId: "agent-parent");

    private static NaradAuthorityEventDraft NaradDraft(string id, string type, NaradEventContext context)
        => new(
            id,
            type,
            context.SessionId,
            Payload(id),
            DateTimeOffset.UtcNow.ToString("O"),
            NaradRecordClasses.Authority,
            NaradProtocolVersions.V1,
            context.RootRunId,
            context.RunId);

    private static JsonElement Payload(string value)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"value":"{{value}}"}""");
        return document.RootElement.Clone();
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class CrashHarness : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-execution-crash", Guid.NewGuid().ToString("N"));

        public CrashHarness()
        {
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
                .Build();
            Database = new KautukaDatabase(Configuration);
            Database.EnsureReady();
        }

        public IConfiguration Configuration { get; }

        public KautukaDatabase Database { get; }

        public MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public ServiceProvider Restart()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Configuration);
            services.AddSingleton(_ => new KautukaDatabase(Configuration));
            services.AddSingleton<TimeProvider>(Clock);
            services.AddLogging();
            services.AddSingleton<IApprovalContinuationQueue, ApprovalContinuationQueue>();
            services.AddSingleton<IRunWaitStateStore, SqliteRunWaitStateStore>();
            services.AddSingleton<IAgentToolInvocationStore, GatewayAgentToolInvocationStore>();
            services.AddSingleton<IAgentToolInvocationLinkStore, GatewayAgentToolInvocationLinkStore>();
            services.AddSingleton<IAgentDefinitionStore>(static sp =>
                new Kautuka.Gateway.Infrastructure.Configuration.SqliteAgentDefinitionStore(
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<KautukaDatabase>()));
            services.AddSingleton<IRuntimeEventStreamHub, RuntimeEventStreamHub>();
            services.AddSingleton<IRuntimeEventPublisher, RuntimeEventPublisher>();
            services.AddSingleton<IRuntimeEventHistoryReader, RuntimeEventHistoryReader>();
            services.AddSingleton<IExecutionQuarantineStore, ExecutionQuarantineStore>();
            services.AddSingleton<IRootRunAdmissionGate, RootRunAdmissionGate>();
            services.AddSingleton<GatewayApprovalStore>();
            services.AddSingleton<IGatewayApprovalStore>(static provider => provider.GetRequiredService<GatewayApprovalStore>());
            services.AddSingleton<IApprovalResolutionEventStore>(static provider => provider.GetRequiredService<GatewayApprovalStore>());
            services.AddSingleton<IApprovalResolutionSideEffectApplier, ApprovalResolutionSideEffectApplier>();
            services.AddSingleton<ApprovalResolutionEventDispatcher>();
            services.AddSingleton<IApprovalResolutionEventDispatcher>(static provider => provider.GetRequiredService<ApprovalResolutionEventDispatcher>());
            services.AddSingleton<IApprovalBypassRegistry, ApprovalBypassRegistry>();
            services.AddSingleton<IRunStopRegistry, RunStopRegistry>();
            services.AddSingleton<IRunStopCommandService, RunStopCommandService>();
            services.AddSingleton<IMediator, NoOpMediator>();
            services.AddSingleton<IApprovalResolutionService, ApprovalResolutionService>();
            services.AddSingleton<IGatewayCatalogStore, EmptyCatalogStore>();
            services.AddSingleton<ExecutionReconciliationService>();
            return services.BuildServiceProvider(validateScopes: true);
        }

        public void InsertAgent(string agentId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at)
                VALUES ($id, 'user', 1, 'user-1', $id, $now, $now)
                """;
            command.Parameters.AddWithValue("$id", agentId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        public void InsertApproval(string requestId, string runId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO tool_approval_requests (id, session_id, run_id, agent_id, user_id, tool_call_id, tool_name, requested_at)
                VALUES ($id, 'child-session-1', $runId, 'agent-child', 'user-1', $toolCallId, 'run_shell', $now)
                """;
            command.Parameters.AddWithValue("$id", requestId);
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$toolCallId", $"tool-{requestId}");
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        public void ResolveApprovalAsChat(string requestId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE tool_approval_requests
                SET decision = 'approved', decided_by = 'user-1', decided_at = $now, resolution_source = 'chat'
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", requestId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void ExpireWaitLease(string waitId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE run_wait_states SET lease_expires_at = $expired WHERE wait_id = $waitId";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$waitId", waitId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void ExpireInvocationLease(string invocationId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_tool_invocations SET completion_lease_expires_at = $expired WHERE invocation_id = $invocationId";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$invocationId", invocationId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void ExpireRuntimeOutboxLease(string storageLocatorKey, string eventId)
        {
            string databasePath = Path.Combine(root, "agents", storageLocatorKey, "agent.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE runtime_event_outbox SET lease_expires_at = $expired WHERE event_id = $eventId";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$eventId", eventId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public string? ReadContinuationStatus(string requestId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM approval_continuation_jobs WHERE request_id = $requestId";
            command.Parameters.AddWithValue("$requestId", requestId);
            return command.ExecuteScalar()?.ToString();
        }

        public string? ReadResolutionPublishedAt(string requestId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT published_at FROM approval_resolution_events WHERE request_id = $requestId LIMIT 1";
            command.Parameters.AddWithValue("$requestId", requestId);
            object? scalar = command.ExecuteScalar();
            return scalar is null or DBNull ? null : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }

        public string DescribeResolutionEvent(string requestId)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT status, published_at, last_error FROM approval_resolution_events WHERE request_id = $requestId LIMIT 1";
            command.Parameters.AddWithValue("$requestId", requestId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return "missing";
            }

            string status = reader.IsDBNull(0) ? "null" : reader.GetString(0);
            string publishedAt = reader.IsDBNull(1) ? "null" : reader.GetString(1);
            string lastError = reader.IsDBNull(2) ? "null" : reader.GetString(2);
            return $"status={status}; published_at={publishedAt}; last_error={lastError}";
        }

        public async ValueTask DisposeAsync()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }

    private sealed class NoOpMediator : IMediator
    {
        public ValueTask Publish(object notification, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => ValueTask.CompletedTask;
        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyCatalogStore : IGatewayCatalogStore
    {
        public bool TryGetProvider(string id, out GatewayProvider? provider) { provider = null; return false; }
        public IReadOnlyList<GatewayProvider> ListProviders() => [];
        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();
        public bool DeleteProvider(string id) => false;
        public bool TryGetModel(string id, out GatewayModel? model) { model = null; return false; }
        public IReadOnlyList<GatewayModel> ListModels() => [];
        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();
        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) => throw new NotSupportedException();
        public bool DeleteModel(string id) => false;
        public bool TryGetAgent(string id, out GatewayAgent? agent) { agent = null; return false; }
        public IReadOnlyList<GatewayAgent> ListAgents() => [];
        public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteAgent(string id) => false;
        public IReadOnlyList<GatewayMcpServer> ListMcpServers() => [];
        public bool TryGetMcpServer(string id, out GatewayMcpServer? server) { server = null; return false; }
        public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId) => throw new NotSupportedException();
        public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames) => throw new NotSupportedException();
        public bool DeleteMcpServer(string id) => false;
        public IReadOnlyList<GatewaySkill> ListSkills() => [];
        public bool TryGetSkill(string id, out GatewaySkill? skill) { skill = null; return false; }
        public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId) => throw new NotSupportedException();
        public bool DeleteSkill(string id) => false;
    }
}
