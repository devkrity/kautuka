using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Infrastructure.Observability;

using Narad.Contracts;
namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class ChatGatewayService : IChatGatewayService, IMemoryExtractionProcessor
{
    private readonly IGatewaySessionStore sessions;
    private readonly ISessionLifecycleService sessionLifecycleService;
    private readonly IAgentChatService agentChatService;
    private readonly ILogger<ChatGatewayService> logger;
    private readonly IMemoryFactExtractor factExtractor;
    private readonly IChatMemoryAssembler memoryAssembler;
    private readonly IAgentMemoryEmbeddingService memoryEmbeddingService;
    private readonly IMemoryScopePolicy memoryScopePolicy;
    private readonly KautukaAiClientFactory aiClientFactory;
    private readonly GatewayState state;
    private readonly IPlatformConfigurationProvider configProvider;
    private readonly IConversationAssetStore assetStore;
    private readonly IGatewayCatalogStore catalogStore;
    private readonly IGatewayUserStore userStore;
    private readonly IAgentFilePathAuthorizationService agentFilePathAuthorizationService;
    private readonly IGatewayUsageQueue usageQueue;
    private readonly IRunWaitStateStore runWaitStateStore;
    private readonly ISessionCompactionService sessionCompactionService;
    private readonly IRuntimeEventPublisher runtimeEventPublisher;
    private readonly IGatewayApprovalStore approvalStore;
    private readonly MemoryExtractionJobStore memoryExtractionJobs;

    public ChatGatewayService(
        IGatewaySessionStore sessions,
        ISessionLifecycleService sessionLifecycleService,
        IAgentChatService agentChatService,
        ILogger<ChatGatewayService> logger,
        IMemoryFactExtractor factExtractor,
        IChatMemoryAssembler memoryAssembler,
        IAgentMemoryEmbeddingService memoryEmbeddingService,
        IMemoryScopePolicy memoryScopePolicy,
        KautukaAiClientFactory aiClientFactory,
        GatewayState state,
        IPlatformConfigurationProvider configProvider,
        IConversationAssetStore assetStore,
        IGatewayCatalogStore catalogStore,
        IGatewayUserStore userStore,
        IAgentFilePathAuthorizationService agentFilePathAuthorizationService,
        IGatewayUsageQueue usageQueue,
        IRunWaitStateStore runWaitStateStore,
        ISessionCompactionService sessionCompactionService,
        IRuntimeEventPublisher runtimeEventPublisher,
        IGatewayApprovalStore approvalStore)
    {
        this.sessions = sessions;
        this.sessionLifecycleService = sessionLifecycleService;
        this.agentChatService = agentChatService;
        this.logger = logger;
        this.factExtractor = factExtractor;
        this.memoryAssembler = memoryAssembler;
        this.memoryEmbeddingService = memoryEmbeddingService;
        this.memoryScopePolicy = memoryScopePolicy;
        this.aiClientFactory = aiClientFactory;
        this.state = state;
        this.configProvider = configProvider;
        this.assetStore = assetStore;
        this.catalogStore = catalogStore;
        this.userStore = userStore;
        this.agentFilePathAuthorizationService = agentFilePathAuthorizationService;
        this.usageQueue = usageQueue;
        this.runWaitStateStore = runWaitStateStore;
        this.sessionCompactionService = sessionCompactionService;
        this.runtimeEventPublisher = runtimeEventPublisher;
        this.approvalStore = approvalStore;
        memoryExtractionJobs = new MemoryExtractionJobStore(state.Database, TimeProvider.System);
    }

    public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
        JsonElement body,
        AgentRequestContext? requestContext = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string targetType = GatewayJsonExtensions.GetStringProperty(body, "target_type", GatewayJsonExtensions.GetStringProperty(body, "targetType", "agent"));
        string targetId = GatewayJsonExtensions.GetStringProperty(body, "target_id", GatewayJsonExtensions.GetStringProperty(body, "targetId", ""));
        string message = GatewayJsonExtensions.GetStringProperty(body, "message", "").Trim();
        string sessionId = GatewayJsonExtensions.GetStringProperty(body, "session_id", GatewayJsonExtensions.GetStringProperty(body, "sessionId", "")).Trim();
        string? userId = GatewayJsonExtensions.GetStringProperty(body, "user_id", GatewayJsonExtensions.GetStringProperty(body, "userId", "")).Trim() is var parsedUserId
            && !string.IsNullOrWhiteSpace(parsedUserId)
            ? parsedUserId
            : null;
        string? source = body.TryGetProperty("source", out JsonElement sourceEl) && sourceEl.ValueKind == JsonValueKind.String ? sourceEl.GetString() : null;
        bool allowMemoryWrites = string.Equals(source, "workflow_test", StringComparison.OrdinalIgnoreCase)
            ? false
            : !string.Equals(source, "workflow", StringComparison.OrdinalIgnoreCase)
                || ReadBooleanProperty(body, "memoryWrites")
                || ReadBooleanProperty(body, "memory_writes");
        string? requestedRunId = GatewayJsonExtensions.GetStringProperty(body, "run_id", GatewayJsonExtensions.GetStringProperty(body, "runId", "")).Trim() is var parsedRunId
            && !string.IsNullOrWhiteSpace(parsedRunId)
            ? parsedRunId
            : null;
        string? requestedUserMessageId = GatewayJsonExtensions.GetStringProperty(body, "user_message_id", GatewayJsonExtensions.GetStringProperty(body, "userMessageId", "")).Trim() is var parsedUserMessageId
            && !string.IsNullOrWhiteSpace(parsedUserMessageId)
            ? parsedUserMessageId
            : null;
        bool earlyLifecyclePublished = ReadBooleanProperty(body, "early_lifecycle_published")
            || ReadBooleanProperty(body, "earlyLifecyclePublished");
        IReadOnlyList<string> assetIds = NaradRequestParsing.ExtractAssetIds(body);
        IReadOnlyList<string> workspaceFilePaths = NaradRequestParsing.ExtractWorkspaceFilePaths(body);

        GatewayUser? user = null;
        if (workspaceFilePaths.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(userId) || !userStore.TryGetUserById(userId, out user) || user is null)
            {
                throw new InvalidOperationException("Workspace file references require an authenticated owner.");
            }
        }

        if (string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            && catalogStore.TryGetAgent(targetId, out GatewayAgent? targetAgent)
            && targetAgent is not null
            && !GatewayOwnershipAccess.CallerCanUse(userId, targetAgent.Scope, targetAgent.OwnerUserId))
        {
            throw new InvalidOperationException("Agent is not available to this caller.");
        }

        GatewaySession session = string.IsNullOrWhiteSpace(sessionId)
            ? await sessionLifecycleService.CreateSessionAsync(targetType, targetId, userId ?? string.Empty, cancellationToken, source).ConfigureAwait(false)
            : await sessionLifecycleService.EnsureSessionAsync(targetType, targetId, sessionId, userId ?? string.Empty, cancellationToken, source).ConfigureAwait(false);
        using IDisposable executionLease = await SessionExecutionGate.EnterAsync($"{targetId}:{session.Id}", cancellationToken).ConfigureAwait(false);
        if (source?.Trim().ToUpperInvariant() is "TELEGRAM" or "SLACK" or "CHANNEL" or "NARAD" or "BROWSER"
            && state.AgentDefinitionStore.HasActiveRunForSession(state.AgentDefinitionStore.GetStorageLocatorKey(targetId), session.Id))
            throw new ChannelSessionBusyException();
        GatewayConversationAsset[] inputAssets = assetStore.GetAssetsByIds(session.Id, assetIds, userId)
            .Select(static asset => asset.Public)
            .ToArray();
        AgentWorkspaceFileReference[] workspaceFiles = ResolveWorkspaceFiles(targetId, user, workspaceFilePaths);

        // /compact shortcut: inline the response without entering the agent loop
        if (message.StartsWith("/compact", StringComparison.OrdinalIgnoreCase))
        {
            const string compactReply = "Session compacted. Future replies will use the compact summary context.";
            session = await sessionCompactionService.CompactAsync(session.Id, "manual", cancellationToken).ConfigureAwait(false) ?? session;
            GatewayChatResult compactResult = new GatewayChatResult(targetType, targetId, session.Id, compactReply, true, new GatewayMetrics(0, 0, 0), null, null, null, null, null);
            PersistMessages(session.Id, message, compactResult);
            string compactRunId = requestedRunId ?? Guid.NewGuid().ToString("N");
            NaradEventContext compactContext = new(
                targetId,
                session.Id,
                userId ?? string.Empty,
                compactRunId,
                compactRunId,
                SourceChannel: string.IsNullOrWhiteSpace(source) ? "chat" : source);
            yield return await PublishCompactAsync(NaradEventTypes.RunStarted, JsonSerializer.SerializeToElement(new JsonObject
            {
                ["trigger"] = "compact",
            }, KautukaJsonContext.Default.JsonObject),
                compactContext,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            yield return await PublishCompactAsync(NaradEventTypes.MessageUserRecorded, NaradEventBodies.MessageUserRecorded(NaradEventBodies.BuildUserMessageId(compactContext.RootRunId, compactContext.RunId), message),
                compactContext,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string messageId = NaradEventBodies.BuildAssistantMessageId(compactContext.RootRunId, compactContext.RunId);
            yield return await PublishCompactAsync(NaradEventTypes.MessageTextStarted, NaradEventBodies.MessageTextStarted(messageId),
                compactContext,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (string chunk in ChunkText(compactResult.Response, 90))
            {
                yield return await PublishCompactAsync(NaradEventTypes.MessageTextDelta, JsonSerializer.SerializeToElement(new GatewayChatTokenDelta(chunk, false), KautukaJsonContext.Default.GatewayChatTokenDelta),
                    compactContext,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            yield return await PublishCompactAsync(NaradEventTypes.MessageTextCompleted, JsonSerializer.SerializeToElement(new JsonObject
            {
                ["messageId"] = messageId,
            }, KautukaJsonContext.Default.JsonObject),
                compactContext,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            yield return await PublishCompactAsync(NaradEventTypes.RunFinished, JsonSerializer.SerializeToElement(new GatewayChatStreamDone(compactResult), KautukaJsonContext.Default.GatewayChatStreamDone),
                compactContext,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            yield break;
        }

        string? stageTransitionId = GatewayJsonExtensions.GetStringProperty(body, "stage_transition_id", "").Trim() is var sti2 && string.IsNullOrWhiteSpace(sti2) ? null : sti2;
        session = await TryAutoCompactSessionAsync(session, targetType, targetId, message, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InjectedMemoryFact> memoryFacts = await memoryAssembler.AssembleAsync(targetType, targetId, message, userId, cancellationToken).ConfigureAwait(false);
        memoryFacts = AppendCompactionContext(memoryFacts, session);

        ChatGatewayServiceLog.ExecutingStreamChat(logger, memoryFacts.Count, targetType, targetId);

        AgentRequestContext? effectiveRequestContext = requestedRunId is null
            && requestedUserMessageId is null
            && !earlyLifecyclePublished
            ? requestContext
            : (requestContext ?? new AgentRequestContext()) with
            {
                RunId = requestedRunId ?? requestContext?.RunId,
                UserMessageId = requestedUserMessageId ?? requestContext?.UserMessageId,
                EarlyLifecycleEventsPublished = earlyLifecyclePublished || requestContext?.EarlyLifecycleEventsPublished == true,
            };
        IAsyncEnumerable<NaradPersistedEvent> stream = targetType == "agent"
            ? agentChatService.StreamAsync(targetId, session.Id, userId, string.IsNullOrWhiteSpace(source) ? "chat" : source, message, stageTransitionId, inputAssets, workspaceFiles, memoryFacts, null, null, effectiveRequestContext, cancellationToken)
            : agentChatService.StreamAsync(targetId, session.Id, userId, string.IsNullOrWhiteSpace(source) ? "chat" : source, message, stageTransitionId, inputAssets, workspaceFiles, memoryFacts, null, null, effectiveRequestContext, cancellationToken);

        GatewayChatApprovalRequest? latestApprovalRequest = null;
        var pausedOpenToolCalls = new List<GatewayToolCall>();
        bool persistedTurnUsage = false;
        string resolvedSource = string.IsNullOrWhiteSpace(source) ? "chat" : source;

        await foreach (NaradPersistedEvent evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            TryRecordPausedToolCallStarted(evt, targetId, pausedOpenToolCalls);
            if (TryEnqueueUsageTurnFromProgress(evt, targetType, targetId, session.Id, userId, resolvedSource))
            {
                persistedTurnUsage = true;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
            {
                GatewayChatApprovalRequest approvalRequest = NaradInterruptObservation.ResolveChatApprovalRequest(
                    evt,
                    approvalStore,
                    fallbackAgentId: targetId,
                    fallbackSessionId: session.Id);
                latestApprovalRequest = approvalRequest;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
            {
                GatewayChatStreamDone done = ResolveStreamTerminalDone(evt, targetType, targetId, session.Id);
                if (string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal)
                    && (done.Result.ToolCalls is null || done.Result.ToolCalls.Count == 0)
                    && pausedOpenToolCalls.Count > 0)
                {
                    done = new GatewayChatStreamDone(done.Result with { ToolCalls = pausedOpenToolCalls.ToArray() });
                }

                string persistedUserMessage = BuildPersistedUserMessage(message, inputAssets);
                if (string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
                {
                    PersistApprovalPausedRun(
                        session.Id,
                        persistedUserMessage,
                        done.Result,
                        latestApprovalRequest,
                        assetIds,
                        userId,
                        runId: null,
                        source: resolvedSource,
                        enqueueUsage: !persistedTurnUsage);
                }
                else
                {
                    PersistCompletedRun(
                        session.Id,
                        persistedUserMessage,
                        done.Result,
                        assetIds,
                        userId,
                        runId: null,
                        source: resolvedSource,
                        enqueueUsage: !persistedTurnUsage,
                        queueFacts: allowMemoryWrites);
                }
            }
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using IDisposable executionLease = await SessionExecutionGate.EnterAsync($"{targetId}:{sessionId}", cancellationToken).ConfigureAwait(false);
        ToolApprovalResponseInfo continuationApprovalResponse = approvalResponse with
        {
            Reason = SanitizeApprovalContinuationReason(approvalResponse.Reason),
        };

        IReadOnlyList<InjectedMemoryFact> memoryFacts = await memoryAssembler.AssembleAsync(targetType, targetId, continuationApprovalResponse.Reason, userId, cancellationToken).ConfigureAwait(false);
        if (sessions.TryGetSession(sessionId, out GatewaySession? continuationSession) && continuationSession is not null)
        {
            memoryFacts = AppendCompactionContext(memoryFacts, continuationSession);
        }

        AgentContinuationRequest? continuationRequest = null;
        RunWaitState? waitState = null;
        AgentRequestContext? requestContext = null;
        continuationApprovalResponse = ResolveApprovalContinuationResponse(continuationApprovalResponse, out waitState);
        if (state.AgentDefinitionStore.HasActiveRunForSession(state.AgentDefinitionStore.GetStorageLocatorKey(targetId), sessionId,
                waitState?.RootRunId ?? continuationApprovalResponse.RootRunId ?? continuationApprovalResponse.RunId))
            throw new InvalidOperationException("Approval continuation is blocked because another run is active in this session. Stop the overlapping runs before resuming.");
        if (waitState is not null)
        {
            requestContext = AgentRunHostCallTracer.CreateRequestContext(waitState.TraceParent, waitState.TraceState);
            IReadOnlyList<ContinuationArtifactManifestEntry>? artifactManifest = string.IsNullOrWhiteSpace(waitState.ArtifactManifestJson)
                ? null
                : JsonSerializer.Deserialize(waitState.ArtifactManifestJson, KautukaJsonContext.Default.ListContinuationArtifactManifestEntry);

            (string behaviorJson, TimeSpan activeElapsed) = RunClockEnvelope.Unwrap(waitState.BehaviorJson);

            continuationRequest = new AgentContinuationRequest(
                Enabled: true,
                ResumeToken: null,
                ContinuationRunId: waitState.ContinuationRunId,
                CheckpointId: null,
                CheckpointIndex: null,
                // Always resume from the live MAF session (null override), including invoke_agent_*.
                // Wait-state session snapshots are not used for approval continuation.
                SessionStateJson: null,
                EffectiveBehaviorJson: behaviorJson,
                BoundaryType: waitState.WaitReason,
                ArtifactManifest: artifactManifest,
                PriorToolCalls: continuationSession is null ? null : FindLatestSessionToolCalls(continuationSession),
                ActiveElapsed: activeElapsed);
        }

        string originalUserMessage = RunUserMessageResolver.Resolve(state.AgentDefinitionStore, sessions,
            targetId, sessionId, waitState?.RunId ?? continuationApprovalResponse.RunId, continuationApprovalResponse.ToolCallId) ?? string.Empty;
        IAsyncEnumerable<NaradPersistedEvent> stream =
            agentChatService.StreamAsync(targetId, sessionId, userId, "hitl", originalUserMessage, null, [], [], memoryFacts, continuationApprovalResponse, continuationRequest, requestContext, cancellationToken);

        bool persistedTurnUsage = false;
        await foreach (NaradPersistedEvent evt in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (TryEnqueueUsageTurnFromProgress(evt, targetType, targetId, sessionId, userId, "hitl"))
            {
                persistedTurnUsage = true;
            }

            if (string.Equals(evt.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal)
                || string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
            {
                if (beforeWaitTerminal is not null)
                {
                    await beforeWaitTerminal(evt, cancellationToken).ConfigureAwait(false);
                }

                GatewayChatStreamDone done = ResolveStreamTerminalDone(evt, targetType, targetId, sessionId);

                // Continuation already owns the original user turn from the pause persist. Append
                // only the assistant final (+ run/usage). Persisting another user/assistant pair
                // here re-surfaces the same answer as a second bubble after resume.
                PersistAssistantStateOnly(sessionId, done.Result, userId);
                GatewayRun continuationRun = sessions.AppendRun(
                    done.Result.TargetType,
                    done.Result.TargetId,
                    userId ?? string.Empty,
                    sessionId,
                    string.Empty,
                    done.Result.Response,
                    done.Result.Metrics,
                    done.Result.EffectiveBehavior,
                    runId: null,
                    traceId: done.Result.TraceId);
                if (!persistedTurnUsage)
                {
                    EnqueueUsageFact(continuationRun, done.Result, userId, "hitl");
                }
                if (!string.IsNullOrWhiteSpace(done.Result.Response))
                {
                    QueueFactExtraction(
                        done.Result.TargetType,
                        done.Result.TargetId,
                        sessionId,
                        originalUserMessage,
                        done.Result.Response);
                }

                if (waitState is not null)
                {
                    // The old wait remains active while the continuation is executing. Closing it
                    // on RunResumed creates an unrecoverable gap if the process exits before the
                    // continuation reaches either a new pause or a terminal result.
                    if (string.Equals(evt.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal))
                    {
                        runWaitStateStore.MarkPausedAgain(waitState.WaitId);
                    }
                    else if (string.Equals(evt.EventType, NaradEventTypes.RunError, StringComparison.Ordinal))
                    {
                        runWaitStateStore.MarkFailed(waitState.WaitId, "continuation_run_failed");
                    }
                    else if (HasActiveChildPauseForApprovalContinuation(
                        continuationApprovalResponse.ToolCallId,
                        done.Result.Run?.Phase,
                        done.Result.Run?.WaitStateReason))
                    {
                        // Nested child HITL keeps the parent invoke_agent_* tool open on the wire.
                        // Release the claimed outer approval wait so inner approvals can resume lineage.
                        runWaitStateStore.ReleaseExecutingWait(waitState.WaitId);
                    }
                    else
                    {
                        runWaitStateStore.MarkCompleted(waitState.WaitId);
                    }
                }
            }
            yield return evt;
        }

        if (waitState is not null
            && !string.IsNullOrWhiteSpace(continuationApprovalResponse.ToolCallId)
            && runWaitStateStore.GetActiveChildPauseForCaller(sessionId, continuationApprovalResponse.ToolCallId) is not null)
        {
            // AAAT nested HITL pauses only the child run on the wire; release the claimed outer
            // approval wait when durable child lineage exists without a parent run.paused event.
            runWaitStateStore.ReleaseExecutingWait(waitState.WaitId);
        }
    }

    private static bool HasActiveChildPauseForApprovalContinuation(
        string? toolCallId,
        string? terminalPhase,
        string? waitStateReason)
        => !string.IsNullOrWhiteSpace(toolCallId)
            && string.Equals(terminalPhase, GatewayRunPhases.Executing, StringComparison.Ordinal)
            && string.Equals(waitStateReason, RunWaitReasons.Child, StringComparison.Ordinal);

    public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
        => PersistCompletedRun(
            sessionId,
            userMessage,
            result,
            userAssetIds,
            userId,
            runId,
            string.IsNullOrWhiteSpace(sourceChannel) ? "chat" : sourceChannel);

    private static T ReadPayload<T>(NaradPersistedEvent persistedEvent, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => NaradStreamObservation.ReadBodyPayload(persistedEvent, typeInfo);

    public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || UserMessageAlreadyRecorded(sessionId, userMessage))
        {
            return;
        }

        PersistUserMessage(
            sessionId,
            userMessage,
            string.IsNullOrWhiteSpace(sourceChannel) ? "chat" : sourceChannel,
            userAssetIds,
            userId);
    }

    public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
        => PersistAssistantStateOnly(sessionId, ChatExecutionHelpers.SummarizePersistedChatResult(result), userId);

    private static string? SanitizeApprovalContinuationReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return reason;
        }

        return reason.Trim().ToUpperInvariant() switch
        {
            "CHAT-UI" => null,
            "CHAT UI" => null,
            "APPROVALS-PANE" => null,
            "APPROVALS PANE" => null,
            "RUN-STOPPED" => null,
            _ => reason,
        };
    }

    private AgentWorkspaceFileReference[] ResolveWorkspaceFiles(
        string agentId,
        GatewayUser? user,
        IReadOnlyList<string> workspaceFilePaths)
    {
        if (workspaceFilePaths.Count == 0)
        {
            return [];
        }

        if (user is null)
        {
            throw new InvalidOperationException("Workspace file references require an authenticated owner.");
        }

        List<AgentWorkspaceFileReference> resolved = [];
        foreach (string path in workspaceFilePaths)
        {
            AgentFilePathAuthorizationResult? authorization = null;
            foreach (string candidate in NaradRequestParsing.GetWorkspaceFileCandidateVariants(path))
            {
                AgentFilePathAuthorizationResult candidateAuthorization = agentFilePathAuthorizationService.Authorize(agentId, user, candidate);
                if (candidateAuthorization.Status is AgentFileOperationStatus.Success
                    && File.Exists(candidateAuthorization.ResolvedPath))
                {
                    authorization = candidateAuthorization;
                    break;
                }
            }

            if (authorization is null)
            {
                throw new FileNotFoundException(
                    $"Workspace file reference '{path}' was not found or is invalid.",
                    path);
            }

            resolved.Add(AgentWorkspaceFileReference.FromAuthorizedPath(authorization.RelativePath, authorization.ResolvedPath));
        }

        return [.. resolved];
    }

    private void PersistCompletedRun(
        string sessionId,
        string userMessage,
        GatewayChatResult result,
        IReadOnlyList<string>? userAssetIds,
        string? userId,
        string? runId,
        string? source,
        bool enqueueUsage = true,
        bool queueFacts = true)
    {
        PersistMessages(sessionId, userMessage, result, source, userAssetIds, userId);
        GatewayRun run = sessions.AppendRun(result.TargetType, result.TargetId, userId ?? string.Empty, sessionId, userMessage, result.Response, result.Metrics, result.EffectiveBehavior, runId, traceId: result.TraceId);
        if (enqueueUsage)
        {
            EnqueueUsageFact(run, result, userId, source);
        }
        if (queueFacts)
        {
            QueueFactExtraction(result.TargetType, result.TargetId, sessionId, userMessage, result.Response);
        }
    }

    private static IReadOnlyList<GatewayToolCall>? FindLatestSessionToolCalls(GatewaySession session)
    {
        for (int index = session.Messages.Count - 1; index >= 0; index--)
        {
            GatewayMessage message = session.Messages[index];
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                return toolCalls;
            }
        }

        return null;
    }

    private bool TryEnqueueUsageTurnFromProgress(
        NaradPersistedEvent persistedEvent,
        string targetType,
        string targetId,
        string sessionId,
        string? userId,
        string? sourceChannel)
    {
        if (!string.Equals(persistedEvent.EventType, NaradEventTypes.ProgressRecorded, StringComparison.Ordinal))
        {
            return false;
        }

        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        string? category = NaradPersistedEventReader.String(body, "category");
        string? status = NaradPersistedEventReader.String(body, "status");
        if (!string.Equals(category, NaradProgressCategories.AgentTurn, StringComparison.Ordinal)
            || string.Equals(status, "active", StringComparison.Ordinal))
        {
            return false;
        }

        if (!NaradPersistedEventReader.TryGetUsage(persistedEvent, out long promptTokens, out long completionTokens))
        {
            return false;
        }

        DateTimeOffset at = DateTimeOffset.TryParse(persistedEvent.OccurredAt, out DateTimeOffset parsedAt)
            ? parsedAt
            : DateTimeOffset.UtcNow;
        EnqueueUsageFactCore(
            runId: $"run-{Guid.NewGuid():N}",
            sessionId,
            targetType,
            targetId,
            userId,
            sourceChannel,
            runStatus: status ?? "completed",
            startedAt: at,
            endedAt: at,
            durationMs: 0,
            firstModelOutputAt: null,
            tokenIn: promptTokens > 0 ? (int)promptTokens : null,
            tokenOut: completionTokens > 0 ? (int)completionTokens : null,
            providerRequestCount: 1,
            modelTurnCount: 1,
            assistantTurnCount: 1,
            toolCallCount: null);
        return true;
    }

    private void EnqueueUsageFact(GatewayRun run, GatewayChatResult result, string? userId, string? sourceChannel)
    {
        DateTimeOffset endedAt = DateTimeOffset.TryParse(run.EndedAt, out DateTimeOffset parsedEndedAt)
            ? parsedEndedAt
            : DateTimeOffset.UtcNow;
        DateTimeOffset startedAt = endedAt.AddMilliseconds(-Math.Max(0, result.Metrics.ElapsedMs));
        string? firstModelOutputAt = result.Usage?.FirstModelOutputAt;

        if (result.UsageTurns is { Count: > 0 } turns)
        {
            for (int index = 0; index < turns.Count; index++)
            {
                GatewayUsageTurn turn = turns[index];
                bool last = index == turns.Count - 1;
                int? tokenIn = turn.TokenIn > 0 ? turn.TokenIn : null;
                int? tokenOut = turn.TokenOut > 0 ? turn.TokenOut : null;
                EnqueueUsageFactCore(
                    $"run-{Guid.NewGuid():N}",
                    run.SessionId,
                    run.TargetType,
                    run.TargetId,
                    userId,
                    sourceChannel,
                    run.Status,
                    startedAt,
                    endedAt,
                    last ? Math.Max(0, result.Metrics.ElapsedMs) : 0,
                    index == 0 ? firstModelOutputAt : null,
                    tokenIn,
                    tokenOut,
                    1,
                    1,
                    last ? 1 : null,
                    last ? result.ToolCalls?.Count : null);
            }

            return;
        }

        int? aggregateTokenIn = result.Metrics.TokenIn > 0 ? result.Metrics.TokenIn : null;
        int? aggregateTokenOut = result.Metrics.TokenOut > 0 ? result.Metrics.TokenOut : null;
        int? providerRequestCount = result.Usage?.ProviderRequestCount;
        EnqueueUsageFactCore(
            run.Id,
            run.SessionId,
            run.TargetType,
            run.TargetId,
            userId,
            sourceChannel,
            run.Status,
            startedAt,
            endedAt,
            Math.Max(0, result.Metrics.ElapsedMs),
            firstModelOutputAt,
            aggregateTokenIn,
            aggregateTokenOut,
            providerRequestCount,
            ResolveModelTurnCount(providerRequestCount),
            1,
            result.ToolCalls?.Count);
    }

    private void EnqueueUsageFactCore(
        string runId,
        string? sessionId,
        string targetType,
        string targetId,
        string? userId,
        string? sourceChannel,
        string runStatus,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        int durationMs,
        string? firstModelOutputAt,
        int? tokenIn,
        int? tokenOut,
        int? providerRequestCount,
        int? modelTurnCount,
        int? assistantTurnCount,
        int? toolCallCount)
    {
        GatewayUser? user = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            _ = userStore.TryGetUserById(userId, out user);
        }

        GatewayAgent? agent = null;
        GatewayModel? model = null;
        GatewayProvider? provider = null;

        if (string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            && catalogStore.TryGetAgent(targetId, out GatewayAgent? resolvedAgent))
        {
            agent = resolvedAgent;
            string? agentModelId = resolvedAgent?.ModelId;
            if (!string.IsNullOrWhiteSpace(agentModelId)
                && catalogStore.TryGetModel(agentModelId, out GatewayModel? resolvedModel))
            {
                model = resolvedModel;
                string? modelProviderId = resolvedModel?.ProviderId;
                if (!string.IsNullOrWhiteSpace(modelProviderId))
                {
                    _ = catalogStore.TryGetProvider(modelProviderId, out provider);
                }
            }
        }

        int? totalTokens = tokenIn.HasValue || tokenOut.HasValue
            ? (tokenIn ?? 0) + (tokenOut ?? 0)
            : null;
        int? firstTokenLatencyMs = null;
        if (!string.IsNullOrWhiteSpace(firstModelOutputAt)
            && DateTimeOffset.TryParse(firstModelOutputAt, out DateTimeOffset parsedFirstModelOutputAt))
        {
            firstTokenLatencyMs = Math.Max(0, (int)(parsedFirstModelOutputAt - startedAt).TotalMilliseconds);
        }

        var fact = new GatewayUsageFact(
            $"usage-{Guid.NewGuid():N}",
            1,
            runId,
            sessionId,
            targetType,
            targetId,
            string.IsNullOrWhiteSpace(userId) ? null : userId,
            user?.Username,
            user?.DisplayName,
            agent?.Id,
            agent?.Name,
            model?.Id,
            model?.ModelName,
            provider?.Id,
            provider?.Name,
            runStatus,
            string.IsNullOrWhiteSpace(sourceChannel) ? null : sourceChannel,
            startedAt.ToString("O"),
            firstModelOutputAt,
            endedAt.ToString("O"),
            durationMs,
            firstTokenLatencyMs,
            tokenIn,
            tokenOut,
            totalTokens,
            providerRequestCount,
            modelTurnCount,
            assistantTurnCount,
            toolCallCount,
            "none",
            null,
            null,
            null,
            null,
            null,
            startedAt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow.ToString("O"));

        if (!usageQueue.TryEnqueue(fact))
        {
            ChatGatewayServiceLog.UsageQueueDropped(logger, runId);
        }
    }

    // Model turns track generation rounds. If low-level provider telemetry is unavailable,
    // completed runs still represent at least one model generation round.
    private static int ResolveModelTurnCount(int? providerRequestCount)
        => providerRequestCount is > 0 ? providerRequestCount.Value : 1;

    private static string BuildPersistedUserMessage(string message, GatewayConversationAsset[] inputAssets)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (inputAssets.Length == 0)
        {
            return string.Empty;
        }

        string label = inputAssets.Length == 1
            ? inputAssets[0].FileName
            : $"{inputAssets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} attachments";

        return $"Shared {label}.";
    }
}
