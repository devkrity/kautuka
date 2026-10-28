using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.RuntimePlane.Projection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

internal static class TestRuntimeEventPublisherSupport
{
    internal static ValueTask<NaradPersistedEvent> PublishWithIdAsync(
        Func<NaradAuthorityEventDraft, NaradEventContext, CancellationToken, ValueTask<NaradPersistedEvent>> publishAsync,
        string eventId,
        string eventType,
        JsonElement body,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
        => publishAsync(
            NaradAuthorityEventDraftFactory.Create(eventId, eventType, body, context),
            context,
            cancellationToken);
}

internal sealed class TestRuntimeEventPublisher : IRuntimeEventPublisher
{
    public List<NaradPersistedEvent> Published { get; } = [];

    public ValueTask<NaradPersistedEvent> PublishAsync(
        NaradAuthorityEventDraft draft,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long sessionSequence = Published.Count(item => string.Equals(item.SessionId, context.SessionId, StringComparison.Ordinal)) + 1;
        long rootSequence = Published.Count(item => string.Equals(item.RootRunId, context.RootRunId, StringComparison.Ordinal)) + 1;
        NaradPersistedEvent persistedEvent = NaradTestEventFactory.FromDraft(draft, context, sessionSequence, rootSequence);
        Published.Add(persistedEvent);
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

internal static class NaradTestSupport
{
    private sealed class NaradQueueFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-narad-queue-tests", Guid.NewGuid().ToString("N"));
        public SessionTurnQueueStore Store { get; }
        public NaradQueueFixture()
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            var database = new KautukaDatabase(configuration);
            database.EnsureReady();
            Store = new SessionTurnQueueStore(database);
        }
        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task<WebApplication> BuildAppAsync(
        IReadOnlyList<NaradPersistedEvent>? seedEvents = null,
        IGatewayAuthGuard? authGuard = null,
        IGatewaySessionStore? sessionStore = null,
        IThreadCommandService? threadCommandService = null,
        INaradIntentRequestHandler? intentHandler = null,
        IRuntimeEventPublisher? runtimeEventPublisher = null,
        INaradIntentExecutionQueue? intentQueue = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());

        builder.Services.AddSingleton(authGuard ?? new AllowAllAuthGuard());
        builder.Services.AddSingleton<IGatewayCatalogStore>(new FakeCatalogStore());
        builder.Services.AddSingleton<NaradTestAgentStore>();
        builder.Services.AddSingleton<IAgentDefinitionStore>(sp => sp.GetRequiredService<NaradTestAgentStore>());
        builder.Services.AddSingleton<IRuntimeEventHistoryReader>(sp => sp.GetRequiredService<NaradTestAgentStore>());
        builder.Services.AddSingleton<IRootRunAdmissionGate, RootRunAdmissionGate>();
        var streamHub = new RuntimeEventStreamHub();
        builder.Services.AddSingleton(streamHub);
        builder.Services.AddSingleton<IRuntimeEventStreamHub>(_ => streamHub);
        builder.Services.AddSingleton<IRuntimeEventPublisher>(runtimeEventPublisher ?? new TestRuntimeEventPublisher());
        builder.Services.AddSingleton<INaradWorkspaceWatchHub, NaradWorkspaceWatchHub>();
        builder.Services.AddSingleton<INaradClientLeaseService, NaradClientLeaseService>();
        builder.Services.AddSingleton<IChatGatewayService, NoOpChatGatewayService>();
        builder.Services.AddSingleton(_ => new NaradQueueFixture());
        builder.Services.AddSingleton(sp => sp.GetRequiredService<NaradQueueFixture>().Store);
        if (intentQueue is null)
        {
            builder.Services.AddSingleton<NaradIntentExecutionQueue>();
            builder.Services.AddSingleton<INaradIntentExecutionQueue>(sp => sp.GetRequiredService<NaradIntentExecutionQueue>());
        }
        else
        {
            builder.Services.AddSingleton(intentQueue);
        }
        builder.Services.AddSingleton<IThreadCommandCoordinator, ThreadCommandCoordinator>();

        if (sessionStore is null)
        {
            builder.Services.AddSingleton<ThreadCommandTestSupport.ThreadCommandSessionStore>(_ => new ThreadCommandTestSupport.ThreadCommandSessionStore());
            builder.Services.AddSingleton<IGatewaySessionStore>(sp => sp.GetRequiredService<ThreadCommandTestSupport.ThreadCommandSessionStore>());
        }
        else
        {
            builder.Services.AddSingleton(sessionStore);
        }

        if (intentHandler is null)
        {
            builder.Services.AddSingleton<INaradIntentRequestHandler, NaradIntentRequestHandler>();
        }
        else
        {
            builder.Services.AddSingleton(intentHandler);
        }

        if (threadCommandService is not null)
        {
            builder.Services.AddSingleton(threadCommandService);
        }
        else
        {
            builder.Services.AddSingleton<IThreadCommandService, ThreadCommandTestSupport.RecordingThreadCommandService>();
        }

        WebApplication app = builder.Build();
        if (seedEvents is { Count: > 0 })
        {
            NaradTestAgentStore store = app.Services.GetRequiredService<NaradTestAgentStore>();
            IRuntimeEventStreamHub hub = app.Services.GetRequiredService<IRuntimeEventStreamHub>();
            foreach (NaradPersistedEvent persistedEvent in seedEvents.OrderBy(static item => item.SessionSequence))
            {
                store.Append(persistedEvent);
                hub.Publish(persistedEvent);
            }
        }

        app.MapNaradEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return app;
    }

