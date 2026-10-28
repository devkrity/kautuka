using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ExecutionReconciliationNaradTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryProtectsAnUnexpiredContinuationLeaseButNotAnExpiredOne(bool liveLease)
    {
        var history = new FixedRootHistoryReader(
        [
            HistoryEvent(NaradEventTypes.RunStarted, "run-root", "run-root", occurredAt: "2026-08-01T00:00:00Z"),
            HistoryEvent(NaradEventTypes.InterruptRequested, "run-root", "run-root", interruptId: "approval-1"),
            HistoryEvent(NaradEventTypes.InterruptResolved, "run-root", "run-root", interruptId: "approval-1"),
            HistoryEvent(NaradEventTypes.RunResumed, "run-root", "run-root"),
        ]);
        RunWaitState wait = CreateRecoverableWait("wait-1", "approval-1", "run-root") with
        {
            Status = RunWaitStatuses.Executing,
            LeaseOwner = "current-gateway",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(liveLease ? 2 : -2).ToString("O"),
        };
        var publisher = new RecordingNaradPublisher();
        var service = CreateReconciliation(history, publisher,
            new ThreadCommandTestSupport.ThreadCommandApprovalStore(), new FixedRecoverableWaitStore([wait]),
            includeInterruptedRoot: true);
        await service.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.Equal(!liveLease, publisher.Published.Any(item => item.RunId == "run-root" && NaradRunLifecycle.IsTerminal(item.EventType)));
    }

    [Fact]
    public async Task ReconcileDoesNotAppendProcessLostToAlreadyFinishedChild()
    {
        var historyReader = new FixedRootHistoryReader(
        [
            HistoryEvent(NaradEventTypes.RunStarted, "run-root", "run-root", occurredAt: "2026-08-01T00:00:00Z"),
            HistoryEvent(NaradEventTypes.ToolCallStarted, "run-root", "run-root", toolCallId: "tool-open"),
            HistoryEvent(NaradEventTypes.RunStarted, "child-run", "run-root"),
            HistoryEvent(NaradEventTypes.RunFinished, "child-run", "run-root"),
        ]);
        var publisher = new RecordingNaradPublisher();
        var invocations = new ThreadCommandTestSupport.NoOpInvocationStore();
        invocations.Seed(new AgentToolInvocation("inv-1", "run-root", "session-1", "tool-open", "invoke_agent_roller",
            "child-run", "child-session", "agent-child", "run-root", AgentToolInvocationStates.WaitingOnChild,
            "2026-08-01T00:00:00Z", "2026-08-01T00:00:00Z"));
        var reconciliation = CreateReconciliation(historyReader, publisher, new ThreadCommandTestSupport.ThreadCommandApprovalStore(),
            new FixedRecoverableWaitStore([]), invocations);
        await reconciliation.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(publisher.Published, item => item.RunId == "child-run" && NaradRunLifecycle.IsTerminal(item.EventType));
    }

    [Fact]
    public void AnalyzeDetectsOpenInterruptPauseToolAndChildRuns()
    {
        IReadOnlyList<NaradPersistedEvent> history =
        [
            HistoryEvent(NaradEventTypes.RunStarted, "run-root", "run-root"),
            HistoryEvent(NaradEventTypes.InterruptRequested, "run-root", "run-root", interruptId: "interrupt-1", waitId: "wait-1"),
            HistoryEvent(NaradEventTypes.RunPaused, "run-root", "run-root", waitId: "wait-1", interruptId: "interrupt-1"),
            HistoryEvent(NaradEventTypes.ToolCallStarted, "run-root", "run-root", toolCallId: "tool-1"),
            HistoryEvent(NaradEventTypes.RunStarted, "run-child", "run-root"),
        ];

        NaradRunOpenState openState = NaradRunHistoryState.Analyze(history);

        Assert.True(openState.RootPaused);
        Assert.Single(openState.OpenInterrupts);
        Assert.Equal("interrupt-1", openState.OpenInterrupts[0].InterruptId);
        Assert.Equal("tool-1", Assert.Single(openState.OpenToolCallIds));
        Assert.Equal("run-child", Assert.Single(openState.OpenChildRunIds));
    }

    [Fact]
    public async Task ReconcileLeavesValidRecoverableWaitPaused()
    {
        var historyReader = new FixedRootHistoryReader(
        [
            HistoryEvent(NaradEventTypes.RunStarted, "run-root", "run-root", occurredAt: "2026-08-01T00:00:00Z"),
            HistoryEvent(NaradEventTypes.InterruptRequested, "run-root", "run-root", interruptId: "approval-1", waitId: "wait:approval-1"),
            HistoryEvent(NaradEventTypes.RunPaused, "run-root", "run-root", waitId: "wait:approval-1", interruptId: "approval-1"),
        ]);
        var publisher = new RecordingNaradPublisher();
        var approvalStore = new ThreadCommandTestSupport.ThreadCommandApprovalStore(ThreadCommandTestSupport.CreateRequest("approval-1"));
        RunWaitState wait = CreateRecoverableWait("wait:approval-1", "approval-1", "run-root");

        var reconciliation = CreateReconciliation(
            historyReader,
            publisher,
            approvalStore,
            new FixedRecoverableWaitStore([wait]));

        await reconciliation.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(publisher.Published, item => NaradRunLifecycle.IsTerminal(item.EventType));
        Assert.DoesNotContain(publisher.Published, item => item.EventType == NaradEventTypes.InterruptResolved);
    }

    [Fact]
    public async Task ReconcileResolvesExpiredInterruptBeforeRootTerminal()
    {
        var historyReader = new FixedRootHistoryReader(
        [
            HistoryEvent(NaradEventTypes.RunStarted, "run-root", "run-root", occurredAt: "2026-08-01T00:00:00Z"),
            HistoryEvent(NaradEventTypes.InterruptRequested, "run-root", "run-root", interruptId: "approval-ghost", waitId: "wait:approval-ghost"),
            HistoryEvent(NaradEventTypes.RunPaused, "run-root", "run-root", waitId: "wait:approval-ghost", interruptId: "approval-ghost"),
            HistoryEvent(NaradEventTypes.ToolCallStarted, "run-root", "run-root", toolCallId: "tool-open"),
        ]);
        var publisher = new RecordingNaradPublisher();
        var invocations = new ThreadCommandTestSupport.NoOpInvocationStore();
        invocations.Seed(new AgentToolInvocation(
            "inv-1",
            "run-root",
            "session-1",
            "tool-open",
            "run_skill",
            "child-run",
            "child-session",
            "agent-child",
            "run-root",
            AgentToolInvocationStates.WaitingOnChild,
            "2026-08-01T00:00:00Z",
            "2026-08-01T00:00:00Z"));

        var reconciliation = CreateReconciliation(
            historyReader,
            publisher,
            new ThreadCommandTestSupport.ThreadCommandApprovalStore(),
            new FixedRecoverableWaitStore([CreateRecoverableWait("wait:approval-ghost", "approval-ghost", "run-root")]),
            invocations);

        await reconciliation.ReconcileAsync(TestContext.Current.CancellationToken);

        NaradPersistedEvent resolved = Assert.Single(
            publisher.Published,
            item => item.EventType == NaradEventTypes.InterruptResolved);
        Assert.Equal("expired", NaradPersistedEventReader.String(resolved, "decision"));
        NaradPersistedEvent toolFinished = Assert.Single(
            publisher.Published,
            item => item.EventType == NaradEventTypes.ToolCallFinished
                && string.Equals(NaradPersistedEventReader.String(item, "toolCallId"), "tool-open", StringComparison.Ordinal));
        Assert.NotNull(toolFinished);
        Assert.Single(
            publisher.Published,
            item => item.EventType == NaradEventTypes.RunError
                && item.EventId.Contains("child-terminal", StringComparison.Ordinal));
    }

    private static ExecutionReconciliationService CreateReconciliation(
        IRuntimeEventHistoryReader historyReader,
        IRuntimeEventPublisher publisher,
        IGatewayApprovalStore approvalStore,
        IRunWaitStateStore waitStateStore,
        IAgentToolInvocationStore? invocationStore = null,
        bool includeInterruptedRoot = false)
        => new(
            invocationStore ?? new ThreadCommandTestSupport.NoOpInvocationStore(),
            new ApprovalResolutionTestInvocationLinkStore(),
            waitStateStore,
            approvalStore,
            new RecordingQuarantineStore(),
            new ReconciliationStubAgentStore(includeInterruptedRoot ? historyReader : null),
            historyReader,
            publisher,
            NullLogger<ExecutionReconciliationService>.Instance);

    private static RunWaitState CreateRecoverableWait(string waitId, string correlationId, string rootRunId)
        => new(
            waitId,
            rootRunId,
            rootRunId,
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            correlationId,
            "{}",
            "{}",
            "[]",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "2026-08-01T00:00:00Z",
            null,
            "evt-pause",
            rootRunId);

    private static NaradPersistedEvent HistoryEvent(
        string eventType,
        string runId,
        string rootRunId,
        string? occurredAt = null,
        string? interruptId = null,
        string? waitId = null,
        string? toolCallId = null)
    {
        var body = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrWhiteSpace(interruptId))
        {
            body["interruptId"] = interruptId;
        }

        if (!string.IsNullOrWhiteSpace(waitId))
        {
            body["waitId"] = waitId;
        }

        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            body["toolCallId"] = toolCallId;
        }

        if (string.Equals(eventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
        {
            body["reason"] = "interrupt";
            body["resumeMode"] = "command";
        }

        if (string.Equals(eventType, NaradEventTypes.ToolCallStarted, StringComparison.Ordinal))
        {
            body["toolName"] = "search";
            body["executor"] = "backend";
            body["args"] = new System.Text.Json.Nodes.JsonObject();
        }

        NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
            $"evt-{eventType}-{runId}",
            eventType,
            "session-1",
            runId,
            rootRunId,
            JsonSerializer.SerializeToElement(body));
        return occurredAt is null
            ? persistedEvent
            : persistedEvent with { OccurredAt = occurredAt };
    }

    private sealed class FixedRootHistoryReader(IReadOnlyList<NaradPersistedEvent> history) : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => history;
        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => history;
    }

    private sealed class FixedRecoverableWaitStore(IReadOnlyList<RunWaitState> waits) : IRunWaitStateStore
    {
        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId) => waits.FirstOrDefault(wait => string.Equals(wait.WaitId, waitId, StringComparison.Ordinal));
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId) => null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId) => waits.FirstOrDefault(wait => string.Equals(wait.CorrelationId, correlationId, StringComparison.Ordinal));
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId) => null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => false;
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }
        public void ReleaseForRetry(string waitId, string leaseOwner) { }
        public void MarkPausedAgain(string waitId) { }
        public void ReleaseExecutingWait(string waitId) { }
        public void MarkCompleted(string waitId) { }
        public void MarkFailed(string waitId, string failureCode) { }
        public IReadOnlyList<RunWaitState> ListRecoverable() => waits.Where(wait => wait.Status != RunWaitStatuses.Executing
            || string.CompareOrdinal(wait.LeaseExpiresAt, DateTimeOffset.UtcNow.ToString("O")) <= 0).ToArray();
        public void DeleteBySession(string sessionId) { }
    }

    private sealed class RecordingNaradPublisher : IRuntimeEventPublisher
    {
        public List<NaradPersistedEvent> Published { get; } = [];

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => PublishWithIdAsync(draft.EventId, draft.EventType, draft.Body, context, cancellationToken);

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
                eventId,
                eventType,
                context.SessionId,
                context.RunId,
                context.RootRunId,
                body);
            Published.Add(persistedEvent);
            return ValueTask.FromResult(persistedEvent);
        }
    }

    private sealed class RecordingQuarantineStore : IExecutionQuarantineStore
    {
        public ExecutionQuarantineItem Quarantine(string entityType, string entityId, string reasonCode, string safeDiagnostic)
            => new(
                Guid.NewGuid().ToString("N"),
                entityType,
                entityId,
                reasonCode,
                safeDiagnostic,
                ExecutionQuarantineStatuses.Open,
                0,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"));

        public ExecutionQuarantineItem? Get(string quarantineId) => null;
        public IReadOnlyList<ExecutionQuarantineItem> ListActive() => [];
        public bool RequestRetry(string quarantineId) => false;
        public bool Terminate(string quarantineId) => false;
        public bool Resolve(string entityType, string entityId) => true;
        public void MarkAuditPublished(string quarantineId, string publishedAt) { }
    }

    private sealed class SingleAgentCatalogStore(string agentId) : IGatewayCatalogStore
    {
        private readonly GatewayAgent agent = new(
            Id: agentId,
            Name: "Agent",
            ModelId: "model-1",
            SystemPrompt: "prompt",
            SuperExecMode: "trusted",
            Session: new System.Text.Json.Nodes.JsonObject(),
            Planning: new System.Text.Json.Nodes.JsonObject(),
            Sandbox: new System.Text.Json.Nodes.JsonObject(),
            SkillAttachments: [],
            OwnerUserId: "user-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            Enabled: true);

        public bool TryGetProvider(string id, out GatewayProvider? provider) { provider = null; return false; }
        public IReadOnlyList<GatewayProvider> ListProviders() => [];
        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();
        public bool DeleteProvider(string id) => false;
        public bool TryGetModel(string id, out GatewayModel? model) { model = null; return false; }
        public IReadOnlyList<GatewayModel> ListModels() => [];
        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();
        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) { }
        public bool DeleteModel(string id) => false;
        public bool TryGetAgent(string id, out GatewayAgent? found)
        {
            if (string.Equals(id, agentId, StringComparison.Ordinal))
            {
                found = agent;
                return true;
            }

            found = null;
            return false;
        }

        public IReadOnlyList<GatewayAgent> ListAgents() => [agent];
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

    private sealed class ReconciliationStubAgentStore(IRuntimeEventHistoryReader? historyReader = null) : IAgentDefinitionStore
    {
        public string GetStorageLocatorKey(string agentId) => agentId;
        public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId) => false;
        public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null) => throw new NotSupportedException();
        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey) => null;
        public NaradPersistedEvent AppendNaradAuthorityEvent(string rootStorageLocatorKey, string sourceStorageLocatorKey, NaradAuthorityEventDraft draft) => throw new NotSupportedException();
        public NaradPersistedEvent AppendNaradParentSessionProjection(string rootStorageLocatorKey, NaradPersistedEvent sourceEvent, string parentSessionId) => throw new NotSupportedException();
        public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent) { }
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId) => [];
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId) => [];
        public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId) => null;
        public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey) => new Dictionary<string, AgentMemoryEmbeddingState>();
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId, long afterSessionSequence, int limit) => [];
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId) => [];
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId, long afterRootSequence, int limit) => [];
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId) => historyReader?.ReadRootHistory(rootRunId) ?? [];
        public long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId) => 0;
        public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId) => [];
        public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore) => historyReader is null ? [] : ["run-root"];
        public string? FindNaradRootStorageLocatorKey(string rootRunId) => historyReader is null ? null : "agent-1";
        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit) => [];
        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(string rootStorageLocatorKey, string leaseOwner, TimeSpan leaseDuration, int limit) => [];
        public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner) { }
        public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner) { }
        public void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError) { }
        public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit) => [];
        public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => false;
        public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => false;
        public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys() => [];
        public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings) { }
        public void DeleteMemoryEmbedding(string storageLocatorKey, string factId) { }
        public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit) => [];
        public bool DeleteStore(string storageLocatorKey) => false;
    }
}
