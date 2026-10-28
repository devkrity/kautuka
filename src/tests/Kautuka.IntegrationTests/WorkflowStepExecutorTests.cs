using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class WorkflowStepExecutorTests
{
    [Fact]
    public async Task PassesInstanceFilesToTheFirstStepAgentRequest()
    {
        var chat = new ScriptedChatGateway(_ => Events(Event(NaradEventTypes.RunFinished, 1, "done")));
        (WorkflowStepExecutor executor, _, InMemoryWorkflowStore store) = CreateExecutor(chat, new RuntimeEventStreamHub(), new EmptyHistory());
        store.Artifacts = [new WorkflowArtifact("artifact-1", store.Instance.Id, "abc", "report.pdf", "attachments/report.pdf", DateTimeOffset.UtcNow.ToString("O"))];

        await executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken);

        JsonElement files = Assert.Single(chat.RequestBodies).GetProperty("workspaceFiles");
        Assert.Equal("attachments/report.pdf", Assert.Single(files.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task IncludesTheOwnersLastReviewNoteOnTheNextAttempt()
    {
        GatewayWorkflowInstance initial = CreateInstance();
        GatewayWorkflowStepAttempt first = initial.Steps[0] with
        {
            Status = WorkflowStepAttemptStatuses.Finished,
            EndedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        GatewayWorkflowStepAttempt failedAfterRequest = initial.Steps[0] with
        {
            Id = "attempt-2",
            Attempt = 2,
            RunId = "run-2",
            Status = WorkflowStepAttemptStatuses.Failed,
        };
        GatewayWorkflowStepAttempt retry = initial.Steps[0] with
        {
            Id = "attempt-3",
            Attempt = 3,
            RunId = "run-3",
            Status = WorkflowStepAttemptStatuses.Pending,
        };
        var store = new InMemoryWorkflowStore(initial with { Steps = [first, failedAfterRequest, retry] });
        string toolCallId = $"{initial.Id}:{retry.NodeId}:{first.Attempt}";
        var approvalStore = new ThreadCommandApprovalStore(new GatewayToolApprovalRequest(
            "review-1",
            null,
            first.RunId,
            null,
            initial.Snapshot.Nodes[0].AgentId,
            initial.OwnerUserId,
            toolCallId,
            "workflow_review",
            "workflow_review",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            "denied",
            initial.OwnerUserId,
            DateTimeOffset.UtcNow.ToString("O"),
            "Please include the invoice date.",
            null,
            Kind: "workflow_review",
            WorkflowId: initial.WorkflowId,
            InstanceId: initial.Id,
            NodeId: retry.NodeId));
        var chat = new ScriptedChatGateway(_ => Events(Event(NaradEventTypes.RunFinished, 1, "done", retry.RunId)));
        var executor = new WorkflowStepExecutor(
            initial.Id,
            initial.Snapshot.Nodes[0],
            retry,
            store,
            approvalStore,
            chat,
            new EmptyHistory(),
            new RuntimeEventStreamHub(),
            new RecordingPublisher());

        await executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken);

        string prompt = Assert.Single(chat.RequestBodies).GetProperty("message").GetString()!;
        Assert.Contains("The owner asked for changes: Please include the invoice date.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRequestChangesMarkerIsNotShownAsAReviewNote()
    {
        GatewayWorkflowInstance initial = CreateInstance();
        GatewayWorkflowStepAttempt first = initial.Steps[0] with
        {
            Status = WorkflowStepAttemptStatuses.Finished,
        };
        GatewayWorkflowStepAttempt retry = initial.Steps[0] with { Attempt = 2 };
        GatewayWorkflowInstance instance = initial with { Steps = [first, retry] };
        string toolCallId = $"{instance.Id}:{retry.NodeId}:{first.Attempt}";
        var approvalStore = new ThreadCommandApprovalStore(new GatewayToolApprovalRequest(
            "review-1",
            null,
            first.RunId,
            null,
            instance.Snapshot.Nodes[0].AgentId,
            instance.OwnerUserId,
            toolCallId,
            "workflow_review",
            "workflow_review",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            "denied",
            instance.OwnerUserId,
            DateTimeOffset.UtcNow.ToString("O"),
            ApprovalActionCatalog.RequestChanges,
            null,
            Kind: "workflow_review",
            WorkflowId: instance.WorkflowId,
            InstanceId: instance.Id,
            NodeId: retry.NodeId));

        Assert.Null(WorkflowReviewNotes.ForAttempt(instance, retry, approvalStore));
    }

    [Fact]
    public void MissingReviewHasNoNoteOnTheRetryAttempt()
    {
        GatewayWorkflowInstance initial = CreateInstance();
        GatewayWorkflowStepAttempt first = initial.Steps[0] with
        {
            Status = WorkflowStepAttemptStatuses.Finished,
        };
        GatewayWorkflowStepAttempt retry = initial.Steps[0] with { Attempt = 2 };
        GatewayWorkflowInstance instance = initial with { Steps = [first, retry] };

        Assert.Null(WorkflowReviewNotes.ForAttempt(instance, retry, new ThreadCommandApprovalStore()));
    }

    [Fact]
    public async Task KeepsConsumingTheAgentStreamAcrossInterruptUntilRunFinished()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new RuntimeEventStreamHub();
        async IAsyncEnumerable<NaradPersistedEvent> Produce(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Event(NaradEventTypes.InterruptRequested, 1);
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return Event(NaradEventTypes.RunFinished, 2, "final answer");
        }

        var chat = new ScriptedChatGateway(Produce);
        (WorkflowStepExecutor executor, RecordingPublisher publisher, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await chat.FirstYielded.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, hub.SessionSubscriberCount);

        gate.SetResult();
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
        Assert.Contains(NaradEventTypes.WorkflowStepFinished, publisher.Types);
        Assert.DoesNotContain(NaradEventTypes.WorkflowStepFailed, publisher.Types);
    }

    [Fact]
    public async Task WaitsOnTheAgentSessionHubWhenTheStreamEndsAtPause()
    {
        var hub = new RuntimeEventStreamHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, RecordingPublisher publisher, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunFinished, 3, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
        Assert.Contains(NaradEventTypes.WorkflowStepFinished, publisher.Types);
    }

    [Fact]
    public async Task UsesTheRealTerminalTypeInsteadOfAssumingRunFinished()
    {
        var hub = new RuntimeEventStreamHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, RecordingPublisher publisher, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunError, 3, "tool exploded"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Equal("tool exploded", error.Message);
        Assert.Contains(NaradEventTypes.WorkflowStepFailed, publisher.Types);
        Assert.DoesNotContain(NaradEventTypes.WorkflowStepFinished, publisher.Types);
    }

    [Fact]
    public async Task IgnoresProjectedChildTerminalsOnTheParentSessionHub()
    {
        var hub = new RuntimeEventStreamHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, RecordingPublisher publisher, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(ProjectedChildFinished());
        Assert.False(handle.IsCompleted);

        hub.Publish(Event(NaradEventTypes.RunFinished, 4, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("child output", envelope, StringComparison.Ordinal);
        Assert.Contains(NaradEventTypes.WorkflowStepFinished, publisher.Types);
    }

    [Fact]
    public async Task IgnoresADifferentRunTerminalAlreadyOnTheSession()
    {
        var hub = new RuntimeEventStreamHub();
        var history = new MutableHistory(
            Event(NaradEventTypes.RunFinished, 1, "child output", runId: "child-1"));
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 2),
            Event(NaradEventTypes.RunPaused, 3)));
        (WorkflowStepExecutor executor, _, _) = CreateExecutor(chat, hub, history, sessionId: "sess-1");

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunFinished, 4, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("child output", envelope, StringComparison.Ordinal);
        Assert.Equal(1, chat.StreamCalls);
    }

    [Fact]
    public async Task ResumesAPausedAttemptFromTheSessionHubWithoutStartingANewRun()
    {
        var hub = new RuntimeEventStreamHub();
        var history = new MutableHistory(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2));
        var chat = new ScriptedChatGateway(_ => Events(Event(NaradEventTypes.RunFinished, 9, "should not run")));
        (WorkflowStepExecutor executor, _, _) = CreateExecutor(chat, hub, history, sessionId: "sess-1");

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        Assert.Equal(0, chat.StreamCalls);
        hub.Publish(Event(NaradEventTypes.RunFinished, 3, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
        Assert.Equal(0, chat.StreamCalls);
    }

    [Fact]
    public async Task MapsRunCancelledToStepRunCancelled()
    {
        var hub = new RuntimeEventStreamHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, RecordingPublisher publisher, InMemoryWorkflowStore store) =
            CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunCancelled, 3, "stopped"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Equal("stopped", error.Message);
        Assert.Equal(WorkflowFailureCodes.StepRunCancelled, store.Instance.Steps[0].FailureCode);
        Assert.Contains(NaradEventTypes.WorkflowStepFailed, publisher.Types);
    }

    [Fact]
    public async Task WaitsThroughNestedPauseCyclesOnTheHub()
    {
        var hub = new RuntimeEventStreamHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, _, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await WaitForSessionSubscriberAsync(hub, TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunResumed, 3));
        hub.Publish(Event(NaradEventTypes.InterruptRequested, 4));
        hub.Publish(Event(NaradEventTypes.RunPaused, 5));
        Assert.False(handle.IsCompleted);
        hub.Publish(Event(NaradEventTypes.RunFinished, 6, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplaysHistoryWhenTheSessionHubDisconnectsASlowSubscriber()
    {
        var hub = new FlakyThenLiveHub();
        var chat = new ScriptedChatGateway(_ => Events(
            Event(NaradEventTypes.InterruptRequested, 1),
            Event(NaradEventTypes.RunPaused, 2)));
        (WorkflowStepExecutor executor, _, _) = CreateExecutor(chat, hub, new EmptyHistory());

        Task<string> handle = executor.HandleAsync("{}", null!, TestContext.Current.CancellationToken).AsTask();
        await hub.WaitForLiveSubscribeAsync(TestContext.Current.CancellationToken);
        hub.Publish(Event(NaradEventTypes.RunFinished, 3, "final answer"));
        string envelope = await handle.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Contains("final answer", envelope, StringComparison.Ordinal);
    }

    private static (WorkflowStepExecutor Executor, RecordingPublisher Publisher, InMemoryWorkflowStore Store) CreateExecutor(
        IChatGatewayService chat,
        IRuntimeEventStreamHub hub,
        IRuntimeEventHistoryReader history,
        string? sessionId = null)
    {
        var store = new InMemoryWorkflowStore(CreateInstance(sessionId));
        var publisher = new RecordingPublisher();
        var executor = new WorkflowStepExecutor(
            store.Instance.Id,
            store.Instance.Snapshot.Nodes[0],
            store.Instance.Steps[0],
            store,
            new ThreadCommandApprovalStore(),
            chat,
            history,
            hub,
            publisher);
        return (executor, publisher, store);
    }

    private static async Task WaitForSessionSubscriberAsync(RuntimeEventStreamHub hub, CancellationToken cancellationToken)
    {
        while (hub.SessionSubscriberCount == 0)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GatewayWorkflowInstance CreateInstance(string? sessionId = null)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode(
            "n1",
            "agent-1",
            "do",
            "text",
            null,
            [],
            "none",
            false,
            false,
            0);
        var definition = new GatewayWorkflowDefinition(
            "wf-1",
            "user-1",
            "t",
            false,
            1,
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [node],
            now,
            now);
        var attempt = new GatewayWorkflowStepAttempt(
            "wattempt-1",
            "wfi-1",
            "n1",
            1,
            "run-1",
            sessionId,
            WorkflowStepAttemptStatuses.Pending,
            null,
            null,
            null,
            now);
        return new GatewayWorkflowInstance(
            "wfi-1",
            "wf-1",
            1,
            "user-1",
            null,
            WorkflowInstanceStatuses.Running,
            false,
            "n1",
            new JsonObject { ["title"] = "run" },
            "hash",
            1,
            definition,
            [attempt],
            null,
            null,
            now,
            now,
            now,
            null);
    }

    private static NaradPersistedEvent Event(string eventType, long sequence, string? response = null, string runId = "run-1")
    {
        var body = new JsonObject();
        if (response is not null)
        {
            body["response"] = response;
        }

        return NaradTestEventFactory.FromBody(
            eventType,
            JsonSerializer.SerializeToElement(body),
            "sess-1",
            runId,
            runId,
            sequence,
            sequence);
    }

    private static NaradPersistedEvent ProjectedChildFinished()
        => Event(NaradEventTypes.RunFinished, 50, "child output") with
        {
            RootRunId = null,
            RunId = null,
            Sequence = null,
        };

    private static async IAsyncEnumerable<NaradPersistedEvent> Events(params NaradPersistedEvent[] events)
    {
        foreach (NaradPersistedEvent evt in events)
        {
            yield return evt;
        }

        await Task.CompletedTask;
    }

    private sealed class ScriptedChatGateway(
        Func<CancellationToken, IAsyncEnumerable<NaradPersistedEvent>> produce) : IChatGatewayService
    {
        public TaskCompletionSource FirstYielded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StreamCalls { get; private set; }
        public List<JsonElement> RequestBodies { get; } = [];

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestBodies.Add(body.Clone());
            _ = requestContext;
            StreamCalls++;
            bool signaled = false;
            await foreach (NaradPersistedEvent evt in produce(cancellationToken).WithCancellation(cancellationToken))
            {
                yield return evt;
                if (!signaled)
                {
                    signaled = true;
                    FirstYielded.TrySetResult();
                }
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
            _ = targetType;
            _ = targetId;
            _ = sessionId;
            _ = approvalResponse;
            _ = userId;
            _ = beforeWaitTerminal;
            await Task.CompletedTask;
            yield break;
        }

        public void PersistRun(
            string sessionId,
            string userMessage,
            GatewayChatResult result,
            IReadOnlyList<string>? userAssetIds = null,
            string? userId = null,
            string? runId = null,
            string? sourceChannel = null)
        {
        }

        public void RecordUserTurnStart(
            string sessionId,
            string userMessage,
            IReadOnlyList<string>? userAssetIds = null,
            string? userId = null,
            string? sourceChannel = null)
        {
        }

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
        {
        }
    }

    private sealed class RecordingPublisher : IWorkflowNaradPublisher
    {
        public List<string> Types { get; } = [];

        public NaradPersistedEvent Publish(GatewayWorkflowInstance instance, string eventType, JsonObject body)
        {
            Types.Add(eventType);
            return new(
                Guid.NewGuid().ToString("N"),
                NaradProtocolVersions.V1,
                eventType,
                instance.Id,
                Types.Count,
                NaradRecordClasses.Authority,
                body.ToJsonString(),
                DateTimeOffset.UtcNow.ToString("O"),
                WorkflowId: instance.Id,
                WorkflowSequence: Types.Count);
        }
    }

    private sealed class EmptyHistory : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class MutableHistory(params NaradPersistedEvent[] events) : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => events;

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => events;
    }

    private sealed class FlakyThenLiveHub : IRuntimeEventStreamHub
    {
        private readonly RuntimeEventStreamHub inner = new();
        private int sessionSubscribes;

        public int LiveSubscribes { get; private set; }

        public void Publish(NaradPersistedEvent persistedEvent) => inner.Publish(persistedEvent);

        public RuntimeEventStreamSubscription SubscribeForRoot(string rootRunId) => inner.SubscribeForRoot(rootRunId);

        public RuntimeEventStreamSubscription SubscribeForSession(string sessionId)
        {
            if (Interlocked.Increment(ref sessionSubscribes) == 1)
            {
                var channel = System.Threading.Channels.Channel.CreateUnbounded<NaradPersistedEvent>();
                channel.Writer.TryComplete(new InvalidOperationException(
                    $"Runtime stream for '{sessionId}' exceeded its 256-event live buffer; reconnect to replay durable history."));
                return new RuntimeEventStreamSubscription(channel.Reader, static () => { });
            }

            LiveSubscribes++;
            return inner.SubscribeForSession(sessionId);
        }

        public ApprovalInvalidationSubscription SubscribeForUserApprovals(string userId)
            => inner.SubscribeForUserApprovals(userId);

        public ApprovalInvalidationSubscription SubscribeForAdminApprovals()
            => inner.SubscribeForAdminApprovals();

        public void PublishApprovalInvalidation(
            string userId,
            string? agentId,
            string requestId,
            string eventId,
            string eventType,
            string? sessionId = null)
            => inner.PublishApprovalInvalidation(userId, agentId, requestId, eventId, eventType, sessionId);

        public IAsyncEnumerable<NaradPersistedEvent> Subscribe(string rootRunId, CancellationToken cancellationToken)
            => inner.Subscribe(rootRunId, cancellationToken);

        public async Task WaitForLiveSubscribeAsync(CancellationToken cancellationToken)
        {
            while (LiveSubscribes == 0)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class InMemoryWorkflowStore(GatewayWorkflowInstance instance) : IGatewayWorkflowStore
    {
        public GatewayWorkflowInstance Instance { get; private set; } = instance;
        public IReadOnlyList<WorkflowArtifact> Artifacts { get; set; } = [];

        public GatewayWorkflowInstance? GetInstance(string instanceId)
            => string.Equals(instanceId, Instance.Id, StringComparison.Ordinal) ? Instance : null;

        public GatewayWorkflowDefinition CreateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();

        public bool UpdateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();

        public GatewayWorkflowDefinition? GetDefinition(string workflowId) => throw new NotSupportedException();

        public IReadOnlyList<GatewayWorkflowDefinition> ListDefinitions(string ownerUserId, bool archived = false) => throw new NotSupportedException();
        public bool SetArchived(string workflowId, string ownerUserId, bool archived) => throw new NotSupportedException();
        public bool DeleteDefinitionIfNeverRun(string workflowId, string ownerUserId) => throw new NotSupportedException();
        public bool HasRuns(string workflowId, string ownerUserId) => throw new NotSupportedException();

        public GatewayWorkflowInstance CreateInstance(GatewayWorkflowInstance created, IReadOnlyList<WorkflowArtifact>? artifacts = null) => throw new NotSupportedException();

        public IReadOnlyList<WorkflowArtifact> ListArtifacts(string instanceId) => Artifacts;

        public GatewayWorkflowInstance? GetInstanceByStepSession(string sessionId) => null;

        public IReadOnlyList<GatewayWorkflowInstance> ListInstances(string ownerUserId, string? workflowId, int limit, int offset)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayWorkflowInstance> ListRecoverableInstances() => [];

        public bool TryRequestStop(string instanceId, string ownerUserId, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated)
        {
            updated = null;
            return false;
        }

        public bool TrySetInstanceStatus(
            string instanceId,
            string status,
            string? currentNodeId,
            string? failureCode,
            string? error,
            bool? stopRequested,
            DateTimeOffset nowUtc,
            out GatewayWorkflowInstance? updated)
        {
            Instance = Instance with
            {
                Status = status,
                CurrentNodeId = currentNodeId,
                FailureCode = failureCode,
                Error = error,
                StopRequested = stopRequested ?? Instance.StopRequested,
                UpdatedAt = nowUtc.ToString("O"),
            };
            updated = Instance;
            return true;
        }

        public bool TryUpdateEnvelope(string instanceId, JsonObject envelope, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated)
        {
            Instance = Instance with
            {
                Envelope = envelope,
                EnvelopeRevision = Instance.EnvelopeRevision + 1,
                UpdatedAt = nowUtc.ToString("O"),
            };
            updated = Instance;
            return true;
        }

        public bool TryUpdateStepAttempt(GatewayWorkflowStepAttempt attempt, DateTimeOffset nowUtc)
        {
            Instance = Instance with
            {
                Steps = [.. Instance.Steps.Select(step =>
                    string.Equals(step.Id, attempt.Id, StringComparison.Ordinal) ? attempt : step)],
                UpdatedAt = nowUtc.ToString("O"),
            };
            return true;
        }

        public bool TryAddStepAttempt(GatewayWorkflowStepAttempt attempt) => false;

        public bool TryClearMafCheckpoint(string instanceId, DateTimeOffset nowUtc) => false;

        public string? GetMafCheckpointJson(string instanceId) => null;

        public bool TrySaveMafCheckpointJson(string instanceId, string checkpointJson, DateTimeOffset nowUtc) => false;

        public NaradPersistedEvent AppendNaradEvent(NaradPersistedEvent persistedEvent) => persistedEvent;

        public IReadOnlyList<NaradPersistedEvent> ListNaradEvents(string instanceId, long afterSessionSequence, int limit) => [];

        public long ReadNaradSessionSequence(string instanceId) => 0;
    }
}