    internal static NaradPersistedEvent AuthorityEvent(
        string eventId,
        string eventType,
        string sessionId,
        long sessionSequence,
        string? rootRunId = null,
        string? runId = null,
        long? rootSequence = null,
        JsonObject? body = null)
    {
        string occurredAt = "2026-08-05T00:00:00Z";
        var envelope = new JsonObject
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = occurredAt,
            ["sessionId"] = sessionId,
            ["sessionSequence"] = sessionSequence,
        };

        if (body is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> property in body)
            {
                envelope[property.Key] = property.Value?.DeepClone();
            }
        }

        if (!string.IsNullOrWhiteSpace(rootRunId) && !string.IsNullOrWhiteSpace(runId) && rootSequence.HasValue)
        {
            envelope["rootRunId"] = rootRunId;
            envelope["runId"] = runId;
            envelope["sequence"] = rootSequence.Value;
        }

        return new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            sessionId,
            sessionSequence,
            NaradRecordClasses.Authority,
            envelope.ToJsonString(),
            occurredAt,
            rootRunId,
            runId,
            rootSequence,
            null,
            null);
    }

    internal static NaradPersistedEvent ParentProjectionEvent(
        string eventId,
        string eventType,
        string sessionId,
        long sessionSequence,
        string sourceEventId,
        string sourceSessionId,
        string rootRunId,
        string runId,
        JsonObject? body = null)
    {
        string occurredAt = "2026-08-05T00:00:00Z";
        var envelope = new JsonObject
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = occurredAt,
            ["sessionId"] = sessionId,
            ["sessionSequence"] = sessionSequence,
            ["sourceEventId"] = sourceEventId,
            ["sourceSessionId"] = sourceSessionId,
            ["rootRunId"] = rootRunId,
            ["runId"] = runId,
        };

        if (body is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> property in body)
            {
                envelope[property.Key] = property.Value?.DeepClone();
            }
        }

        return new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            sessionId,
            sessionSequence,
            NaradRecordClasses.Authority,
            envelope.ToJsonString(),
            occurredAt,
            RootRunId: null,
            RunId: null,
            Sequence: null,
            WorkflowId: null,
            WorkflowSequence: null);
    }

    internal static async Task<List<SseFrame>> ReadSseFramesAsync(
        Stream stream,
        int maxFrames,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        using var reader = new StreamReader(stream);
        var frames = new List<SseFrame>();
        string? eventId = null;
        var dataLines = new List<string>();
        TimeSpan idle = idleTimeout ?? TimeSpan.FromSeconds(2);

        while (frames.Count < maxFrames)
        {
            using var lineTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lineTimeout.CancelAfter(idle);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(lineTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                eventId = line[4..];
                continue;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLines.Add(line[6..]);
                continue;
            }

            if (line.Length == 0 && dataLines.Count > 0)
            {
                frames.Add(new SseFrame(eventId, string.Join('\n', dataLines)));
                eventId = null;
                dataLines.Clear();
            }
        }

        if (dataLines.Count > 0)
        {
            frames.Add(new SseFrame(eventId, string.Join('\n', dataLines)));
        }

        return frames;
    }

    internal sealed record SseFrame(string? Id, string Data);

    internal sealed class NaradTestAgentStore : IAgentDefinitionStore, IRuntimeEventHistoryReader
    {
        private readonly List<NaradPersistedEvent> events = [];
        private readonly object gate = new();

        public void Append(NaradPersistedEvent persistedEvent)
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
                return events
                    .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                    .Select(static item => item.SessionSequence)
                    .DefaultIfEmpty(0L)
                    .Max();
            }
        }

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId)
            => ListNaradSessionHistory(storageLocatorKey, sessionId, 0, NaradProfileDiscovery.MaxHistoryLimit);

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(
            string storageLocatorKey,
            string sessionId,
            long afterSessionSequence,
            int limit)
        {
            NaradJournalValidation.ValidateHistoryLimit(limit);
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
                        && item.SessionSequence > afterSessionSequence)
                    .OrderBy(static item => item.SessionSequence)
                    .Take(limit)
                    .ToArray();
            }
        }

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId)
            => ListNaradSessionHistory(storageLocatorKey, sessionId);

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId)
            => ListNaradRootHistory(storageLocatorKey, rootRunId, 0, NaradProfileDiscovery.MaxHistoryLimit);

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(
            string storageLocatorKey,
            string rootRunId,
            long afterRootSequence,
            int limit)
        {
            NaradJournalValidation.ValidateHistoryLimit(limit);
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal)
                        && item.Sequence > afterRootSequence)
                    .OrderBy(static item => item.Sequence ?? 0)
                    .Take(limit)
                    .ToArray();
            }
        }

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId)
            => ListNaradRootHistory(storageLocatorKey, rootRunId);

        public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId)
        {
            lock (gate)
            {
                return events.Any(item =>
                    string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal)
                    && string.Equals(item.RunId, rootRunId, StringComparison.Ordinal)
                    && item.EventType is NaradEventTypes.RunFinished
                        or NaradEventTypes.RunError
                        or NaradEventTypes.RunCancelled);
            }
        }

        public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId)
        {
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(item.RootRunId))
                    .Select(static item => item.RootRunId!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore)
        {
            _ = startedBefore;
            lock (gate)
            {
                return events
                    .Where(item => string.Equals(item.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal)
                        && string.Equals(item.RunId, item.RootRunId, StringComparison.Ordinal)
                        && string.CompareOrdinal(item.OccurredAt, startedBefore) < 0)
                    .Select(static item => item.RootRunId!)
                    .Where(static rootRunId => !string.IsNullOrWhiteSpace(rootRunId))
                    .Distinct(StringComparer.Ordinal)
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

        public NaradPersistedEvent AppendNaradAuthorityEvent(
            string rootStorageLocatorKey,
            string sourceStorageLocatorKey,
            NaradAuthorityEventDraft draft)
            => throw new NotSupportedException();

        public NaradPersistedEvent AppendNaradParentSessionProjection(
            string rootStorageLocatorKey,
            NaradPersistedEvent sourceEvent,
            string parentSessionId)
            => throw new NotSupportedException();

        public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent)
            => throw new NotSupportedException();

        public string? FindNaradRootStorageLocatorKey(string rootRunId) => "agent-1";

        public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId)
        {
            lock (gate)
            {
                return events
                    .LastOrDefault(item => string.Equals(item.RunId, runId, StringComparison.Ordinal))
                    ?.RootRunId;
            }
        }

        public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
            => throw new NotSupportedException();

        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey) => throw new NotSupportedException();

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(
            string rootStorageLocatorKey,
            string leaseOwner,
            TimeSpan leaseDuration,
            int limit)
            => throw new NotSupportedException();

        public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner)
            => throw new NotSupportedException();

        public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner)
            => throw new NotSupportedException();

        public void RecordNaradEventOutboxFailure(
            string rootStorageLocatorKey,
            string eventId,
            string leaseOwner,
            string safeError)
            => throw new NotSupportedException();

        public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit)
            => throw new NotSupportedException();

        public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();
        public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId) => throw new NotSupportedException();
        public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys() => throw new NotSupportedException();
        public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings) => throw new NotSupportedException();
        public void DeleteMemoryEmbedding(string storageLocatorKey, string factId) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey) => throw new NotSupportedException();
        public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit) => throw new NotSupportedException();
        public bool DeleteStore(string storageLocatorKey) => throw new NotSupportedException();
    }

    internal sealed class AllowAllAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("user-1", "user", "pass", "admin", true, null, null);
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("user-1", "user", "pass", "admin", true, null, null);
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("user-1", "user", "pass", "admin", true, null, null);
            return null;
        }
    }

    internal sealed class DenyCrossSessionAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("other-user", "other", "pass", "user", false, null, null);
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("other-user", "other", "pass", "user", false, null, null);
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = new GatewayUser("other-user", "other", "pass", "user", false, null, null);
            return null;
        }
    }

    internal sealed class FakeCatalogStore : IGatewayCatalogStore
    {
        private readonly GatewayAgent agent = new(
            Id: "agent-1",
            Name: "Agent One",
            ModelId: "model-1",
            SystemPrompt: "prompt",
            SuperExecMode: "trusted",
            Session: new JsonObject(),
            Planning: new JsonObject(),
            Sandbox: new JsonObject(),
            SkillAttachments: [],
            OwnerUserId: "user-1",
            CreatedAt: "2026-08-05T00:00:00Z",
            Enabled: true);

        public IReadOnlyList<GatewayAgent> ListAgents() => [agent];

        public bool TryGetAgent(string agentId, out GatewayAgent? resolvedAgent)
        {
            resolvedAgent = string.Equals(agentId, agent.Id, StringComparison.Ordinal) ? agent : null;
            return resolvedAgent is not null;
        }

        public IReadOnlyList<GatewayModel> ListModels() => [];
        public bool TryGetModel(string modelId, out GatewayModel? model) { model = null; return false; }
        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();
        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) => throw new NotSupportedException();
        public bool DeleteModel(string modelId) => false;
        public IReadOnlyList<GatewayProvider> ListProviders() => [];
        public bool TryGetProvider(string providerId, out GatewayProvider? provider) { provider = null; return false; }
        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();
        public bool DeleteProvider(string providerId) => false;
        public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteAgent(string agentId) => false;
        public IReadOnlyList<GatewayMcpServer> ListMcpServers() => [];
        public bool TryGetMcpServer(string id, out GatewayMcpServer? server) { server = null; return false; }
        public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId) => throw new NotSupportedException();
        public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames) => throw new NotSupportedException();
        public bool DeleteMcpServer(string serverId) => false;
        public IReadOnlyList<GatewaySkill> ListSkills() => [];
        public bool TryGetSkill(string id, out GatewaySkill? skill) { skill = null; return false; }
        public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId) => throw new NotSupportedException();
        public bool DeleteSkill(string skillId) => false;
    }

    internal sealed class NoOpChatGatewayService : IChatGatewayService
    {
        public IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<NaradPersistedEvent>();

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<NaradPersistedEvent>();

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

    internal sealed class NoOpIntentHandler : INaradIntentRequestHandler
    {
        public Task<IResult> SendMessageAsync(
            string agentId,
            string sessionId,
            JsonElement body,
            GatewayUser user,
            CancellationToken cancellationToken)
            => Task.FromResult(NaradIntentResponses.Accepted(currentRevision: 0));
    }

}
