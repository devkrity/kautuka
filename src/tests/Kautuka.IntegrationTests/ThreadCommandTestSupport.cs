using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

internal static class ThreadCommandTestSupport
{
    internal static GatewayToolApprovalRequest CreateRequest(string id, string? decision = null)
        => new(
            Id: id,
            SessionId: "session-1",
            RunId: "run-1",
            CheckpointId: "checkpoint-1",
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: decision,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));

    internal static NaradPersistedEvent RuntimeEvent(
        long sequence,
        string eventType,
        object payload,
        long? rootRuntimeSequence = null,
        string? toolCallId = null,
        string? runId = null,
        string? rootRunId = null,
        string? sessionId = null,
        string? parentSessionId = null)
    {
        string eventId = $"event-{sequence}";
        long revision = rootRuntimeSequence ?? sequence;
        string effectiveRunId = runId ?? "run-1";
        string effectiveRootRunId = rootRunId ?? effectiveRunId;
        string effectiveSessionId = sessionId ?? "session-1";
        JsonElement body = JsonSerializer.SerializeToElement(payload);
        if (toolCallId is not null || parentSessionId is not null)
        {
            var enriched = JsonNode.Parse(body.GetRawText())!.AsObject();
            if (toolCallId is not null)
            {
                enriched["toolCallId"] = toolCallId;
            }

            if (parentSessionId is not null)
            {
                enriched["parentSessionId"] = parentSessionId;
            }

            body = JsonSerializer.SerializeToElement(enriched, KautukaJsonContext.Default.JsonObject);
        }

        return NaradTestEventFactory.FromBody(
            eventType,
            body,
            effectiveSessionId,
            effectiveRunId,
            effectiveRootRunId,
            sequence,
            revision);
    }

    internal static ThreadCommandService CreateService(
        IGatewayApprovalStore approvalStore,
        ThreadCommandAgentStore? agentStore = null,
        IRunStopCommandService? stopCommands = null,
        ThreadCommandSessionStore? sessions = null,
        ThreadCommandUserStore? users = null,
        IWorkflowCommandService? workflowCommands = null,
        ApprovalResolutionTestInvocationLinkStore? invocationLinks = null,
        RecordingContinuationQueue? continuationQueue = null,
        IAgentToolApprovalWaiter? agentToolApprovalWaiter = null,
        IRunWaitStateStore? runWaitStateStore = null,
        IGatewayWorkflowStore? workflowStore = null)
    {
        agentStore ??= new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }));
        sessions ??= new ThreadCommandSessionStore();
        users ??= new ThreadCommandUserStore();
        invocationLinks ??= new ApprovalResolutionTestInvocationLinkStore();
        continuationQueue ??= new RecordingContinuationQueue();
        agentToolApprovalWaiter ??= new ThreadCommandNoOpApprovalWaiter();
        runWaitStateStore ??= new NoOpRunWaitStateStore();
        var coordinator = new ThreadCommandCoordinator(agentStore);
        IRunStopCommandService effectiveStopCommands = stopCommands ?? new NoOpRunStopCommandService();
        workflowCommands ??= CreateWorkflowService(
            agentStore,
            effectiveStopCommands,
            approvalStore,
            continuationQueue,
            waitStore: runWaitStateStore as NoOpRunWaitStateStore,
            invocationLinks: invocationLinks,
            agentToolApprovalWaiter: agentToolApprovalWaiter);
        return new ThreadCommandService(
            approvalStore,
            CreateResolutionService(
                approvalStore,
                effectiveStopCommands,
                invocationLinks,
                agentStore,
                continuationQueue,
                agentToolApprovalWaiter,
                runWaitStateStore),
            effectiveStopCommands,
            agentStore,
            invocationLinks,
            sessions,
            users,
            coordinator,
            workflowCommands,
            agentToolApprovalWaiter,
            runWaitStateStore,
            workflowStore);
    }

    internal static WorkflowCommandService CreateWorkflowService(
        ThreadCommandAgentStore agentStore,
        IRunStopCommandService stopCommands,
        IGatewayApprovalStore? approvalStore = null,
        RecordingContinuationQueue? continuationQueue = null,
        NoOpRunWaitStateStore? waitStore = null,
        ApprovalResolutionTestInvocationLinkStore? invocationLinks = null,
        IAgentToolApprovalWaiter? agentToolApprovalWaiter = null)
        => new(
            agentStore,
            stopCommands,
            waitStore ?? new NoOpRunWaitStateStore(),
            approvalStore ?? new ThreadCommandApprovalStore(),
            continuationQueue ?? new RecordingContinuationQueue(),
            invocationLinks ?? new ApprovalResolutionTestInvocationLinkStore(),
            agentStore,
            agentToolApprovalWaiter);

    internal static ThreadCommandService CreateStopCapableService(
        ThreadCommandApprovalStore approvalStore,
        out ThreadCommandAgentStore agentStore,
        out RunStopRegistry stopSignals)
    {
        agentStore = new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }));
        var stopSignalsLocal = new RunStopRegistry();
        stopSignals = stopSignalsLocal;
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignalsLocal.IsStopped("run-1")),
            stopSignalsLocal);
        return CreateService(
            approvalStore,
            agentStore,
            stopCommands);
    }

    internal static void RegisterThreadCommandServices(IServiceCollection services)
    {
        services.AddSingleton<ThreadCommandAgentStore>(_ => new ThreadCommandAgentStore(
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" })));
        services.AddSingleton<IAgentDefinitionStore>(static provider => provider.GetRequiredService<ThreadCommandAgentStore>());
        services.AddSingleton<IRuntimeEventHistoryReader>(static provider => provider.GetRequiredService<ThreadCommandAgentStore>());
        services.AddSingleton<IRootRunAdmissionGate, RootRunAdmissionGate>();
        services.AddSingleton<IThreadCommandCoordinator, ThreadCommandCoordinator>();
        services.AddSingleton<IRunStopCommandService, NoOpRunStopCommandService>();
        services.AddSingleton<IRunStopRegistry, RunStopRegistry>();
        services.AddSingleton<IRunWaitStateStore, NoOpRunWaitStateStore>();
        services.AddSingleton<IApprovalContinuationQueue, RecordingContinuationQueue>();
        services.AddSingleton<IAgentToolInvocationStore, NoOpInvocationStore>();
        services.AddSingleton<IRuntimeEventPublisher>(static provider => new ThreadCommandPublisher(
            provider.GetRequiredService<ThreadCommandAgentStore>(),
            static () => false));
        services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
        services.AddSingleton<IThreadCommandService, ThreadCommandService>();
    }

    internal static ApprovalResolutionService CreateResolutionService(
        IGatewayApprovalStore approvalStore,
        IRunStopCommandService? stopCommands = null,
        ApprovalResolutionTestInvocationLinkStore? invocationLinks = null,
        ThreadCommandAgentStore? agentStore = null,
        RecordingContinuationQueue? continuationQueue = null,
        IAgentToolApprovalWaiter? agentToolApprovalWaiter = null,
        IRunWaitStateStore? runWaitStateStore = null,
        IRunLimitResolver? runLimitResolver = null)
    {
        IRunStopCommandService effectiveStopCommands = stopCommands ?? new NoOpRunStopCommandService();
        invocationLinks ??= new ApprovalResolutionTestInvocationLinkStore();
        agentToolApprovalWaiter ??= new ThreadCommandNoOpApprovalWaiter();
        IRunWaitStateStore waits = runWaitStateStore ?? new NoOpRunWaitStateStore();
        var enqueueHandler = new ApprovalContinuationEnqueueHandler(
            continuationQueue ?? new RecordingContinuationQueue(),
            approvalStore,
            agentToolApprovalWaiter,
            invocationLinks,
            waits,
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);
        return new ApprovalResolutionService(
            approvalStore,
            new ThreadCommandMediator(enqueueHandler),
            new RunStopRegistry(),
            new SideEffectApplyingResolutionEventDispatcher(
                new ApprovalResolutionSideEffectApplier(effectiveStopCommands),
                approvalStore),
            invocationLinks,
            agentStore ?? new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" })),
            NullLogger<ApprovalResolutionService>.Instance,
            autopilotStore: null,
            agentToolApprovalWaiter,
            runLimitResolver,
            waits,
            new TestGatewayWorkflowStore());
    }

    internal static GatewayApprovalStore CreateGatewayApprovalStore(KautukaDatabase database)
        => new(database);

    internal sealed class MinimalAgentDefinitionStore : IAgentDefinitionStore
    {
        public string GetStorageLocatorKey(string agentId) => agentId;
        public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId) => null;
        public long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId) => 0;
        public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId) => false;
        public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId) => [];
        public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null) => throw new NotSupportedException();
        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey) => throw new NotSupportedException();
        public NaradPersistedEvent AppendNaradAuthorityEvent(string rootStorageLocatorKey, string sourceStorageLocatorKey, NaradAuthorityEventDraft draft) => throw new NotSupportedException();
        public NaradPersistedEvent AppendNaradParentSessionProjection(string rootStorageLocatorKey, NaradPersistedEvent sourceEvent, string parentSessionId) => throw new NotSupportedException();
        public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId) => throw new NotSupportedException();
        public string? FindNaradRootStorageLocatorKey(string rootRunId) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId, long afterSessionSequence, int limit) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId, long afterRootSequence, int limit) => throw new NotSupportedException();
        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId) => throw new NotSupportedException();
        public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore) => [];
        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit) => throw new NotSupportedException();
        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(string rootStorageLocatorKey, string leaseOwner, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner) => throw new NotSupportedException();
        public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner) => throw new NotSupportedException();
        public void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError) => throw new NotSupportedException();
        public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit) => throw new NotSupportedException();
        public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();
        public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();
        public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys() => throw new NotSupportedException();
        public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings) => throw new NotSupportedException();
        public void DeleteMemoryEmbedding(string storageLocatorKey, string factId) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey) => throw new NotSupportedException();
        public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit) => throw new NotSupportedException();
        public bool DeleteStore(string storageLocatorKey) => throw new NotSupportedException();
    }

    internal sealed class NoOpRootRunAdmissionGate : IRootRunAdmissionGate
    {
        public static NoOpRootRunAdmissionGate Instance { get; } = new();

        public bool IsAdmissionClosed(string agentId, string? rootRunId) => false;
    }

    internal sealed class ControllableAdmissionGate : IRootRunAdmissionGate
    {
        private readonly HashSet<string> closedRuns = new(StringComparer.Ordinal);

        public void Close(string rootRunId) => closedRuns.Add(rootRunId);

        public bool IsAdmissionClosed(string agentId, string? rootRunId)
            => !string.IsNullOrWhiteSpace(rootRunId) && closedRuns.Contains(rootRunId);
    }

    internal sealed class ThreadCommandApprovalStore : IGatewayApprovalStore
    {
        private readonly Dictionary<string, GatewayToolApprovalRequest> items;
        private readonly object gate = new();
        public int ResolveAttempts { get; private set; }

        public ApprovalResolutionEventWork? LastResolutionEvent { get; private set; }

        public List<ApprovalResolutionEventWork> ResolutionEvents { get; } = [];

        public ThreadCommandApprovalStore(params GatewayToolApprovalRequest[] requests)
            => items = requests.ToDictionary(request => request.Id, StringComparer.Ordinal);

        public void CreateRequest(GatewayToolApprovalRequest request)
        {
            lock (gate)
            {
                items[request.Id] = request;
            }
        }

        public bool Resolve(
            string requestId,
            string decision,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
        {
            lock (gate)
            {
                ResolveAttempts++;
                if (!items.TryGetValue(requestId, out GatewayToolApprovalRequest? request) || request.Decision is not null)
                {
                    return false;
                }

                items[requestId] = request with
                {
                    Decision = decision,
                    Reason = reason,
                    DecidedBy = decidedBy,
                    ResolutionSource = resolutionSource,
                };

                if (string.Equals(
                        PackedToolApproval.NormalizeStorageDecision(decision),
                        "stopped",
                        StringComparison.OrdinalIgnoreCase)
                    && resolutionEvent?.PackedRequestIds is { Count: > 1 } packedRequestIds)
                {
                    foreach (string packedId in packedRequestIds)
                    {
                        if (string.Equals(packedId, requestId, StringComparison.Ordinal)
                            || !items.TryGetValue(packedId, out GatewayToolApprovalRequest? sibling)
                            || sibling.Decision is not null)
                        {
                            continue;
                        }

                        items[packedId] = sibling with
                        {
                            Decision = "stopped",
                            Reason = reason,
                            DecidedBy = decidedBy,
                            ResolutionSource = resolutionSource,
                        };
                    }
                }

                CaptureResolutionEvent(resolutionEvent);
                return true;
            }
        }

        public bool Expire(
            string requestId,
            DateTimeOffset nowUtc,
            string reason,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
        {
            lock (gate)
            {
                if (!items.TryGetValue(requestId, out GatewayToolApprovalRequest? request) || request.Decision is not null)
                {
                    return false;
                }

                items[requestId] = request with
                {
                    Decision = "expired",
                    Reason = reason,
                    DecidedAt = nowUtc.ToString("O"),
                    ResolutionSource = "system_timeout",
                };
                CaptureResolutionEvent(resolutionEvent);
                return true;
            }
        }

        private void CaptureResolutionEvent(ApprovalResolutionEventWork? resolutionEvent)
        {
            if (resolutionEvent is not null
                && PackedToolApproval.PackHasPendingMembers(
                    resolutionEvent.PackedRequestIds,
                    id => items.TryGetValue(id, out GatewayToolApprovalRequest? row) ? row.Decision : null))
            {
                LastResolutionEvent = null;
                return;
            }

            LastResolutionEvent = resolutionEvent;
            if (resolutionEvent is not null)
            {
                ResolutionEvents.Add(resolutionEvent);
            }
        }

        public bool Expire(string requestId, DateTimeOffset nowUtc, string reason)
            => Expire(requestId, nowUtc, reason, null, null);

        public bool Resolve(
            string requestId,
            bool approved,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation)
            => Resolve(
                requestId,
                approved ? "approved" : "denied",
                reason,
                decidedBy,
                resolutionSource,
                continuation,
                null);

        public GatewayToolApprovalRequest? GetById(string requestId)
        {
            lock (gate)
            {
                return items.TryGetValue(requestId, out GatewayToolApprovalRequest? request) ? request : null;
            }
        }

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
        {
            lock (gate)
            {
                return items.Values
                    .Where(request => string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))
                    .ToArray();
            }
        }

        public IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string outerSessionId)
        {
            lock (gate)
            {
                return items.Values
                    .Where(request => string.Equals(request.OuterSessionId, outerSessionId, StringComparison.Ordinal))
                    .ToArray();
            }
        }

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName)
        {
            lock (gate)
            {
                return items.Values
                    .Where(request => request.Decision is not null
                        && string.Equals(request.RunId, runId, StringComparison.Ordinal)
                        && string.Equals(request.ToolCallId, toolCallId, StringComparison.Ordinal)
                        && string.Equals(request.ToolName, toolName, StringComparison.Ordinal))
                    .OrderByDescending(request => request.DecidedAt)
                    .FirstOrDefault();
            }
        }

        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    internal sealed class ThreadCommandAgentStore : IAgentDefinitionStore, IRuntimeEventHistoryReader
    {
        private readonly List<NaradPersistedEvent> events = [];
        private readonly object gate = new();

        public ThreadCommandAgentStore(params NaradPersistedEvent[] seed)
            => events.AddRange(seed);

        public void AppendEvent(NaradPersistedEvent persistedEvent)
        {
            lock (gate)
            {
                events.Add(persistedEvent);
            }
        }

        public string GetStorageLocatorKey(string agentId) => agentId;

        public long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId)
        {
            lock (gate)
            {
                return SessionProjectionRevision.ReadRevision(
                    events,
                    sessionId,
                    rootRunId => IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId));
            }
        }

        public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId)
        {
            lock (gate)
            {
                return events
                    .LastOrDefault(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                    ?.RootRunId;
            }
        }

        public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId)
        {
            lock (gate)
            {
                return events.Any(item =>
                    string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal)
                    && string.Equals(item.RunId, rootRunId, StringComparison.Ordinal)
                    && item.EventType is NaradEventTypes.RunStopRequested
                        or NaradEventTypes.RunFinished
                        or NaradEventTypes.RunError
                        or NaradEventTypes.RunCancelled);
            }
        }

        public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId)
        {
            lock (gate)
            {
                return SessionProjectionRevision.ListRootRunIdsForSession(events, sessionId);
            }
        }

        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId)
        {
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal))
                    .OrderBy(static item => item.Sequence ?? 0)
                    .ToArray();
            }
        }

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId)
        {
            _ = rootAgentId;
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                    .OrderBy(static item => item.SessionSequence)
                    .ToArray();
            }
        }

        public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
            => throw new NotSupportedException();

        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey) => throw new NotSupportedException();

        public NaradPersistedEvent AppendNaradAuthorityEvent(string rootStorageLocatorKey, string sourceStorageLocatorKey, NaradAuthorityEventDraft draft)
            => throw new NotSupportedException();

        public NaradPersistedEvent AppendNaradParentSessionProjection(
            string rootStorageLocatorKey,
            NaradPersistedEvent sourceEvent,
            string parentSessionId)
            => throw new NotSupportedException();

        public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId)
            => throw new NotSupportedException();

        public string? FindNaradRootStorageLocatorKey(string rootRunId) => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId, long afterSessionSequence, int limit)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId, long afterRootSequence, int limit)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId)
            => ReadRootHistory(rootRunId);

        public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore) => [];

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit) => throw new NotSupportedException();

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(string rootStorageLocatorKey, string leaseOwner, TimeSpan leaseDuration, int limit)
            => throw new NotSupportedException();

        public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner) => throw new NotSupportedException();

        public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner) => throw new NotSupportedException();

        public void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit)
            => throw new NotSupportedException();

        public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();

        public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();

        public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys() => throw new NotSupportedException();

        public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings)
            => throw new NotSupportedException();

        public void DeleteMemoryEmbedding(string storageLocatorKey, string factId) => throw new NotSupportedException();

        public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey)
            => throw new NotSupportedException();

        public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit)
            => throw new NotSupportedException();

        public bool DeleteStore(string storageLocatorKey) => throw new NotSupportedException();
    }

    internal sealed class ThreadCommandSessionStore : IGatewaySessionStore
    {
        private readonly Dictionary<string, GatewaySession> sessions;

        public ThreadCommandSessionStore(params GatewaySession[] extraSessions)
        {
            GatewaySession primary = new(
                "session-1",
                "agent",
                "agent-1",
                "user-1",
                "Thread",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null);
            sessions = new Dictionary<string, GatewaySession>(StringComparer.Ordinal)
            {
                [primary.Id] = primary,
            };
            foreach (GatewaySession session in extraSessions)
            {
                sessions[session.Id] = session;
            }
        }

        public bool TryGetSession(string sessionId, out GatewaySession? resolved)
            => sessions.TryGetValue(sessionId, out resolved);

        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null)
            => throw new NotSupportedException();

        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false)
            => throw new NotSupportedException();

        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => throw new NotSupportedException();

        public GatewaySession? ArchiveSession(string sessionId) => throw new NotSupportedException();

        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => throw new NotSupportedException();

        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId)
            => throw new NotSupportedException();

        public bool DeleteSession(string sessionId) => throw new NotSupportedException();

        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => throw new NotSupportedException();

        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
            => throw new NotSupportedException();

        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null)
            => throw new NotSupportedException();

        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => throw new NotSupportedException();

        public bool DeleteMemoryFact(string factId) => throw new NotSupportedException();

        public bool UpdateMemoryFact(string factId, string title, string content, string category) => throw new NotSupportedException();

        public IReadOnlyList<GatewayRun> ListRuns() => throw new NotSupportedException();

        public bool TryGetRun(string runId, out GatewayRun? run)
        {
            run = null;
            return false;
        }

        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
            => throw new NotSupportedException();

        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null)
            => throw new NotSupportedException();

        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options)
            => throw new NotSupportedException();

        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null)
            => throw new NotSupportedException();
    }

    internal sealed class ThreadCommandUserStore : IGatewayUserStore
    {
        private readonly GatewayUser user = new(
            "user-1",
            "user-1",
            "password",
            "admin",
            true,
            null,
            "Admin User");

        public bool TryGetUserById(string userId, out GatewayUser? resolved)
        {
            resolved = string.Equals(userId, user.Id, StringComparison.Ordinal) ? user : null;
            return resolved is not null;
        }

        public bool TryGetUserByUsername(string username, out GatewayUser? resolved)
        {
            resolved = string.Equals(username, user.Username, StringComparison.Ordinal) ? user : null;
            return resolved is not null;
        }

        public IReadOnlyList<GatewayUserListItem> ListUsers() => throw new NotSupportedException();

        public bool CanSetup => false;

        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) => throw new NotSupportedException();

        public GatewayUserListItem? InviteUser(JsonElement body, out string? error) => throw new NotSupportedException();

        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) => throw new NotSupportedException();

        public bool UpdateIsActive(string userId, bool isActive, out string? error) => throw new NotSupportedException();

        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => throw new NotSupportedException();

        public bool UpdateProfile(string userId, string? email, string? displayName) => throw new NotSupportedException();

        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) => throw new NotSupportedException();


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    internal sealed class NoOpResolutionEventDispatcher : IApprovalResolutionEventDispatcher
    {
        public ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class SideEffectApplyingResolutionEventDispatcher(
        IApprovalResolutionSideEffectApplier sideEffectApplier,
        IGatewayApprovalStore approvalStore) : IApprovalResolutionEventDispatcher
    {
        public async ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken)
        {
            ApprovalResolutionEventWork? work = approvalStore switch
            {
                ThreadCommandApprovalStore threadStore => threadStore.LastResolutionEvent,
                _ => null,
            };
            if (work is null)
            {
                return;
            }

            if (work.StopRun)
            {
                await sideEffectApplier.ApplyAsync(work, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class NoOpRunStopCommandService : IRunStopCommandService
    {
        public ValueTask<RunStopCommandStatus> RequestStopAsync(RunStopCommand command, CancellationToken cancellationToken)
            => ValueTask.FromResult(RunStopCommandStatus.Accepted);
    }

    private sealed class ThreadCommandNoOpApprovalWaiter : IAgentToolApprovalWaiter
    {
        public bool IsWaiting(string requestId) => false;

        public ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    internal sealed class ThreadCommandWaitingApprovalWaiter(string requestId) : IAgentToolApprovalWaiter
    {
        public bool IsWaiting(string id)
            => string.Equals(id, requestId, StringComparison.Ordinal);

        public ValueTask<HitlApprovalResolved> WaitAsync(string id, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    internal sealed class ThreadCommandMediator(params INotificationHandler<HitlApprovalResolved>[] approvalResolvedHandlers) : IMediator
    {
        public async ValueTask Publish(object notification, CancellationToken cancellationToken = default)
        {
            if (notification is HitlApprovalResolved resolved)
            {
                foreach (INotificationHandler<HitlApprovalResolved> handler in approvalResolvedHandlers)
                {
                    await handler.Handle(resolved, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Publish((object)notification!, cancellationToken);

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();

        public ValueTask<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class ThreadCommandPublisher(ThreadCommandAgentStore history, Func<bool> signalState) : IRuntimeEventPublisher
    {
        public List<NaradPersistedEvent> Published { get; } = [];

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            _ = signalState();
            long sessionSequence = history.ReadSessionHistory(context.AgentId, context.SessionId).Count + Published.Count + 1;
            long? rootSequence = string.IsNullOrWhiteSpace(context.RootRunId)
                ? null
                : history.ReadRootHistory(context.RootRunId).Count + Published.Count(item =>
                    string.Equals(item.RootRunId, context.RootRunId, StringComparison.Ordinal)) + 1;
            NaradPersistedEvent persistedEvent = NaradTestEventFactory.FromDraft(draft, context, sessionSequence, rootSequence);
            Published.Add(persistedEvent);
            history.AppendEvent(persistedEvent);
            return ValueTask.FromResult(persistedEvent);
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    internal sealed class NoOpRunWaitStateStore : IRunWaitStateStore
    {
        private readonly List<RunWaitState> waits = [];

        public void Seed(RunWaitState wait) => waits.Add(wait);

        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();

        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();

        public RunWaitState? GetByWaitId(string waitId) => waits.FirstOrDefault(w => string.Equals(w.WaitId, waitId, StringComparison.Ordinal));

        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId)
            => waits.FirstOrDefault(w => string.Equals(w.ContinuationRunId, continuationRunId, StringComparison.Ordinal));

        public RunWaitState? GetActiveByCorrelationId(string correlationId)
            => waits.FirstOrDefault(w => string.Equals(w.CorrelationId, correlationId, StringComparison.Ordinal));

        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId)
            => waits.FirstOrDefault(w => string.Equals(w.SessionId, callerSessionId, StringComparison.Ordinal)
                && string.Equals(w.ToolCallId, callerToolCallId, StringComparison.Ordinal)
                && string.Equals(w.WaitReason, RunWaitReasons.Child, StringComparison.Ordinal));

        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => true;

        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }

        public void ReleaseForRetry(string waitId, string leaseOwner) { }

        public void MarkPausedAgain(string waitId) { }

        public void ReleaseExecutingWait(string waitId) { }

        public void MarkCompleted(string waitId) { }

        public void MarkFailed(string waitId, string failureCode) { }

        public IReadOnlyList<RunWaitState> ListRecoverable() => waits;

        public void DeleteBySession(string sessionId) => waits.RemoveAll(w => string.Equals(w.SessionId, sessionId, StringComparison.Ordinal));
    }

    internal sealed class RecordingContinuationQueue : IApprovalContinuationQueue
    {
        public List<ApprovalContinuationWork> Enqueued { get; } = [];

        public ValueTask<ApprovalContinuationEnqueueResult> EnqueueAsync(ApprovalContinuationWork work, CancellationToken cancellationToken)
        {
            if (Enqueued.Any(item => string.Equals(item.RequestId, work.RequestId, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(ApprovalContinuationEnqueueResult.AlreadyPending);
            }

            Enqueued.Add(work);
            return ValueTask.FromResult(ApprovalContinuationEnqueueResult.Inserted);
        }

        public IAsyncEnumerable<ApprovalContinuationLease> LeaseAllAsync(string leaseOwner, CancellationToken cancellationToken)
            => AsyncEnumerable.Empty<ApprovalContinuationLease>();

        public ValueTask CompleteAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RenewAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<ApprovalContinuationFailureDisposition> FailAsync(
            ApprovalContinuationLease lease,
            string safeError,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ApprovalContinuationFailureDisposition.RetryScheduled);
    }

    internal sealed class RecordingThreadCommandService : IThreadCommandService
    {
        public StopActiveRootCommand? LastStopCommand { get; private set; }
        public ResumeWaitingRootCommand? LastResumeCommand { get; private set; }
        public ResolveThreadInterruptCommand? LastResolveCommand { get; private set; }
        public ThreadCommandResult StopResult { get; set; } = new(ThreadCommandStatus.Accepted, Decision: "stopped");
        public ThreadCommandResult ResumeResult { get; set; } = new(ThreadCommandStatus.Accepted, Decision: "continuation_enqueued");
        public IApprovalResolutionService? Resolution { get; set; }

        public ValueTask<ThreadCommandResult> StopActiveRootAsync(StopActiveRootCommand command, CancellationToken cancellationToken)
        {
            LastStopCommand = command;
            return ValueTask.FromResult(StopResult);
        }

        public async ValueTask<ThreadCommandResult> ResolveInterruptAsync(ResolveThreadInterruptCommand command, CancellationToken cancellationToken)
        {
            LastResolveCommand = command;
            if (Resolution is null)
            {
                throw new NotSupportedException();
            }

            if (!ApprovalActionCatalog.TryMapDecision(command.Decision, out bool approved, out bool stopRun, out string action))
            {
                return new ThreadCommandResult(ThreadCommandStatus.Rejected, Detail: "Field 'decision' is not a known interrupt action.");
            }

            ApprovalResolutionResult resolution = await Resolution.ResolveAsync(
                new ApprovalResolutionCommand(
                    command.InterruptId,
                    approved,
                    command.Reason,
                    command.UserId,
                    command.Source,
                    UserId: command.UserId,
                    StopRun: stopRun,
                    OriginatingChannelType: command.OriginatingChannelType,
                    DecidedByDisplayName: command.Actor,
                    Action: action),
                cancellationToken).ConfigureAwait(false);

            return resolution.Status switch
            {
                ApprovalResolutionStatus.Resolved => new ThreadCommandResult(
                    ThreadCommandStatus.Accepted,
                    Decision: resolution.Request?.Decision,
                    RequestId: command.InterruptId),
                ApprovalResolutionStatus.AlreadyResolved => new ThreadCommandResult(
                    ThreadCommandStatus.AlreadyApplied,
                    Decision: resolution.Request?.Decision,
                    RequestId: command.InterruptId),
                ApprovalResolutionStatus.Expired => new ThreadCommandResult(
                    ThreadCommandStatus.Expired,
                    RequestId: command.InterruptId),
                _ => new ThreadCommandResult(ThreadCommandStatus.NotFound, RequestId: command.InterruptId),
            };
        }

        public ValueTask<ThreadCommandResult> ResolveAllInterruptsAsync(ResolveAllThreadInterruptsCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> ResumeWaitingRootAsync(ResumeWaitingRootCommand command, CancellationToken cancellationToken)
        {
            LastResumeCommand = command;
            return ValueTask.FromResult(ResumeResult);
        }

        public ValueTask<ThreadCommandResult> RetryTargetAsync(RetryExecutionTargetCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> CancelGraphNodeAsync(CancelGraphNodeCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    internal sealed class NoOpInvocationStore : IAgentToolInvocationStore
    {
        private readonly Dictionary<string, AgentToolInvocation> items = new(StringComparer.Ordinal);

        public void Seed(AgentToolInvocation invocation) => items[invocation.InvocationId] = invocation;

        public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request) => throw new NotSupportedException();

        public AgentToolInvocation? Get(string invocationId)
            => items.TryGetValue(invocationId, out AgentToolInvocation? invocation) ? invocation : null;

        public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId) => null;

        public AgentToolInvocation? GetByChildRunId(string childRunId)
            => items.Values.FirstOrDefault(item => string.Equals(item.ChildRunId, childRunId, StringComparison.Ordinal));

        public IReadOnlyList<AgentToolInvocation> ListRecoverable() => items.Values.ToArray();

        public void RecordApprovalLink(AgentToolInvocationLink link) { }

        public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome) { }

        public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration) => false;

        public void MarkWaitingOnChild(string invocationId, string childRunId) { }

        public void MarkCompleted(string invocationId, string leaseOwner) { }

        public bool TrySealCompleted(string invocationId) => false;

        public void MarkFailed(string invocationId, string failureCode) { }
    }

    internal sealed class RecordingRunStopCommandService : IRunStopCommandService
    {
        public List<RunStopCommand> Commands { get; } = [];

        public ValueTask<RunStopCommandStatus> RequestStopAsync(
            RunStopCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return ValueTask.FromResult(RunStopCommandStatus.Accepted);
        }
    }
}

internal sealed class ApprovalResolutionTestInvocationLinkStore(params AgentToolInvocationLink[] links) : IAgentToolInvocationLinkStore
{
    private readonly List<AgentToolInvocationLink> values = [.. links];

    public void Upsert(AgentToolInvocationLink link)
    {
        values.RemoveAll(candidate => string.Equals(candidate.ApprovalRequestId, link.ApprovalRequestId, StringComparison.Ordinal));
        values.Add(link);
    }

    public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
        => values.FirstOrDefault(link => string.Equals(link.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal));

    public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
        => [.. values.Where(link => string.Equals(link.CallerSessionId, callerSessionId, StringComparison.Ordinal))];

    public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
        => values.FirstOrDefault(link => string.Equals(link.ChildSessionId, childSessionId, StringComparison.Ordinal));

    public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
        => [.. values.Where(link => string.Equals(link.RootRunId, rootRunId, StringComparison.Ordinal))];

    public bool HasLinksForSession(string sessionId)
        => values.Any(link =>
            string.Equals(link.CallerSessionId, sessionId, StringComparison.Ordinal)
            || string.Equals(link.ChildSessionId, sessionId, StringComparison.Ordinal));
}
