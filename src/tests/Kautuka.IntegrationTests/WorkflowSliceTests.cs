using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class WorkflowSliceTests
{
    [Theory]
    [InlineData(900, "ask", "auto")]
    [InlineData(100, "auto", "ask")]
    public async Task DiamondWorkflowRunsOnlyTheSelectedBranchAndJoinsOnce(int amount, string selectedBranch, string unselectedBranch)
    {
        WorkflowDiamondRunResult result = await RunDiamondWorkflowAsync(amount, overlappingRules: false);

        Assert.Equal(WorkflowInstanceStatuses.Finished, result.Instance.Status);
        Assert.Equal("filed", result.Instance.Envelope["fileResult"]?.GetValue<string>());
        Assert.Equal(3, result.StepStartedEvents.Length);
        Assert.Equal(3, result.Chat.AgentRequests.Count);
        Assert.Equal(1, result.StepStartedEvents.Count(item => item.NodeId == "review"));
        Assert.Equal(1, result.StepStartedEvents.Count(item => item.NodeId == selectedBranch));
        Assert.Equal(1, result.StepStartedEvents.Count(item => item.NodeId == "file"));
        Assert.DoesNotContain(result.StepStartedEvents, item => item.NodeId == unselectedBranch);
        Assert.DoesNotContain(result.Chat.AgentRequests, agentId => agentId == unselectedBranch);

        Assert.Equal(
            new[] { ("review", selectedBranch), (selectedBranch, "file") },
            result.EdgeEvents.Select(item => (item.From, item.To)).ToArray());
        Assert.Equal(1, result.EdgeEvents.Count(item => item.To == "file"));
        Assert.All(result.Chat.AgentRequests.GroupBy(agentId => agentId), group => Assert.Single(group));
    }

    [Fact]
    public async Task DiamondWorkflowEvaluatesOverlappingRulesInDefinitionOrder()
    {
        WorkflowDiamondRunResult result = await RunDiamondWorkflowAsync(900, overlappingRules: true);

        Assert.Equal(WorkflowInstanceStatuses.Finished, result.Instance.Status);
        Assert.Equal("filed", result.Instance.Envelope["fileResult"]?.GetValue<string>());
        Assert.Equal(new[] { "review", "ask", "file" }, result.StepStartedEvents.Select(item => item.NodeId).ToArray());
        Assert.DoesNotContain(result.StepStartedEvents, item => item.NodeId == "auto");
        Assert.Equal(new[] { ("review", "ask"), ("ask", "file") },
            result.EdgeEvents.Select(item => (item.From, item.To)).ToArray());
        Assert.Equal(3, result.Chat.AgentRequests.Count);
        Assert.All(result.Chat.AgentRequests.GroupBy(agentId => agentId), group => Assert.Single(group));
    }

    [Fact]
    public async Task ResumeWaitingRootEnqueuesContinuationWithoutPublishingRunResumed()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child",
            WorkflowSliceFixture.ChildRunId,
            WorkflowSliceFixture.ChildRunId,
            "agent",
            "child-agent",
            WorkflowSliceFixture.ThreadId,
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            WorkflowSliceFixture.ChildApprovalId,
            "{}",
            "{}",
            "[]",
            WorkflowSliceFixture.ChildToolCallId,
            "run_shell",
            WorkflowSliceFixture.ChildApprovalId,
            "child-session",
            WorkflowSliceFixture.RootRunId,
            WorkflowSliceFixture.ChildRunId,
            null,
            null,
            "2026-08-04T00:00:00.0000000+00:00",
            null,
            "event-11",
            WorkflowSliceFixture.RootRunId));
        var approvalStore = new ThreadCommandApprovalStore(CreateRequest("approval-child-shell") with
        {
            Id = WorkflowSliceFixture.ChildApprovalId,
            ToolCallId = WorkflowSliceFixture.ChildToolCallId,
            Decision = "approved",
        });
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore);
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.ResumeWaitingRootAsync(
            new ResumeWaitingRootCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                WorkflowSliceFixture.RootRunId,
                "user-1",
                ExpectedRevision: 12),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("continuation_enqueued", result.Decision);
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(WorkflowSliceFixture.ChildApprovalId, continuationQueue.Enqueued[0].RequestId);
        Assert.DoesNotContain(
            agentStore.ReadSessionHistory("agent-1", WorkflowSliceFixture.ThreadId),
            static item => item.EventType == NaradEventTypes.RunResumed);
    }

    [Fact]
    public async Task ResumeWaitingRootEnqueuesResolvedPackedSiblingNotPackHead()
    {
        const string packHead = "apr-roller";
        const string sibling = "apr-fetch";
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-packed",
            WorkflowSliceFixture.RootRunId,
            WorkflowSliceFixture.RootRunId,
            "agent",
            "agent-1",
            WorkflowSliceFixture.ThreadId,
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            packHead,
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, [packHead, sibling]),
            "[]",
            "call-roller",
            "invoke_agent_roller",
            packHead,
            null,
            null,
            null,
            null,
            null,
            "2026-08-04T00:00:12.0000000+00:00",
            null,
            "event-packed",
            WorkflowSliceFixture.RootRunId));
        var approvalStore = new ThreadCommandApprovalStore(
            CreateRequest(packHead) with
            {
                Id = packHead,
                ToolCallId = "call-roller",
                ToolName = "invoke_agent_roller",
                Decision = "denied",
            },
            CreateRequest(sibling) with
            {
                Id = sibling,
                ToolCallId = "call-fetch",
                ToolName = "kautuka_web_fetch",
                Decision = "denied",
            });
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore);
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.ResumeWaitingRootAsync(
            new ResumeWaitingRootCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                WorkflowSliceFixture.RootRunId,
                "user-1",
                ExpectedRevision: 12,
                ResolvedApprovalRequestId: sibling),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Equal("continuation_enqueued", result.Decision);
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(sibling, continuationQueue.Enqueued[0].RequestId);
        Assert.False(continuationQueue.Enqueued[0].Approved);
    }

    [Fact]
    public async Task ResumeWaitingRootRejectsPendingApproval()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child",
            WorkflowSliceFixture.ChildRunId,
            WorkflowSliceFixture.ChildRunId,
            "agent",
            "child-agent",
            WorkflowSliceFixture.ThreadId,
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            WorkflowSliceFixture.ChildApprovalId,
            "{}",
            "{}",
            "[]",
            WorkflowSliceFixture.ChildToolCallId,
            "run_shell",
            WorkflowSliceFixture.ChildApprovalId,
            "child-session",
            WorkflowSliceFixture.RootRunId,
            WorkflowSliceFixture.ChildRunId,
            null,
            null,
            "2026-08-04T00:00:00.0000000+00:00",
            null,
            "event-11",
            WorkflowSliceFixture.RootRunId));
        var approvalStore = new ThreadCommandApprovalStore(CreateRequest("approval-child-shell") with
        {
            Id = WorkflowSliceFixture.ChildApprovalId,
            ToolCallId = WorkflowSliceFixture.ChildToolCallId,
        });
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService(), approvalStore, waitStore: waitStore);
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.ResumeWaitingRootAsync(
            new ResumeWaitingRootCommand("agent-1", WorkflowSliceFixture.ThreadId, WorkflowSliceFixture.RootRunId, "user-1", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
        Assert.Contains("approval", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelGraphNodeRejectsRunningToolCancel()
    {
        var agentStore = new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }),
            RuntimeEvent(2, NaradEventTypes.ToolCallStarted,
                new GatewayChatToolCall("tool-1", "run_shell", "{}", "skill", ParentMessageId: "assistant-1"),
                toolCallId: "tool-1"));
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.CancelGraphNodeAsync(
            new CancelGraphNodeCommand("agent-1", "session-1", "node-tool-1", "user-1", null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.UnsupportedCapability, result.Status);
        Assert.Contains("tool", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelGraphNodeAcceptsNestedChildRunStopWithoutStoppingRoot()
    {
        IReadOnlyList<NaradPersistedEvent> fixture = WorkflowSliceFixture.BuildThroughChildPause();
        var agentStore = new ThreadCommandAgentStore([.. fixture]);
        var stopSignals = new RunStopRegistry();
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignals.IsStopped(WorkflowSliceFixture.ChildRunId)),
            stopSignals);
        var workflow = CreateWorkflowService(agentStore, stopCommands);
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.CancelGraphNodeAsync(
            new CancelGraphNodeCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                $"node-run-{WorkflowSliceFixture.ChildRunId}",
                "user-1",
                ExpectedRevision: 12),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.True(stopSignals.IsStopped(WorkflowSliceFixture.ChildRunId));
        Assert.False(stopSignals.IsStopped(WorkflowSliceFixture.RootRunId));
    }

    [Fact]
    public async Task CancelGraphNodeRejectsTerminalPhaseNode()
    {
        IReadOnlyList<NaradPersistedEvent> fixture = WorkflowSliceFixture.BuildThroughTerminalAggregation();
        var agentStore = new ThreadCommandAgentStore([.. fixture]);
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.CancelGraphNodeAsync(
            new CancelGraphNodeCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "node-root",
                "user-1",
                ExpectedRevision: 19),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.UnsupportedCapability, result.Status);
    }

    [Fact]
    public async Task CancelGraphNodeRejectsNestedChildWhenCanonicalRootIsTerminal()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        agentStore.AppendEvent(RuntimeEvent(
            13,
            NaradEventTypes.RunStopRequested,
            new { reason = "stopped" },
            runId: WorkflowSliceFixture.RootRunId,
            rootRunId: WorkflowSliceFixture.RootRunId));
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.CancelGraphNodeAsync(
            new CancelGraphNodeCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                $"node-run-{WorkflowSliceFixture.ChildRunId}",
                "user-1",
                ExpectedRevision: 13),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
        Assert.Contains("terminal", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryTargetRejectsUnsupportedPhaseNode()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughPartialJoin()]);
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "phase",
                "plan",
                "user-1",
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.UnsupportedCapability, result.Status);
    }

    [Fact]
    public async Task RetryTargetRejectsChildRunRetry()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughFailedChildInvocation()]);
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "run",
                WorkflowSliceFixture.ChildRunId,
                "user-1",
                null),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.UnsupportedCapability, result.Status);
    }

    [Fact]
    public async Task RetryTargetEnqueuesApprovalContinuationAfterReleaseForRetry()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child",
            WorkflowSliceFixture.ChildRunId,
            WorkflowSliceFixture.ChildRunId,
            "agent",
            "child-agent",
            WorkflowSliceFixture.ThreadId,
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            WorkflowSliceFixture.ChildApprovalId,
            "{}",
            "{}",
            "[]",
            WorkflowSliceFixture.ChildToolCallId,
            "run_shell",
            WorkflowSliceFixture.ChildApprovalId,
            "child-session",
            WorkflowSliceFixture.RootRunId,
            WorkflowSliceFixture.ChildRunId,
            null,
            null,
            "2026-08-04T00:00:00.0000000+00:00",
            null,
            "event-11",
            WorkflowSliceFixture.RootRunId));
        var approvalStore = new ThreadCommandApprovalStore(CreateRequest("approval-child-shell") with
        {
            Id = WorkflowSliceFixture.ChildApprovalId,
            ToolCallId = WorkflowSliceFixture.ChildToolCallId,
            Decision = "approved",
        });
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore);
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "approval",
                WorkflowSliceFixture.ChildApprovalId,
                "user-1",
                ExpectedRevision: 12),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, result.Status);
        Assert.Single(continuationQueue.Enqueued);
        Assert.Equal(WorkflowSliceFixture.ChildApprovalId, continuationQueue.Enqueued[0].RequestId);
    }

    [Fact]
    public async Task RetryTargetRepeatedEnqueueReturnsAlreadyAppliedWithoutDoubleSchedule()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var continuationQueue = new RecordingContinuationQueue();
        var waitStore = new NoOpRunWaitStateStore();
        waitStore.Seed(new RunWaitState(
            "wait-child",
            WorkflowSliceFixture.ChildRunId,
            WorkflowSliceFixture.ChildRunId,
            "agent",
            "child-agent",
            WorkflowSliceFixture.ThreadId,
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            WorkflowSliceFixture.ChildApprovalId,
            "{}",
            "{}",
            "[]",
            WorkflowSliceFixture.ChildToolCallId,
            "run_shell",
            WorkflowSliceFixture.ChildApprovalId,
            "child-session",
            WorkflowSliceFixture.RootRunId,
            WorkflowSliceFixture.ChildRunId,
            null,
            null,
            "2026-08-04T00:00:00.0000000+00:00",
            null,
            "event-11",
            WorkflowSliceFixture.RootRunId));
        var approvalStore = new ThreadCommandApprovalStore(CreateRequest("approval-child-shell") with
        {
            Id = WorkflowSliceFixture.ChildApprovalId,
            ToolCallId = WorkflowSliceFixture.ChildToolCallId,
            Decision = "approved",
        });
        var workflow = CreateWorkflowService(
            agentStore,
            new NoOpRunStopCommandService(),
            approvalStore,
            continuationQueue,
            waitStore);
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult first = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "approval",
                WorkflowSliceFixture.ChildApprovalId,
                "user-1",
                ExpectedRevision: 12),
            TestContext.Current.CancellationToken);
        ThreadCommandResult second = await service.RetryTargetAsync(
            new RetryExecutionTargetCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                "approval",
                WorkflowSliceFixture.ChildApprovalId,
                "user-1",
                ExpectedRevision: 12),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, first.Status);
        Assert.Equal(ThreadCommandStatus.AlreadyApplied, second.Status);
        Assert.Single(continuationQueue.Enqueued);
    }

    [Fact]
    public async Task WorkflowCommandRejectsStaleExpectedRevision()
    {
        var agentStore = new ThreadCommandAgentStore([.. WorkflowSliceFixture.BuildThroughChildPause()]);
        var workflow = CreateWorkflowService(agentStore, new NoOpRunStopCommandService());
        ThreadCommandService service = CreateService(new ThreadCommandApprovalStore(), agentStore: agentStore, workflowCommands: workflow);

        ThreadCommandResult result = await service.CancelGraphNodeAsync(
            new CancelGraphNodeCommand(
                "agent-1",
                WorkflowSliceFixture.ThreadId,
                $"node-run-{WorkflowSliceFixture.RootRunId}",
                "user-1",
                ExpectedRevision: 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Conflict, result.Status);
    }

    private static async Task<WorkflowDiamondRunResult> RunDiamondWorkflowAsync(int amount, bool overlappingRules)
    {
        using var database = new TestGatewayDatabase();
        var workflowStore = new GatewayWorkflowStore(database.Database);
        string now = DateTimeOffset.UtcNow.ToString("O");
        GatewayWorkflowSwitchCase[] cases = overlappingRules
            ? [new("/amount", "gt", "100", "ask"), new("/amount", "gt", "500", "auto")]
            : [new("/amount", "gt", "500", "ask")];
        var nodes = new GatewayWorkflowNode[]
        {
            new("review", "review", "Review the amount", "json", null, [], "none", false, false, 0,
                cases, "auto"),
            new("ask", "ask", "Ask a manager", "json", null, [], "none", false, false, 0, null, "file"),
            new("auto", "auto", "Approve automatically", "json", null, [], "none", false, false, 0, null, "file"),
            new("file", "file", "File the result", "json", null, [], "none", false, false, 0),
        };
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["amount"] = new JsonObject { ["type"] = "number" },
                ["fileResult"] = new JsonObject { ["type"] = "string" },
            },
        };
        var definition = new GatewayWorkflowDefinition(
            $"workflow-diamond-{Guid.NewGuid():N}", "workflow-test-owner", "Review and file", false, 1,
            schema.DeepClone().AsObject(), schema.DeepClone().AsObject(), schema.DeepClone().AsObject(),
            nodes, now, now);
        workflowStore.CreateDefinition(definition);
        string instanceId = $"wfi-{Guid.NewGuid():N}";
        GatewayWorkflowStepAttempt[] attempts = [.. nodes.Select(node => new GatewayWorkflowStepAttempt(
            $"attempt-{node.NodeId}-{Guid.NewGuid():N}", instanceId, node.NodeId, 1,
            $"run-{node.NodeId}-{Guid.NewGuid():N}", null, WorkflowStepAttemptStatuses.Pending, null, null, null, now))];
        var input = new JsonObject { ["amount"] = amount };
        var instance = new GatewayWorkflowInstance(
            instanceId, definition.Id, definition.Revision, definition.OwnerUserId, null,
            WorkflowInstanceStatuses.Running, false, "review", input,
            WorkflowEnvelopeCanonical.Sha256Hex(input), 0, definition, attempts,
            null, null, now, now, now, null);
        workflowStore.CreateInstance(instance);

        var chat = new DiamondChatGateway();
        var runtimeHub = new RuntimeEventStreamHub();
        var execution = new WorkflowExecutionService(
            workflowStore,
            chat,
            new WorkflowNaradPublisher(workflowStore, new WorkflowNaradStreamHub(), runtimeHub),
            new EmptyRuntimeEventHistoryReader(),
            runtimeHub,
            new GatewayApprovalStore(database.Database),
            new DiamondMediator(),
            NullLogger<WorkflowExecutionService>.Instance);
        execution.Enqueue(instanceId);
        await WaitForWorkflowTerminalAsync(workflowStore, instanceId);
        // The service publishes terminal status just before its background task unwinds and
        // releases the pooled SQLite connection; let that finalizer complete before fixture disposal.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        GatewayWorkflowInstance completed = workflowStore.GetInstance(instanceId)
            ?? throw new Xunit.Sdk.XunitException("The workflow instance disappeared while its steps ran.");
        NaradPersistedEvent[] persisted = [.. workflowStore.ListNaradEvents(instanceId, 0, 100)];
        WorkflowStartedEvent[] started = [.. persisted
            .Where(item => item.EventType == NaradEventTypes.WorkflowStepStarted)
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.EventJson);
                return new WorkflowStartedEvent(document.RootElement.GetProperty("nodeId").GetString()!);
            })];
        WorkflowTakenEdge[] edges = [.. persisted
            .Where(item => item.EventType == NaradEventTypes.WorkflowEdgeTaken)
            .Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.EventJson);
                return new WorkflowTakenEdge(
                    document.RootElement.GetProperty("from").GetString()!,
                    document.RootElement.GetProperty("to").GetString()!);
            })];
        return new WorkflowDiamondRunResult(completed, chat, started, edges);
    }

    private static async Task WaitForWorkflowTerminalAsync(GatewayWorkflowStore store, string instanceId)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (store.GetInstance(instanceId)?.Status is WorkflowInstanceStatuses.Finished or WorkflowInstanceStatuses.Error)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed record WorkflowStartedEvent(string NodeId);
    private sealed record WorkflowTakenEdge(string From, string To);
    private sealed record WorkflowDiamondRunResult(
        GatewayWorkflowInstance Instance,
        DiamondChatGateway Chat,
        WorkflowStartedEvent[] StepStartedEvents,
        WorkflowTakenEdge[] EdgeEvents);

    private sealed class EmptyRuntimeEventHistoryReader : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];
        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class DiamondChatGateway : IChatGatewayService
    {
        private int requestCount;
        public List<string> AgentRequests { get; } = [];

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string targetType = body.GetProperty("target_type").GetString() ?? "agent";
            string targetId = body.GetProperty("target_id").GetString() ?? "unknown";
            string runId = body.GetProperty("run_id").GetString() ?? "run-test";
            AgentRequests.Add(targetId);
            string sessionId = $"diamond-session-{Interlocked.Increment(ref requestCount)}";
            yield return NaradTestEventFactory.FromChatEvent(
                new GatewayChatSessionReady(sessionId), targetId, targetId, runId);
            string response = string.Equals(targetId, "file", StringComparison.Ordinal)
                ? "{\"fileResult\":\"filed\"}"
                : "{}";
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatStreamDone(new GatewayChatResult(
                targetType, targetId, sessionId, response, false, new GatewayMetrics(0, 0, 0))),
                targetId, targetId, runId);
            await Task.CompletedTask;
            _ = requestContext;
            _ = cancellationToken;
        }

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null) { }
        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null) { }
        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null) { }
    }

    private sealed class DiamondMediator : IMediator
    {
        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Publish(object notification, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => ValueTask.CompletedTask;
    }
}
