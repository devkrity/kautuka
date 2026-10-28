using System.Globalization;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sessions;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewaySessionStore : IGatewaySessionStore
{
    private const int sessionMutationStripeCount = 257;

    private readonly GatewayState state;
    private readonly IGatewayContinuationStore continuationStore;
    private readonly IConversationAssetStore assetStore;
    private readonly IAgentMemoryEmbeddingQueue memoryEmbeddingQueue;
    private readonly ISessionTitleService sessionTitleService;
    private readonly IPlatformConfigurationProvider? configProvider;
    private readonly IAgentToolInvocationLinkStore? invocationLinkStore;
    private readonly ILogger<GatewaySessionStore> logger;
    private readonly object[] sessionMutationGates = CreateSessionMutationGates();

    public GatewaySessionStore(
        GatewayState state,
        IGatewayContinuationStore continuationStore,
        IConversationAssetStore assetStore,
        IAgentMemoryEmbeddingQueue memoryEmbeddingQueue,
        ISessionTitleService sessionTitleService,
        ILogger<GatewaySessionStore> logger,
        IPlatformConfigurationProvider? configProvider = null,
        IAgentToolInvocationLinkStore? invocationLinkStore = null)
    {
        this.state = state;
        this.continuationStore = continuationStore;
        this.assetStore = assetStore;
        this.memoryEmbeddingQueue = memoryEmbeddingQueue;
        this.sessionTitleService = sessionTitleService;
        this.configProvider = configProvider;
        this.invocationLinkStore = invocationLinkStore;
        this.logger = logger;
    }

    public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null)
    {
        EnsureAgentSessionTarget(targetType, targetId);
        string now = Timestamp();
        var session = new GatewaySession(
            $"session-{Guid.NewGuid():N}",
            targetType,
            targetId,
            ownerUserId,
            sessionTitleService.InitialTitle(originator),
            now,
            now,
            [],
            false,
            null,
            EstimatedContextTokens: SessionContextEstimator.EstimateTokens([], GetInjectionTokenBudget()),
            RawTranscriptEstimatedTokens: SessionContextEstimator.EstimateTokens([], GetInjectionTokenBudget()),
            LastEstimatedAt: now,
            Originator: NormalizeOriginator(originator));

        state.CreateAgentRuntimeSession(session);
        state.Sessions[session.Id] = session;
        return HydrateSessionAssets(session);
    }

    public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) && existing is not null)
        {
            bool sameTargetType = string.Equals(existing.TargetType, targetType, StringComparison.OrdinalIgnoreCase);
            bool sameTargetId = string.Equals(existing.TargetId, targetId, StringComparison.OrdinalIgnoreCase);
            bool sameOwnerUserId = string.Equals(existing.OwnerUserId, ownerUserId, StringComparison.Ordinal);

            if (sameTargetType && sameTargetId && sameOwnerUserId)
            {
                return HydrateSessionAssets(existing);
            }

            GatewaySessionStoreLog.CrossTargetSessionReuseRejected(
                logger,
                sessionId,
                existing.TargetType,
                existing.TargetId,
                targetType,
                targetId);
        }

        return CreateSession(targetType, targetId, ownerUserId, originator);
    }

    public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) =>
        state.Sessions.Values
            .Where(item => string.Equals(item.TargetType, targetType, StringComparison.Ordinal)
                && string.Equals(item.TargetId, targetId, StringComparison.Ordinal)
                && string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal)
                && !IsWorkflowTestSession(item)
                && (includeArchived || string.IsNullOrWhiteSpace(item.ArchivedAt)))
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Select(HydrateSessionAssets)
            .ToList();

    public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) =>
        state.Sessions.Values
            .Where(item => string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal) && !IsWorkflowTestSession(item))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .Select(HydrateSessionAssets)
            .ToList();

    public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId)
    {
        int boundedRetainCount = Math.Max(1, retainCount);
        List<GatewaySession> matchingSessions = state.Sessions.Values
            .Where(item => string.Equals(item.TargetType, targetType, StringComparison.Ordinal)
                && string.Equals(item.TargetId, targetId, StringComparison.Ordinal)
                && string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal)
                && !IsWorkflowTestSession(item))
            .OrderByDescending(item => string.Equals(item.Id, preserveSessionId, StringComparison.Ordinal))
            .ThenByDescending(item => item.CreatedAt)
            .ToList();

        if (matchingSessions.Count <= boundedRetainCount)
        {
            return [];
        }

        List<string> removedSessionIds = [];
        foreach (GatewaySession session in matchingSessions.Skip(boundedRetainCount))
        {
            if (invocationLinkStore?.HasLinksForSession(session.Id) == true)
            {
                continue;
            }

            if (DeleteSession(session.Id))
            {
                removedSessionIds.Add(session.Id);
            }
        }

        return removedSessionIds;
    }

    private static bool IsWorkflowTestSession(GatewaySession session) =>
        string.Equals(session.Originator, "workflow_test", StringComparison.OrdinalIgnoreCase);

    public GatewaySession? UpdateSessionTitle(string sessionId, string title)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                return null;
            }

            string normalizedTitle = title.Trim();
            if (string.IsNullOrWhiteSpace(normalizedTitle))
            {
                return null;
            }

            string now = Timestamp();
            var updated = existing with
            {
                Title = normalizedTitle,
                TitleUserSet = true,
                UpdatedAt = now,
            };
            state.UpdateAgentRuntimeSession(updated);
            state.Sessions[sessionId] = updated;
            return HydrateSessionAssets(updated);
        }
    }

    public GatewaySession? ArchiveSession(string sessionId)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(existing.ArchivedAt))
            {
                return HydrateSessionAssets(existing);
            }

            string now = Timestamp();
            var updated = existing with
            {
                ArchivedAt = now,
                UpdatedAt = now,
            };
            state.UpdateAgentRuntimeSession(updated);
            state.Sessions[sessionId] = updated;
            return HydrateSessionAssets(updated);
        }
    }

    public bool TryGetSession(string sessionId, out GatewaySession? session)
    {
        if (state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) && existing is not null)
        {
            session = HydrateSessionAssets(existing);
            return true;
        }

        session = null;
        return false;
    }

    public bool DeleteSession(string sessionId)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (invocationLinkStore?.HasLinksForSession(sessionId) == true)
            {
                return false;
            }

            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                return false;
            }

            if (!state.DeleteAgentRuntimeSession(existing))
            {
                return false;
            }
            _ = state.Sessions.TryRemove(sessionId, out _);
            continuationStore.DeleteBySessions([sessionId]);
            assetStore.DeleteSessionAssets([sessionId]);
            return true;
        }
    }

    public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                return null;
            }

            string now = Timestamp();
            string normalizedSummary = string.IsNullOrWhiteSpace(summary) ? $"Compacted at {now}" : summary.Trim();
            var compacted = existing with
            {
                Compacted = true,
                CompactSummary = normalizedSummary,
                UpdatedAt = now,
                LastCompactedAt = now,
                LastCompactionReason = string.IsNullOrWhiteSpace(reason) ? "manual" : reason.Trim(),
            };
            compacted = RefreshSessionEstimate(compacted);
            state.UpdateAgentRuntimeSession(compacted);
            state.Sessions[sessionId] = compacted;
            return HydrateSessionAssets(compacted);
        }
    }

    public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                GatewaySessionStoreLog.AppendMessagesMissingSession(logger, sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' not found.");
            }

            existing = ValidateSessionMessageSequence(existing);
            if (ContainsMessage(existing.Messages, userMessage.Id))
            {
                return HydrateSessionAssets(existing);
            }

            GatewayMessage sequencedUserMessage = AssignSequence(userMessage, existing.Messages);
            var updatedMessages = existing.Messages.Append(sequencedUserMessage).ToList();
            string resolvedTitle = ResolveSessionTitle(existing, updatedMessages);

            var updated = existing with
            {
                Title = resolvedTitle,
                UpdatedAt = Timestamp(),
                Messages = updatedMessages,
            };

            updated = RefreshSessionEstimate(updated);

            CommitSessionAppend(updated, [sequencedUserMessage]);

            if (userAssetIds is { Count: > 0 })
            {
                assetStore.BindAssetsToMessage(sessionId, sequencedUserMessage.Id, userAssetIds, userId);
            }

            return HydrateSessionAssets(updated);
        }
    }

    public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                GatewaySessionStoreLog.AppendMessagesMissingSession(logger, sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' not found.");
            }

            existing = ValidateSessionMessageSequence(existing);
            if (ContainsMessage(existing.Messages, assistantMessage.Id))
            {
                return HydrateSessionAssets(existing);
            }

            GatewayMessage sequencedAssistantMessage = AssignSequence(assistantMessage, existing.Messages);
            var updatedMessages = existing.Messages.Append(sequencedAssistantMessage).ToList();
            string resolvedTitle = ResolveSessionTitle(existing, updatedMessages);

            var updated = existing with
            {
                Title = resolvedTitle,
                UpdatedAt = Timestamp(),
                Messages = updatedMessages,
            };

            updated = RefreshSessionEstimate(updated);

            CommitSessionAppend(updated, [sequencedAssistantMessage]);

            if (sequencedAssistantMessage.Assets is { Count: > 0 })
            {
                assetStore.BindAssetsToMessage(sessionId, sequencedAssistantMessage.Id, sequencedAssistantMessage.Assets.Select(static asset => asset.Id).ToArray(), userId);
            }

            return HydrateSessionAssets(updated);
        }
    }

    public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId)
    {
        IReadOnlyList<GatewaySession> sessions = ListSessions(targetType, targetId, ownerUserId);
        IReadOnlyList<GatewayMemoryFact> durableFacts = state.MemoryStore.List(targetType, targetId)
            .Where(item => string.Equals(item.OwnerUserId, ownerUserId, StringComparison.Ordinal))
            .ToArray();

        return new GatewayMemoriesResponse(
            [.. sessions],
            [.. durableFacts.Select(item => new GatewayMemoryFactPayload(
                item.Id,
                item.Title,
                item.Content,
                item.Category,
                item.Source,
                item.Confidence,
                item.CreatedAt,
                item.UpdatedAt,
                item.SessionId))]);
    }

    public bool DeleteMemoryFact(string factId)
    {
        if (!state.MemoryStore.TryGet(factId, out GatewayMemoryFact? existingFact) || existingFact is null)
        {
            return false;
        }

        if (!state.DeleteAgentMemoryFact(existingFact))
        {
            return false;
        }

        _ = state.MemoryStore.Remove(factId);
        EnqueueEmbeddingReconciliation(AgentMemoryEmbeddingOperation.Delete, existingFact);
        return true;
    }

    public bool UpdateMemoryFact(string factId, string title, string content, string category)
    {
        if (!state.MemoryStore.TryGet(factId, out GatewayMemoryFact? existing) || existing is null)
        {
            return false;
        }

        GatewayMemoryFact updated = existing with
        {
            Title = title,
            Content = content,
            Category = category,
            UpdatedAt = Timestamp(),
        };
        state.UpsertAgentMemoryFacts(updated.TargetType, updated.TargetId, [updated]);
        EnqueueEmbeddingReconciliation(AgentMemoryEmbeddingOperation.Persist, updated);
        return true;
    }

    public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
    {
        lock (GetSessionMutationGate(sessionId))
        {
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? existing) || existing is null)
            {
                GatewaySessionStoreLog.AppendMessagesMissingSession(logger, sessionId);
                throw new InvalidOperationException($"Session '{sessionId}' not found.");
            }

            existing = ValidateSessionMessageSequence(existing);

            List<GatewayMessage> updatedMessages = [.. existing.Messages];
            GatewayMessage? sequencedUserMessage = null;
            GatewayMessage? sequencedAssistantMessage = null;
            if (!ContainsMessage(updatedMessages, userMessage.Id))
            {
                sequencedUserMessage = AssignSequence(userMessage, updatedMessages);
                updatedMessages.Add(sequencedUserMessage);
            }

            if (!ContainsMessage(updatedMessages, assistantMessage.Id))
            {
                sequencedAssistantMessage = AssignSequence(assistantMessage, updatedMessages);
                updatedMessages.Add(sequencedAssistantMessage);
            }

            if (sequencedUserMessage is null && sequencedAssistantMessage is null)
            {
                return HydrateSessionAssets(existing);
            }

            string resolvedTitle = ResolveSessionTitle(existing, updatedMessages);

            var updated = existing with
            {
                Title = resolvedTitle,
                UpdatedAt = Timestamp(),
                Messages = updatedMessages,
            };

            updated = RefreshSessionEstimate(updated);

            CommitSessionAppend(
                updated,
                [.. new[] { sequencedUserMessage, sequencedAssistantMessage }.OfType<GatewayMessage>()]);

            if (sequencedUserMessage is not null && userAssetIds is { Count: > 0 })
            {
                assetStore.BindAssetsToMessage(sessionId, sequencedUserMessage.Id, userAssetIds, userId);
            }

            if (sequencedAssistantMessage?.Assets is { Count: > 0 })
            {
                assetStore.BindAssetsToMessage(sessionId, sequencedAssistantMessage.Id, sequencedAssistantMessage.Assets.Select(static asset => asset.Id).ToArray(), userId);
            }

            return HydrateSessionAssets(updated);
        }
    }

    private GatewaySession HydrateSessionAssets(GatewaySession session)
    {
        session = ValidateSessionMessageSequence(session);

        if (session.Messages.Count == 0)
        {
            return session;
        }

        IReadOnlyDictionary<string, IReadOnlyList<GatewayConversationAsset>> assetsByMessageId =
            assetStore.ListAssetsByMessage(session.Id, session.Messages.Select(static message => message.Id).ToArray());

        GatewayMessage[] hydratedMessages = session.Messages
            .Select(message => message with
            {
                Assets = assetsByMessageId.TryGetValue(message.Id, out IReadOnlyList<GatewayConversationAsset>? assets)
                    ? assets
                    : [],
            })
            .ToArray();

        return session with { Messages = hydratedMessages };
    }

    private static string? NormalizeOriginator(string? originator)
    {
        if (string.IsNullOrWhiteSpace(originator))
        {
            return "chat";
        }

        return originator.Trim().ToUpperInvariant() switch
        {
            "CHAT" => "chat",
            "AUTOPILOT" => "autopilot",
            "TELEGRAM" => "telegram",
            "HITL" => "hitl",
            var _ => originator.Trim(),
        };
    }

    private static GatewaySession ValidateSessionMessageSequence(GatewaySession session)
    {
        int previousSequence = 0;
        foreach (GatewayMessage message in session.Messages)
        {
            if (message.Sequence <= previousSequence)
            {
                throw new InvalidOperationException($"Session '{session.Id}' contains a non-increasing message sequence at message '{message.Id}'.");
            }

            previousSequence = message.Sequence;
        }

        return session;
    }

    private static GatewayMessage AssignSequence(GatewayMessage message, IReadOnlyList<GatewayMessage> existingMessages)
    {
        int nextSequence = existingMessages.Count == 0
            ? 1
            : checked(existingMessages.Max(static existingMessage => existingMessage.Sequence) + 1);
        return message with { Sequence = nextSequence };
    }

    private static bool ContainsMessage(IReadOnlyList<GatewayMessage> messages, string messageId)
        => messages.Any(message => string.Equals(message.Id, messageId, StringComparison.Ordinal));

    private void CommitSessionAppend(GatewaySession updated, IReadOnlyList<GatewayMessage> appendedMessages)
    {
        EnsureAgentSessionTarget(updated.TargetType, updated.TargetId);
        state.AppendAgentRuntimeSessionMessages(updated, appendedMessages);
        state.Sessions[updated.Id] = updated;
    }

    private object GetSessionMutationGate(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        int stripe = (StringComparer.Ordinal.GetHashCode(sessionId) & int.MaxValue) % sessionMutationGates.Length;
        return sessionMutationGates[stripe];
    }

    private static object[] CreateSessionMutationGates()
    {
        var gates = new object[sessionMutationStripeCount];
        for (int index = 0; index < gates.Length; index++)
        {
            gates[index] = new object();
        }

        return gates;
    }

    private static void EnsureAgentSessionTarget(string targetType, string targetId)
    {
        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Runtime sessions must target an agent.", nameof(targetType));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
    }

    private GatewaySession RefreshSessionEstimate(GatewaySession session)
    {
        string estimatedAt = Timestamp();
        int rawTranscriptEstimatedTokens = SessionContextEstimator.EstimateTokens(session.Messages, GetInjectionTokenBudget());
        int effectiveEstimatedTokens = session.Compacted
            ? SessionContextEstimator.EstimateCompactedTokens(session.CompactSummary, session.Messages, GetInjectionTokenBudget())
            : rawTranscriptEstimatedTokens;

        return session with
        {
            EstimatedContextTokens = effectiveEstimatedTokens,
            RawTranscriptEstimatedTokens = rawTranscriptEstimatedTokens,
            LastEstimatedAt = estimatedAt,
        };
    }

    private int GetInjectionTokenBudget()
        => Math.Max(0, configProvider?.GetCurrent().Memory.InjectionTokenBudget ?? 0);

    public IReadOnlyList<GatewayRun> ListRuns()
    {
        lock (state.RunLock)
        {
            return state.Runs.OrderByDescending(static run => run.StartedAt).ToList();
        }
    }

    public bool TryGetRun(string runId, out GatewayRun? run)
    {
        lock (state.RunLock)
        {
            run = state.Runs.FirstOrDefault(item => string.Equals(item.Id, runId, StringComparison.Ordinal));
            return run is not null;
        }
    }

    public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null)
    {
        EnsureAgentSessionTarget(targetType, targetId);
        string timestamp = Timestamp();
        string resolvedRunId = runId ?? $"run-{Guid.NewGuid():N}";
        string resolvedSessionId = sessionId ?? resolvedRunId;
        string resolvedAgentId = string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            ? targetId
            : targetId;
        var run = new GatewayRun(
            resolvedRunId,
            userId,
            targetType,
            targetId,
            SessionId: sessionId,
            InteractionId: null,
            string.IsNullOrWhiteSpace(error) ? "completed" : "failed",
            new Dictionary<string, object?> { ["message"] = input },
            new Dictionary<string, object?>
            {
                ["response"] = output,
                ["tokenIn"] = metrics?.TokenIn,
                ["tokenOut"] = metrics?.TokenOut,
                ["elapsedMs"] = metrics?.ElapsedMs,
            },
            effectiveBehavior,
            error,
            timestamp,
            timestamp,
            metrics?.ElapsedMs ?? 0,
            traceId);

        state.CreateAgentRuntimeRun(run);
        lock (state.RunLock)
        {
            state.Runs.Add(run);
        }
        return run;
    }

    public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options)
    {
        EnsureAgentSessionTarget(targetType, targetId);
        string timestamp = Timestamp();
        string runId = $"run-{Guid.NewGuid():N}";
        string resolvedSessionId = sessionId ?? runId;
        var run = new GatewayRun(
            runId,
            userId,
            targetType,
            targetId,
            sessionId,
            interactionId,
            "awaiting_human",
            new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["interaction_id"] = interactionId,
                ["session_id"] = sessionId,
            },
            new Dictionary<string, object?>
            {
                ["options"] = options.ToArray(),
            },
            null,
            null,
            timestamp,
            timestamp,
            0);

        state.CreateAgentRuntimeRun(run);
        lock (state.RunLock)
        {
            state.Runs.Add(run);
        }
        return run;
    }

    public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null)
    {
        lock (state.RunLock)
        {
            int index = state.Runs.FindIndex(item => string.Equals(item.InteractionId, interactionId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            GatewayRun existing = state.Runs[index];
            DateTimeOffset startedAt = DateTimeOffset.TryParse(existing.StartedAt, out DateTimeOffset parsedStartedAt)
                ? parsedStartedAt
                : DateTimeOffset.UtcNow;
            string endedAt = Timestamp();
            int durationMs = Math.Max(0, (int)(DateTimeOffset.Parse(endedAt, CultureInfo.InvariantCulture) - startedAt).TotalMilliseconds);

            var updated = existing with
            {
                Status = status,
                Output = output,
                EffectiveBehavior = existing.EffectiveBehavior,
                Error = error,
                EndedAt = endedAt,
                DurationMs = durationMs,
            };

            EnsureAgentSessionTarget(updated.TargetType, updated.TargetId);
            state.UpdateAgentRuntimeRun(updated);
            state.Runs[index] = updated;

            return updated;
        }
    }

    private void EnqueueEmbeddingReconciliation(AgentMemoryEmbeddingOperation operation, GatewayMemoryFact fact)
    {
        if (!string.Equals(fact.TargetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!memoryEmbeddingQueue.TryEnqueue(new AgentMemoryEmbeddingWork(operation, fact)))
        {
            GatewaySessionStoreLog.EmbeddingReconciliationDropped(logger, operation, fact.Id, fact.TargetId);
        }
    }

    private string ResolveSessionTitle(GatewaySession session, IReadOnlyList<GatewayMessage> messages)
        => session.TitleUserSet
            ? session.Title
            : sessionTitleService.ResolveTitle(session.Title, session.Originator, session.TargetType, session.TargetId, messages);

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}

internal static partial class GatewaySessionStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Rejected cross-target session reuse. Session {SessionId} belongs to {ExistingTargetType}:{ExistingTargetId} but request was for {RequestedTargetType}:{RequestedTargetId}")]
    public static partial void CrossTargetSessionReuseRejected(ILogger logger, string sessionId, string existingTargetType, string existingTargetId, string requestedTargetType, string requestedTargetId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "AppendMessages called on missing session {SessionId}")]
    public static partial void AppendMessagesMissingSession(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Embedding reconciliation queue full. Dropping {Operation} work for fact {FactId} on agent {AgentId}")]
    public static partial void EmbeddingReconciliationDropped(ILogger logger, AgentMemoryEmbeddingOperation operation, string factId, string agentId);

}
