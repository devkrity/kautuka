using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ThreadCommandServiceTests
{
    [Fact]
    public async Task ThirdWorkflowChangeRequestIsRejectedBeforeClosingReview()
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode("node-1", "agent-1", "Review", "schema", new JsonObject(), [], "none", false, true, 0);
        var definition = new GatewayWorkflowDefinition("workflow-1", "user-1", "Review", false, 1,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, [node], now, now);
        GatewayWorkflowStepAttempt[] attempts = Enumerable.Range(1, 3).Select(attempt =>
            new GatewayWorkflowStepAttempt($"attempt-{attempt}", "instance-1", node.NodeId, attempt,
                $"run-{attempt}", "session-1", WorkflowStepAttemptStatuses.Finished, null, now, now, now)).ToArray();
        var instance = new GatewayWorkflowInstance("instance-1", definition.Id, 1, "user-1", null,
            WorkflowInstanceStatuses.Paused, false, node.NodeId, new JsonObject(), "hash", 0,
            definition, attempts, null, null, now, now, now, null);
        var workflows = new TestGatewayWorkflowStore(instance);
        GatewayToolApprovalRequest request = CreateRequest("review-1") with
        {
            ToolName = "workflow_review",
            ToolType = "workflow_review",
            Kind = "workflow_review",
            WorkflowId = definition.Id,
            InstanceId = instance.Id,
            NodeId = node.NodeId,
        };
        var approvals = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvals, workflowStore: workflows);
        ThreadCommandResult result = await service.ResolveInterruptAsync(new ResolveThreadInterruptCommand(
            request.Id, ApprovalActionCatalog.RequestChanges, "Please change this", "user-1", "Owner", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Rejected, result.Status);
        Assert.Equal(WorkflowFailureCodes.GateChangesExhaustedMessage, result.Detail);
        Assert.Null(approvals.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task ResolveInterruptApprovesWithoutRegisteringRunBypass()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("approved", approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task ResolveAllInterruptsReturnsNotFoundWithoutSpinning()
    {
        GatewayToolApprovalRequest ghost = CreateRequest("ghost-request");
        var approvalStore = new GhostPendingApprovalStore(ghost);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult result = await service.ResolveAllInterruptsAsync(
            new ResolveAllThreadInterruptsCommand(
                "agent-1",
                "session-1",
                "run-1",
                Approved: true,
                TrustThisRun: false,
                StopRun: false,
                Reason: null,
                "user-1",
                "Admin User",
                ExpectedRevision: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.NotFound, result.Status);
        Assert.Equal(0, result.ResolvedCount);
    }

    [Fact]
    public async Task RepeatedResolveInterruptIsIdempotent()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult first = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: true),
            TestContext.Current.CancellationToken);
        ThreadCommandResult second = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, first.Status);
        Assert.Equal(ThreadCommandStatus.AlreadyApplied, second.Status);
    }

    [Fact]
    public async Task ResolveInterruptWithStopRunPersistsDurableStopBeforeResolution()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateStopCapableService(approvalStore, out ThreadCommandAgentStore agentStore, out _);
        var publisher = new ThreadCommandPublisher(agentStore, static () => false);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("stopped", approvalStore.GetById(request.Id)?.Decision);
        Assert.True(agentStore.IsNaradRootRunAdmissionClosed("agent-1", "run-1"));
    }

    [Fact]
    public async Task StaleExpectedRevisionReturnsConflictWithoutMutatingApproval()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(RuntimeEvent(5, NaradEventTypes.RunStarted, new { trigger = "chat" }, rootRuntimeSequence: 9));
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: true, expectedRevision: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
        Assert.Null(approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task FutureExpectedRevisionReturnsConflictWithoutMutatingApproval()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(RuntimeEvent(5, NaradEventTypes.RunStarted, new { trigger = "chat" }, rootRuntimeSequence: 4));
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: true, expectedRevision: 9),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
        Assert.Null(approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task StopActiveRootDeniesPendingApprovalsForTheRun()
    {
        GatewayToolApprovalRequest first = CreateRequest("request-1");
        GatewayToolApprovalRequest second = CreateRequest("request-2");
        var approvalStore = new ThreadCommandApprovalStore(first, second);
        ThreadCommandService service = CreateStopCapableService(approvalStore, out _, out _);

        ThreadCommandResult result = await service.StopActiveRootAsync(
            new StopActiveRootCommand("agent-1", "session-1", "run-1", "stopped", "user-1", "Admin User", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal(2, result.ResolvedCount);
        Assert.Equal("stopped", approvalStore.GetById("request-1")?.Decision);
        Assert.Equal("stopped", approvalStore.GetById("request-2")?.Decision);
    }

    [Fact]
    public async Task ResolveAllStopsOnExpiredWithoutRunBypass()
    {
        GatewayToolApprovalRequest first = CreateRequest("request-1");
        GatewayToolApprovalRequest expired = CreateRequest("request-2") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        };
        var approvalStore = new ThreadCommandApprovalStore(first, expired);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult result = await service.ResolveAllInterruptsAsync(
            new ResolveAllThreadInterruptsCommand(
                "agent-1",
                "session-1",
                "run-1",
                Approved: true,
                TrustThisRun: true,
                StopRun: false,
                Reason: null,
                "user-1",
                "Admin User",
                ExpectedRevision: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Expired, result.Status);
        Assert.Equal(1, result.ResolvedCount);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("cancel")]
    public async Task UnsupportedTargetsReturnExplicitCapabilityResponse(string capability)
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughPartialJoin()]);
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = capability switch
        {
            "retry" => await service.RetryTargetAsync(
                new RetryExecutionTargetCommand("agent-1", WorkflowSliceFixture.ThreadId, "phase", "plan", "user-1", null),
                TestContext.Current.CancellationToken),
            _ => await service.CancelGraphNodeAsync(
                new CancelGraphNodeCommand("agent-1", WorkflowSliceFixture.ThreadId, "node-parallel-call-b", "user-1", null),
                TestContext.Current.CancellationToken),
        };

        Assert.Equal(ThreadCommandStatus.UnsupportedCapability, result.Status);
    }

    [Fact]
    public async Task StopActiveRootDeniesNestedPendingApprovalsScopedToRoot()
    {
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            "nested-approval",
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-nested",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run"));
        GatewayToolApprovalRequest nested = CreateRequest("nested-approval") with
        {
            SessionId = "child-session",
            RunId = "child-run",
            OuterSessionId = "session-1",
            AgentId = "child-agent",
        };
        var approvalStore = new ThreadCommandApprovalStore(nested);
        ThreadCommandService service = CreateStopCapableService(
            approvalStore,
            out ThreadCommandAgentStore agentStore,
            out _,
            invocationLinks: links);
        agentStore.AppendEvent(RuntimeEvent(
            1,
            NaradEventTypes.RunStarted,
            new { trigger = "chat" },
            runId: "root-run",
            rootRunId: "root-run"));

        ThreadCommandResult result = await service.StopActiveRootAsync(
            new StopActiveRootCommand("agent-1", "session-1", "root-run", "stopped", "user-1", "Admin User", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal(1, result.ResolvedCount);
        Assert.Equal("stopped", approvalStore.GetById("nested-approval")?.Decision);
    }

    [Fact]
    public async Task MultiRootStaleRevisionFromPriorRootConflictsWithoutMutation()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "run-1", rootRunId: "run-1"),
            RuntimeEvent(7, NaradEventTypes.RunFinished, new { }, runId: "run-1", rootRunId: "run-1", rootRuntimeSequence: 7),
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "run-2", rootRunId: "run-2"),
            RuntimeEvent(4, NaradEventTypes.RunPaused, new { reason = "tool_approval" }, runId: "run-2", rootRunId: "run-2", rootRuntimeSequence: 4));
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore);

        ThreadCommandResult stale = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, expectedRevision: 7),
            TestContext.Current.CancellationToken);
        ThreadCommandResult current = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, expectedRevision: 4),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, stale.Status);
        Assert.Equal(ThreadCommandStatus.Accepted, current.Status);
        Assert.Equal("approved", approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task NestedParentSessionRevisionAcceptsCurrentAndRejectsStale()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                3,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "root-run",
                sessionId: "child-session",
                parentSessionId: "session-1",
                rootRuntimeSequence: 3));
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore);

        ThreadCommandResult stale = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, expectedRevision: 2),
            TestContext.Current.CancellationToken);
        ThreadCommandResult current = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, expectedRevision: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, stale.Status);
        Assert.Equal(ThreadCommandStatus.Accepted, current.Status);
    }

    [Fact]
    public async Task NestedBrowserResolveSkipsDurableResumeWhenAgentToolWaiterOwnsApproval()
    {
        GatewayToolApprovalRequest request = CreateRequest("nested-apr") with
        {
            SessionId = "child-session",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
        };
        var approvalStore = new ThreadCommandApprovalStore(request);
        var queue = new RecordingContinuationQueue();
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                3,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "root-run",
                sessionId: "child-session",
                parentSessionId: "session-1",
                rootRuntimeSequence: 3));
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            continuationQueue: queue,
            agentToolApprovalWaiter: new ThreadCommandWaitingApprovalWaiter(request.Id));

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("approved", approvalStore.GetById(request.Id)?.Decision);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task RetryTargetRejectsApprovalFromDifferentThread()
    {
        GatewayToolApprovalRequest foreign = CreateRequest("foreign-approval") with
        {
            SessionId = "other-session",
            RunId = "other-run",
        };
        var approvalStore = new ThreadCommandApprovalStore(foreign);
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-foreign",
            "other-run",
            "other-run",
            "agent",
            "agent-1",
            "other-session",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            foreign.Id,
            "{}",
            "{}",
            "[]",
            "tool-call-1",
            "run_shell",
            foreign.Id,
            "other-session",
            "other-run",
            "other-run",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-1",
            "other-run"));
        var workflow = CreateWorkflowService(
            new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" })),
            new NoOpRunStopCommandService(),
            approvalStore,
            waitStore: waitStore);
        ThreadCommandService service = CreateService(approvalStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                "session-1",
                "approval",
                foreign.Id,
                "user-1",
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.NotFound, result.Status);
    }

    private static ThreadCommandService CreateStopCapableService(
        ThreadCommandApprovalStore approvalStore,
        out ThreadCommandAgentStore agentStore,
        out RunStopRegistry stopSignals,
        ApprovalResolutionTestInvocationLinkStore? invocationLinks = null)
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        agentStore = new ThreadCommandAgentStore(started);
        var stopSignalsLocal = new RunStopRegistry();
        stopSignals = stopSignalsLocal;
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignalsLocal.IsStopped("run-1")),
            stopSignalsLocal);
        return CreateService(
            approvalStore,
            agentStore,
            stopCommands,
            invocationLinks: invocationLinks);
    }

    [Fact]
    public async Task UnsupportedCapabilityWithStaleRevisionReturnsConflictBeforeUnsupported()
    {
        ThreadCommandService service = CreateService(
            new ThreadCommandApprovalStore(CreateRequest("request-1")),
            agentStore: new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, rootRuntimeSequence: 5)));

        ThreadCommandResult result = await service.ResumeWaitingRootAsync(
            new ResumeWaitingRootCommand("agent-1", "session-1", "run-1", "user-1", 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
    }

    private sealed class GhostPendingApprovalStore(GatewayToolApprovalRequest ghost) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) => throw new NotSupportedException();

        public bool Resolve(
            string requestId,
            string decision,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
            => throw new NotSupportedException();

        public bool Resolve(
            string requestId,
            bool approved,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation)
            => throw new NotSupportedException();

        public GatewayToolApprovalRequest? GetById(string requestId) => null;

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => string.Equals(sessionId, ghost.SessionId, StringComparison.Ordinal) ? [ghost] : [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string outerSessionId) => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;

        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    [Fact]
    public async Task ResolveInterruptFromBrowserEnqueuesContinuationAfterApproval()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1") with
        {
            ToolName = "run_skill_script",
            ToolType = "skill",
        };
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "run-1", rootRunId: "run-1"),
            RuntimeEvent(2, NaradEventTypes.RunPaused, new { reason = "tool_approval" }, runId: "run-1", rootRunId: "run-1", rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-1",
            "run-1",
            "run-1",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            request.Id,
            "{}",
            "{}",
            "[]",
            "tool-call-1",
            "run_skill_script",
            request.Id,
            null,
            "run-1",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-2",
            "run-1"));
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore);
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            workflowCommands: workflow,
            continuationQueue: continuationQueue);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(request.Id, continuationQueue.Enqueued[0].RequestId);
        Assert.Equal(HitlResolutionSource.Browser, continuationQueue.Enqueued[0].Source);
        Assert.True(continuationQueue.Enqueued[0].Approved);
    }

    [Fact]
    public async Task NestedBrowserResolveEnqueuesChildSessionContinuation()
    {
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            "nested-browser",
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-nested-browser",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run",
            CallerRunId: "root-run",
            CallerToolCallId: "invoke-1"));
        GatewayToolApprovalRequest nested = CreateRequest("nested-browser") with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolName = "run_skill_script",
            ToolType = "skill",
        };
        var approvalStore = new ThreadCommandApprovalStore(nested);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                10,
                NaradEventTypes.RunStarted,
                new { trigger = "agent_tool" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 1),
            RuntimeEvent(
                11,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child",
            "child-run",
            "child-run",
            "agent",
            "child-agent",
            "child-session",
            "child-agent",
            "user-1",
            RunWaitReasons.Approval,
            nested.Id,
            "{}",
            "{}",
            "[]",
            "tool-dice",
            "run_skill_script",
            nested.Id,
            null,
            "child-run",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-child-2",
            "child-run"));
        // Durable nested AAAT path parks the parent invoke_agent_* as waiting_on_child.
        waitStore.Seed(new RunWaitState(
            "wait-parent-child",
            "root-continuation",
            "root-run",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Child,
            nested.Id,
            "{}",
            "{}",
            "[]",
            "invoke-1",
            "invoke_agent_roller",
            nested.Id,
            "child-session",
            "root-run",
            "child-run",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-parent-child",
            "root-run"));
        var sessions = new ThreadCommandSessionStore(
            new GatewaySession(
                "child-session",
                "agent",
                "child-agent",
                "user-1",
                "Child",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null));
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore,
            links);
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            workflowCommands: workflow,
            sessions: sessions,
            invocationLinks: links,
            continuationQueue: continuationQueue,
            runWaitStateStore: waitStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: nested.Id, approved: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("approved", approvalStore.GetById(nested.Id)?.Decision);
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(nested.Id, continuationQueue.Enqueued[0].RequestId);
        Assert.Equal("child-session", continuationQueue.Enqueued[0].SessionId);
        Assert.Equal("child-agent", continuationQueue.Enqueued[0].TargetId);
    }

    [Fact]
    public async Task NestedPackedLeftoverLastProjectsInterruptResolvedToParentSession()
    {
        const string headId = "apr-pack-head";
        const string leftoverId = "apr-pack-leftover";
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            headId,
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-pack-head",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run",
            CallerRunId: "root-run",
            CallerToolCallId: "invoke-1"));
        GatewayToolApprovalRequest head = CreateRequest(headId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-head",
            ToolName = "invoke_agent_roller",
            ToolType = "agent",
        };
        GatewayToolApprovalRequest leftover = CreateRequest(leftoverId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-leftover",
            ToolName = "kautuka_web_fetch",
            ToolType = "mcp",
        };
        var approvalStore = new ThreadCommandApprovalStore(head, leftover);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                10,
                NaradEventTypes.RunStarted,
                new { trigger = "agent_tool" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 1),
            RuntimeEvent(
                11,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child-pack",
            "child-run",
            "child-run",
            "agent",
            "child-agent",
            "child-session",
            "child-agent",
            "user-1",
            RunWaitReasons.Approval,
            headId,
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, [headId, leftoverId]),
            "[]",
            "call-head",
            "invoke_agent_roller",
            headId,
            null,
            "child-run",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-child-pack",
            "child-run"));
        waitStore.Seed(new RunWaitState(
            "wait-parent-child",
            "root-continuation",
            "root-run",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Child,
            headId,
            "{}",
            "{}",
            "[]",
            "invoke-1",
            "invoke_agent_roller",
            headId,
            "child-session",
            "root-run",
            "child-run",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-parent-child",
            "root-run"));
        var sessions = new ThreadCommandSessionStore(
            new GatewaySession(
                "child-session",
                "agent",
                "child-agent",
                "user-1",
                "Child",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null));
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore,
            links);
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            workflowCommands: workflow,
            sessions: sessions,
            invocationLinks: links,
            continuationQueue: continuationQueue,
            runWaitStateStore: waitStore);

        ThreadCommandResult first = await service.ResolveInterruptAsync(
            Command(requestId: headId, approved: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(ThreadCommandStatus.Accepted, first.Status);
        Assert.Equal("approved", approvalStore.GetById(headId)?.Decision);
        Assert.Empty(approvalStore.ResolutionEvents);
        Assert.Empty(continuationQueue.Enqueued);

        ThreadCommandResult last = await service.ResolveInterruptAsync(
            Command(requestId: leftoverId, approved: false),
            TestContext.Current.CancellationToken);
        Assert.Equal(ThreadCommandStatus.Accepted, last.Status);
        Assert.Equal("denied", approvalStore.GetById(leftoverId)?.Decision);

        ApprovalResolutionEventWork resolved = Assert.Single(approvalStore.ResolutionEvents);
        Assert.Equal($"approval-resolution:{headId}", resolved.EventId);
        Assert.Equal(headId, resolved.RequestId);
        Assert.Equal("child-session", resolved.Context.SessionId);
        Assert.Equal("session-1", resolved.Context.ParentSessionId);
        Assert.Equal(headId, resolved.Payload.GetProperty("interruptId").GetString());
        Assert.Equal("deny", resolved.Payload.GetProperty("decision").GetString());
        Assert.Equal(2, resolved.Payload.GetProperty("members").GetArrayLength());
        Assert.Equal("approve", resolved.Payload.GetProperty("members")[0].GetProperty("decision").GetString());
        Assert.Equal("deny", resolved.Payload.GetProperty("members")[1].GetProperty("decision").GetString());
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(leftoverId, continuationQueue.Enqueued[0].RequestId);
        Assert.Equal("child-session", continuationQueue.Enqueued[0].SessionId);
    }

    [Fact]
    public async Task PackedStopRunClosesRemainingMembersAndPublishesInterruptResolved()
    {
        const string headId = "apr-stop-head";
        const string leftoverId = "apr-stop-leftover";
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            headId,
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-stop-head",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run",
            CallerRunId: "root-run",
            CallerToolCallId: "invoke-1"));
        GatewayToolApprovalRequest head = CreateRequest(headId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-head",
            ToolName = "invoke_agent_roller",
            ToolType = "agent",
        };
        GatewayToolApprovalRequest leftover = CreateRequest(leftoverId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-leftover",
            ToolName = "kautuka_web_fetch",
            ToolType = "mcp",
        };
        var approvalStore = new ThreadCommandApprovalStore(head, leftover);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                10,
                NaradEventTypes.RunStarted,
                new { trigger = "agent_tool" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 1),
            RuntimeEvent(
                11,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child-pack",
            "child-run",
            "child-run",
            "agent",
            "child-agent",
            "child-session",
            "child-agent",
            "user-1",
            RunWaitReasons.Approval,
            headId,
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, [headId, leftoverId]),
            "[]",
            "call-head",
            "invoke_agent_roller",
            headId,
            null,
            "child-run",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-child-pack",
            "child-run"));
        var sessions = new ThreadCommandSessionStore(
            new GatewaySession(
                "child-session",
                "agent",
                "child-agent",
                "user-1",
                "Child",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null));
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore,
            links);
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            workflowCommands: workflow,
            sessions: sessions,
            invocationLinks: links,
            continuationQueue: continuationQueue,
            runWaitStateStore: waitStore);

        ThreadCommandResult stopped = await service.ResolveInterruptAsync(
            Command(requestId: headId, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(ThreadCommandStatus.Accepted, stopped.Status);
        Assert.Equal("stopped", approvalStore.GetById(headId)?.Decision);
        Assert.Equal("stopped", approvalStore.GetById(leftoverId)?.Decision);

        ApprovalResolutionEventWork resolved = Assert.Single(approvalStore.ResolutionEvents);
        Assert.Equal($"approval-resolution:{headId}", resolved.EventId);
        Assert.Equal("stop_run", resolved.Payload.GetProperty("decision").GetString());
        Assert.Equal(2, resolved.Payload.GetProperty("members").GetArrayLength());
        Assert.Equal("stop_run", resolved.Payload.GetProperty("members")[0].GetProperty("decision").GetString());
        Assert.Equal("stop_run", resolved.Payload.GetProperty("members")[1].GetProperty("decision").GetString());
        Assert.True(resolved.StopRun);
    }

    [Fact]
    public async Task NestedPackedStopRunPublishesRootStopRequested()
    {
        const string headId = "apr-nested-stop-head";
        const string leftoverId = "apr-nested-stop-leftover";
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            headId,
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-nested-stop-head",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run",
            CallerRunId: "root-run",
            CallerToolCallId: "invoke-1"));
        GatewayToolApprovalRequest head = CreateRequest(headId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-head",
            ToolName = "invoke_agent_roller",
            ToolType = "agent",
        };
        GatewayToolApprovalRequest leftover = CreateRequest(leftoverId) with
        {
            SessionId = "child-session",
            RunId = "child-run",
            AgentId = "child-agent",
            OuterSessionId = "session-1",
            OuterAgentId = "agent-1",
            ToolCallId = "call-leftover",
            ToolName = "kautuka_web_fetch",
            ToolType = "mcp",
        };
        var approvalStore = new ThreadCommandApprovalStore(head, leftover);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, runId: "root-run", rootRunId: "root-run"),
            RuntimeEvent(
                10,
                NaradEventTypes.RunStarted,
                new { trigger = "agent_tool" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 1),
            RuntimeEvent(
                11,
                NaradEventTypes.RunPaused,
                new { reason = "tool_approval" },
                runId: "child-run",
                rootRunId: "child-run",
                sessionId: "child-session",
                rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-nested-stop",
            "child-run",
            "child-run",
            "agent",
            "child-agent",
            "child-session",
            "child-agent",
            "user-1",
            RunWaitReasons.Approval,
            headId,
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, [headId, leftoverId]),
            "[]",
            "call-head",
            "invoke_agent_roller",
            headId,
            null,
            "child-run",
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-nested-stop",
            "child-run"));
        var stopSignals = new RunStopRegistry();
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignals.IsStopped("root-run")),
            stopSignals);
        var sessions = new ThreadCommandSessionStore(
            new GatewaySession(
                "child-session",
                "agent",
                "child-agent",
                "user-1",
                "Child",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null));
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            stopCommands: stopCommands,
            sessions: sessions,
            invocationLinks: links,
            continuationQueue: continuationQueue,
            runWaitStateStore: waitStore);

        ThreadCommandResult stopped = await service.ResolveInterruptAsync(
            Command(requestId: headId, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, stopped.Status);
        Assert.Contains(
            agentStore.ReadSessionHistory("agent-1", "session-1"),
            static item => item.EventType == NaradEventTypes.RunStopRequested
                && string.Equals(item.RunId, "root-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrowserPackedStopRunEnqueuesStopContinuation()
    {
        const string headId = "apr-browser-stop-head";
        const string leftoverId = "apr-browser-stop-leftover";
        GatewayToolApprovalRequest head = CreateRequest(headId) with
        {
            ToolCallId = "call-head",
            ToolName = "kautuka_web_fetch",
            ToolType = "mcp",
        };
        GatewayToolApprovalRequest leftover = CreateRequest(leftoverId) with
        {
            ToolCallId = "call-leftover",
            ToolName = "run_skill_script",
            ToolType = "mcp",
        };
        var approvalStore = new ThreadCommandApprovalStore(head, leftover);
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }),
            RuntimeEvent(2, NaradEventTypes.RunPaused, new { reason = "tool_approval" }, rootRuntimeSequence: 2));
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-browser-pack",
            "run-1",
            "run-1",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            headId,
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, [headId, leftoverId]),
            "[]",
            "call-head",
            "kautuka_web_fetch",
            headId,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-browser-pack",
            "run-1"));
        ThreadCommandService service = CreateService(
            approvalStore,
            agentStore: agentStore,
            continuationQueue: continuationQueue,
            runWaitStateStore: waitStore);

        ThreadCommandResult stopped = await service.ResolveInterruptAsync(
            Command(requestId: headId, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, stopped.Status);
        ApprovalContinuationWork work = Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(headId, work.RequestId);
        Assert.True(work.StopRun);
    }

    [Fact]
    public async Task SessionlessAutopilotResolveDoesNotRequireThreadOwner()
    {
        GatewayToolApprovalRequest request = CreateRequest("apr-prerun") with
        {
            SessionId = null,
            OuterSessionId = null,
            ToolType = "autopilot_prerun",
            RunId = "srun-1",
        };
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("stopped", approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task SessionlessExpiredResolveReturnsExpiredNotMissingOwner()
    {
        GatewayToolApprovalRequest request = CreateRequest("apr-prerun") with
        {
            SessionId = null,
            OuterSessionId = null,
            ToolType = "autopilot_prerun",
            RunId = "srun-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        };
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult result = await service.ResolveInterruptAsync(
            Command(requestId: request.Id, approved: false, stopRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Expired, result.Status);
        Assert.Equal("expired", result.Decision);
        Assert.Equal("expired", approvalStore.GetById(request.Id)?.Decision);
    }

    private static ResolveThreadInterruptCommand Command(
        string requestId = "request-1",
        bool approved = true,
        bool stopRun = false,
        long? expectedRevision = null)
        => new(
            requestId,
            stopRun
                ? ApprovalActionCatalog.StopRun
                : approved
                    ? ApprovalActionCatalog.Approve
                    : ApprovalActionCatalog.Deny,
            null,
            "user-1",
            "Admin User",
            expectedRevision);
}
