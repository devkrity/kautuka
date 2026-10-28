using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewaySessionStoreInteractionRunTests
{
    [Fact]
    public void InteractionRunCanBeCreatedAndResolvedAgainstSession()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession session = store.CreateSession("agent", "triage-agent", "user-123");
        GatewayRun pending = store.CreateInteractionRun(
            "agent",
            "triage-agent",
            "user-123",
            session.Id,
            "hitl-123",
            "Approve deployment?",
            ["approve", "deny"]);

        GatewayRun? resolved = store.UpdateInteractionRun(
            "hitl-123",
            "resolved",
            new Dictionary<string, object?>
            {
                ["resolution"] = "approve",
                ["resolved_by_external_user_id"] = "telegram-42",
            });

        Assert.Equal("awaiting_human", pending.Status);
        Assert.Equal(session.Id, pending.SessionId);
        Assert.Equal("hitl-123", pending.InteractionId);
        Assert.NotNull(resolved);
        Assert.Equal(pending.Id, resolved!.Id);
        Assert.Equal("resolved", resolved.Status);
        Assert.Equal("approve", resolved.Output["resolution"]);
        Assert.Equal("telegram-42", resolved.Output["resolved_by_external_user_id"]);
        Assert.True(resolved.DurationMs >= 0);
    }

    [Fact]
    public async Task SessionLifecyclePublishesRetentionRequestsForAgents()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);
        var publisher = new CapturingMediator();
        var lifecycle = new SessionLifecycleService(
            store,
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Agents =
                [
                    new AgentDefinition
                    {
                        Id = "agent-alpha",
                        Session = new AgentSessionDefinition { MaxHistoryRuns = 2 },
                    },
                ],
            }),
            publisher);

        GatewaySession agentSession = await lifecycle.CreateSessionAsync("agent", "agent-alpha", "user-123", CancellationToken.None);

        Assert.Single(publisher.RetentionRequests);
        Assert.Contains(publisher.RetentionRequests, item => item.TargetType == "agent" && item.TargetId == "agent-alpha" && item.OwnerUserId == "user-123" && item.SessionId == agentSession.Id && item.RetainCount == 2);
    }

    [Fact]
    public void PruneSessionsKeepsNewestSessionsForTarget()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession first = store.CreateSession("agent", "agent-alpha", "user-123");
        GatewaySession second = store.CreateSession("agent", "agent-alpha", "user-123");
        GatewaySession third = store.CreateSession("agent", "agent-alpha", "user-123");

        IReadOnlyList<string> deleted = store.PruneSessions("agent", "agent-alpha", "user-123", retainCount: 2, preserveSessionId: third.Id);

        Assert.Single(deleted);
        Assert.Contains(first.Id, deleted);
        Assert.True(store.TryGetSession(second.Id, out _));
        Assert.True(store.TryGetSession(third.Id, out _));
    }

    [Fact]
    public void PruneSessionsDoesNotDeleteOtherOwnersSessions()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession ownerOneOld = store.CreateSession("agent", "agent-alpha", "user-123");
        GatewaySession ownerOneNew = store.CreateSession("agent", "agent-alpha", "user-123");
        GatewaySession ownerTwoFirst = store.CreateSession("agent", "agent-alpha", "user-999");
        GatewaySession ownerTwoSecond = store.CreateSession("agent", "agent-alpha", "user-999");

        IReadOnlyList<string> deleted = store.PruneSessions("agent", "agent-alpha", "user-123", retainCount: 1, preserveSessionId: ownerOneNew.Id);

        Assert.Single(deleted);
        Assert.Contains(ownerOneOld.Id, deleted);
        Assert.True(store.TryGetSession(ownerOneNew.Id, out _));
        Assert.True(store.TryGetSession(ownerTwoFirst.Id, out _));
        Assert.True(store.TryGetSession(ownerTwoSecond.Id, out _));
    }

    [Fact]
    public void SessionRoundTripPersistsToolCallsThinkingMetricsAndActivityEntries()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.Agents["ops-agent"] = new GatewayAgent(
            "ops-agent",
            "Ops Agent",
            "model-ops",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-123",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());
        state.AgentStorageLocators["ops-agent"] = "ops-agent";
        ((IAtomicAgentDefinitionStore)state.AgentDefinitionStore).SaveDefinitionAndRegistryAtomically(
            new AgentRegistryEntry(
                "ops-agent",
                GatewayOwnershipScopes.User,
                Enabled: true,
                OwnerUserId: "user-123",
                StorageLocatorKey: "ops-agent"),
            new AgentDefinition
            {
                Id = "ops-agent",
                Scope = GatewayOwnershipScopes.User,
                Enabled = true,
                Name = "Ops Agent",
                ModelId = "model-ops",
                SystemPrompt = "prompt",
                SuperExecMode = "trusted",
            });
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession session = store.CreateSession("agent", "ops-agent", "user-123");
        store.AppendMessages(
            session.Id,
            new GatewayMessage(
                "msg-user",
                "user",
                "Roll one d20.",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null),
            new GatewayMessage(
                Id: "msg-assistant",
                Role: "assistant",
                Content: "Rolled a d20.",
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Compact: false,
                Thinking: "Need randomness from the skill.",
                Metrics: new GatewayMetrics(12, 8, 345),
                ToolCalls: [new GatewayToolCall("call-1", "run_skill_script__roll_dice", "ops-agent", "{\"scriptPath\":\"generated-from-markdown.sh\",\"arguments\":[\"20\"]}", "17", "done", "skill")],
                ExecutedActions:
                [
                    new GatewayExecutedAction(
                        "action-call-1",
                        "skill",
                        "run_skill_script__roll_dice",
                        "completed",
                        ToolCallIds: ["call-1"])
                ],
                ActivityEntries:
                [
                    new GatewayActivityEntry(
                        "activity-action-call-1",
                        1,
                        "action",
                        "completed",
                        ActionKind: "skill",
                        Name: "run_skill_script__roll_dice",
                        ToolCallIds: ["call-1"])
                ]));

        GatewayState reloadedState = CreateState(temp.SqlitePath);
        Assert.True(reloadedState.Sessions.TryGetValue(session.Id, out GatewaySession? reloaded));

        GatewayMessage assistant = Assert.Single(reloaded!.Messages, message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Need randomness from the skill.", assistant.Thinking);
        Assert.NotNull(assistant.Metrics);
        Assert.Equal(12, assistant.Metrics!.TokenIn);
        Assert.NotNull(assistant.ToolCalls);
        GatewayToolCall toolCall = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("call-1", toolCall.ToolCallId);
        Assert.Equal("run_skill_script__roll_dice", toolCall.ToolName);
        Assert.Equal("done", toolCall.Status);
        GatewayExecutedAction executedAction = Assert.Single(assistant.ExecutedActions!);
        Assert.Equal("action-call-1", executedAction.Id);
        Assert.Equal("run_skill_script__roll_dice", executedAction.Name);
        GatewayActivityEntry activityEntry = Assert.Single(assistant.ActivityEntries!);
        Assert.Equal("activity-action-call-1", activityEntry.Id);
        Assert.Equal("action", activityEntry.Kind);
    }

    [Fact]
    public void CreateSessionPrefixesGeneratedTitleWithOriginator()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession session = store.CreateSession("agent", "ops-agent", "user-123", "telegram");

        Assert.Equal("telegram", session.Originator);
        Assert.Equal("[Telegram] New Chat", session.Title);
    }

    [Fact]
    public void CreateSessionPrefixesAgentToolOriginAsTool()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession session = store.CreateSession("agent", "ops-agent", "user-123", "agent_tool");

        Assert.Equal("agent_tool", session.Originator);
        Assert.Equal("[Tool] New Chat", session.Title);
    }

    [Fact]
    public void AppendMessagesUsesOriginPrefixForGeneratedTitlesButDoesNotRewriteManualTitles()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        GatewaySession generated = store.CreateSession("agent", "ops-agent", "user-123", "autopilot");

        GatewaySession titled = store.AppendMessages(
            generated.Id,
            new GatewayMessage(
                "msg-user-generated",
                "user",
                "Summarize the nightly deployment status",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null),
            new GatewayMessage(
                "msg-assistant-generated",
                "assistant",
                "Nightly deployment status summarized.",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null));

        Assert.Equal("[Autopilot] Summarize the nightly deployment status", titled.Title);

        GatewaySession manual = store.CreateSession("agent", "ops-agent", "user-123", "telegram");
        manual = Assert.IsType<GatewaySession>(store.UpdateSessionTitle(manual.Id, "Custom incident title"));

        GatewaySession preserved = store.AppendMessages(
            manual.Id,
            new GatewayMessage(
                "msg-user-manual",
                "user",
                "This should not rewrite the custom title",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null),
            new GatewayMessage(
                "msg-assistant-manual",
                "assistant",
                "Custom title should remain unchanged.",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null));

        Assert.Equal("Custom incident title", preserved.Title);
    }

    [Fact]
    public void UpdateMemoryFactQueuesEmbeddingReconciliationAfterPersistingAgentRuntime()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.Agents["ops-agent"] = new GatewayAgent(
            "ops-agent",
            "Ops Agent",
            "model-ops",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-123",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());
        state.AgentStorageLocators["ops-agent"] = "ops-agent";
        state.AgentDefinitionStore.SaveDefinition("ops-agent", new AgentDefinition
        {
            Id = "ops-agent",
            Scope = GatewayOwnershipScopes.User,
            Enabled = true,
            Name = "Ops Agent",
            ModelId = "model-ops",
            SystemPrompt = "prompt",
            SuperExecMode = "trusted",
        });

        GatewayMemoryFact fact = new(
            "fact-1",
            "agent",
            "ops-agent",
            "user-123",
            "Before",
            "Old content",
            "profile",
            "manual",
            1.0,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            null);
        state.UpsertAgentMemoryFacts("agent", "ops-agent", [fact]);

        var queue = new CapturingAgentMemoryEmbeddingQueue();
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            queue,
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        bool updated = store.UpdateMemoryFact("fact-1", "After", "New content", "preferences");

        Assert.True(updated);
        AgentMemoryEmbeddingWork work = Assert.Single(queue.WorkItems);
        Assert.Equal(AgentMemoryEmbeddingOperation.Persist, work.Operation);
        Assert.Equal("fact-1", work.Fact.Id);
        Assert.Equal("After", work.Fact.Title);
        Assert.Equal("New content", work.Fact.Content);

        GatewayMemoryFact reloadedFact = Assert.Single(state.AgentMemoryFactRepository.ListMemoryFacts("ops-agent"));
        Assert.Equal("After", reloadedFact.Title);
        Assert.Equal("New content", reloadedFact.Content);
        Assert.Equal("preferences", reloadedFact.Category);
    }

    [Fact]
    public void DeleteMemoryFactQueuesEmbeddingReconciliationAfterPersistingAgentRuntime()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.Agents["ops-agent"] = new GatewayAgent(
            "ops-agent",
            "Ops Agent",
            "model-ops",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-123",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());
        state.AgentStorageLocators["ops-agent"] = "ops-agent";
        state.AgentDefinitionStore.SaveDefinition("ops-agent", new AgentDefinition
        {
            Id = "ops-agent",
            Scope = GatewayOwnershipScopes.User,
            Enabled = true,
            Name = "Ops Agent",
            ModelId = "model-ops",
            SystemPrompt = "prompt",
            SuperExecMode = "trusted",
        });

        GatewayMemoryFact fact = new(
            "fact-1",
            "agent",
            "ops-agent",
            "user-123",
            "Before",
            "Old content",
            "profile",
            "manual",
            1.0,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            null);
        state.UpsertAgentMemoryFacts("agent", "ops-agent", [fact]);

        var queue = new CapturingAgentMemoryEmbeddingQueue();
        var store = new GatewaySessionStore(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            queue,
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance);

        bool removed = store.DeleteMemoryFact("fact-1");

        Assert.True(removed);
        AgentMemoryEmbeddingWork work = Assert.Single(queue.WorkItems);
        Assert.Equal(AgentMemoryEmbeddingOperation.Delete, work.Operation);
        Assert.Equal("fact-1", work.Fact.Id);

        Assert.Empty(state.AgentMemoryFactRepository.ListMemoryFacts("ops-agent"));
    }

    private static GatewayState CreateState(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        return GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration, database),
            configuration,
            new NoOpWorkspaceRuntimeTool());
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => CreateContext("agent", agentId, userId);

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => CreateContext("agent", agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession("agent", agentId, userId));

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession("agent", agentId, userId));

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string scopeType, string scopeId, string? userId)
            => new(scopeType, scopeId, userId, Path.GetTempPath(), "/workspace", "/workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string scopeType, string scopeId, string? userId)
            => new($"{scopeType}-{scopeId}", false, scopeType, scopeId, userId);
    }

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
    }

    private sealed class CapturingMediator : IMediator
    {
        public List<SessionRetentionRequested> RetentionRequests { get; } = [];

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
        {
            if (notification is SessionRetentionRequested retention)
            {
                RetentionRequests.Add(retention);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            if (notification is SessionRetentionRequested retention)
            {
                RetentionRequests.Add(retention);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-session-tests", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string SqlitePath => Path.Combine(root, "gateway.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

internal sealed class NoOpGatewayContinuationStore : IGatewayContinuationStore
{
    public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint) { }
    public int GetNextCheckpointIndex(string continuationRunId) => 0;
    public GatewayContinuationCheckpoint? GetById(string checkpointId) => null;
    public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash) => null;
    public void DeleteBySessions(IReadOnlyCollection<string> sessionIds) { }
}

internal sealed class NoOpConversationAssetStore : IConversationAssetStore
{
    public Task<GatewayConversationAsset> CreateUploadAsync(string sessionId, string? userId, AssetUploadCommand command, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public IReadOnlyDictionary<string, IReadOnlyList<GatewayConversationAsset>> ListAssetsByMessage(string sessionId, IReadOnlyList<string> messageIds)
        => new Dictionary<string, IReadOnlyList<GatewayConversationAsset>>(StringComparer.Ordinal);

    public IReadOnlyList<GatewayConversationAsset> ListSessionAssets(string sessionId, string? userId, IReadOnlyList<string>? assetIds = null)
        => [];

    public IReadOnlyList<StoredConversationAsset> GetAssetsByIds(string sessionId, IReadOnlyList<string> assetIds, string? userId)
        => [];

    public bool HasRetainedWorkspaceReference(string agentId, string relativePath, string ownerUserId)
        => false;

    public IReadOnlyList<WorkspaceAttachmentReferenceSummary> ListRetainedWorkspaceAttachmentReferences(string agentId)
        => [];

    public IReadOnlyList<WorkspaceAttachmentFileSummary> ListUnreferencedWorkspaceAttachmentFiles(string agentId)
        => [];

    public string? FindLatestAssetIdByFileName(string sessionId, string fileName, string? userId)
        => null;

    public (string SessionId, string AssetId)? FindLatestAssetReferenceByFileName(string fileName, string? userId)
        => null;

    public void BindAssetsToMessage(string sessionId, string messageId, IReadOnlyList<string> assetIds, string? userId)
    {
    }

    public bool TryOpenAsset(string sessionId, string assetId, string? userId, out AssetDownloadResult? result)
    {
        result = null;
        return false;
    }

    public void DeleteSessionAssets(IReadOnlyList<string> sessionIds)
    {
    }
}

internal sealed class NoOpAgentMemoryEmbeddingQueue : IAgentMemoryEmbeddingQueue
{
    public bool TryEnqueue(AgentMemoryEmbeddingWork work) => true;

    public bool TryDequeue(out AgentMemoryEmbeddingWork? work)
    {
        work = null;
        return false;
    }

    public IAsyncEnumerable<AgentMemoryEmbeddingWork> DequeueAllAsync(CancellationToken cancellationToken)
        => AsyncEnumerable.Empty<AgentMemoryEmbeddingWork>();
}

internal sealed class NoOpAgentMemoryEmbeddingService : IAgentMemoryEmbeddingService
{
    public bool EnqueuePersistFacts(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts)
        => false;

    public Task PersistFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PersistFactsAsync(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class CapturingAgentMemoryEmbeddingQueue : IAgentMemoryEmbeddingQueue
{
    public List<AgentMemoryEmbeddingWork> WorkItems { get; } = [];

    public bool TryEnqueue(AgentMemoryEmbeddingWork work)
    {
        WorkItems.Add(work);
        return true;
    }

    public bool TryDequeue(out AgentMemoryEmbeddingWork? work)
    {
        if (WorkItems.Count == 0)
        {
            work = null;
            return false;
        }

        work = WorkItems[0];
        WorkItems.RemoveAt(0);
        return true;
    }

    public IAsyncEnumerable<AgentMemoryEmbeddingWork> DequeueAllAsync(CancellationToken cancellationToken)
        => AsyncEnumerable.Empty<AgentMemoryEmbeddingWork>();
}
