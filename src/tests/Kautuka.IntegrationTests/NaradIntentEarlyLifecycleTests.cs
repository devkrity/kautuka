using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Endpoints.Runtime;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.NaradTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class NaradIntentEarlyLifecycleTests
{
    [Fact]
    public async Task AppendPromptAcceptsStaleBrowserRevisionWhileRunAdvances()
    {
        await using WebApplication app = await BuildAppAsync(seedEvents:
        [
            AuthorityEvent("running", NaradEventTypes.RunStarted, "session-1", 1, "root-1", "root-1", 1),
        ]).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/narad/v1/agents/agent-1/sessions/session-1/messages",
            new { message = "follow-up while streaming", expectedRevision = 0 }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        QueuedSessionTurn waiting = Assert.Single(app.Services.GetRequiredService<SessionTurnQueueStore>().ListSession("user-1", "session-1"));
        using JsonDocument payload = JsonDocument.Parse(waiting.PayloadJson);
        Assert.Equal("follow-up while streaming", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BrowserQueueListsWaitingOnlyAndClearCannotRemoveNewerOrActiveWork()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        SessionTurnQueueStore queue = app.Services.GetRequiredService<SessionTurnQueueStore>();
        void Admit(string id, string message) => queue.Admit(id, JsonSerializer.SerializeToElement(new
        {
            target_id = "agent-1",
            session_id = "session-1",
            user_id = "user-1",
            message,
            source = "narad",
        }, JsonSerializerOptions.Web));
        Admit("active", "active prompt");
        QueuedSessionTurn active = queue.TryLease(Assert.Single(queue.ListHeads()))!;
        Admit("waiting", "waiting prompt");
        const string url = "/api/narad/v1/agents/agent-1/sessions/session-1/queue";
        JsonElement snapshot = await client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("waiting", Assert.Single(snapshot.GetProperty("messages").EnumerateArray()).GetProperty("id").GetString());
        long cutoff = snapshot.GetProperty("throughOrder").GetInt64();
        Admit("newer", "newer prompt");
        using HttpResponseMessage cleared = await client.PostAsJsonAsync(url + "/remove", new { throughOrder = cutoff }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal("newer", Assert.Single(queue.ListSession("user-1", "session-1")).Id);
        queue.Renew(active);
        Assert.True(queue.HasQueued("session-1"));
        using HttpResponseMessage removed = await client.PostAsJsonAsync(url + "/remove", new { messageId = "newer" }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(queue.ListSession("user-1", "session-1"));
    }

    [Fact]
    public async Task BrowserQueueDoesNotExposeOtherOwnersOrOtherSessions()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        SessionTurnQueueStore queue = app.Services.GetRequiredService<SessionTurnQueueStore>();
        queue.Admit("other-owner", JsonSerializer.SerializeToElement(new
        {
            target_id = "agent-1",
            session_id = "session-1",
            user_id = "someone-else",
            message = "private",
        }, JsonSerializerOptions.Web));
        const string url = "/api/narad/v1/agents/agent-1/sessions/session-1/queue";
        JsonElement snapshot = await client.GetFromJsonAsync<JsonElement>(url, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Empty(snapshot.GetProperty("messages").EnumerateArray());
        using HttpResponseMessage removed = await client.PostAsJsonAsync(url + "/remove", new { messageId = "other-owner" }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Single(queue.ListSession("someone-else", "session-1"));
        using HttpResponseMessage wrongSession = await client.GetAsync(new Uri("/api/narad/v1/agents/agent-1/sessions/unknown/queue", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.OK, wrongSession.StatusCode);
    }

    [Fact]
    public async Task SendMessageIntentDurablyQueuesWithoutStartingRunBeforeExecution()
    {
        var publisher = new TestRuntimeEventPublisher();
        await using WebApplication app = await BuildAppAsync(runtimeEventPublisher: publisher).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = "hello", expectedRevision = 0 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("accepted", body.GetProperty("status").GetString());

        Assert.Empty(publisher.Published);
        Assert.Equal(0, body.GetProperty("currentSessionSequence").GetInt64());
        QueuedSessionTurn turn = Assert.Single(app.Services.GetRequiredService<SessionTurnQueueStore>().ListSession("user-1", "session-1"));
        using JsonDocument stored = JsonDocument.Parse(turn.PayloadJson);
        Assert.Equal("hello", stored.RootElement.GetProperty("message").GetString());
        Assert.False(stored.RootElement.GetProperty("request").GetProperty("early_lifecycle_published").GetBoolean());
    }

    [Fact]
    public async Task SendMessageIntentAcceptsFileOnlyMessage()
    {
        var publisher = new TestRuntimeEventPublisher();
        await using WebApplication app = await BuildAppAsync(runtimeEventPublisher: publisher).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = "", assetIds = new[] { "asset-file-only" }, expectedRevision = 0 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(publisher.Published);
        QueuedSessionTurn turn = Assert.Single(app.Services.GetRequiredService<SessionTurnQueueStore>().ListSession("user-1", "session-1"));
        using JsonDocument stored = JsonDocument.Parse(turn.PayloadJson);
        Assert.Equal("asset-file-only", stored.RootElement.GetProperty("request").GetProperty("assetIds")[0].GetString());
    }

    [Fact]
    public async Task SendMessageIntentRejectsFullQueueWithoutPublishingAnUnstartedRun()
    {
        var publisher = new TestRuntimeEventPublisher();
        await using WebApplication app = await BuildAppAsync(
            runtimeEventPublisher: publisher,
            intentQueue: new FullNaradIntentExecutionQueue()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = "hello", expectedRevision = 0 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task StreamAsyncEmitsEarlyLifecycleWhenFlagSetButHistoryEmpty()
    {
        var publisher = new TestRuntimeEventPublisher();
        var historyReader = new RecordingRuntimeEventHistoryReader();
        AgentChatService service = CreateService(publisher, historyReader);

        string runId = Guid.NewGuid().ToString("N");

        NaradPersistedEvent[] events = await CollectAsync(service.StreamAsync(
            "agent-1",
            "session-1",
            "user-1",
            "narad",
            "hello",
            null,
            [],
            [],
            [],
            null,
            null,
            new AgentRequestContext(
                RunId: runId,
                UserMessageId: NaradEventBodies.BuildUserMessageId(runId, runId),
                EarlyLifecycleEventsPublished: true),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains(events, item => item.EventType == NaradEventTypes.RunStarted);
        Assert.Contains(events, item => item.EventType == NaradEventTypes.MessageUserRecorded);
        Assert.Equal(2, publisher.Published.Count(item =>
            item.EventType is NaradEventTypes.RunStarted or NaradEventTypes.MessageUserRecorded));
    }

    [Fact]
    public async Task StreamAsyncRecordsAttachmentIdsOnUserMessage()
    {
        var publisher = new TestRuntimeEventPublisher();
        var historyReader = new RecordingRuntimeEventHistoryReader();
        AgentChatService service = CreateService(publisher, historyReader);
        GatewayConversationAsset asset = new(
            "asset-channel-1",
            "photo.jpg",
            "image/jpeg",
            12,
            "userUpload",
            "inlinePreview",
            "/api/sessions/session-1/assets/asset-channel-1",
            DateTimeOffset.UtcNow.ToString("O"));

        NaradPersistedEvent[] events = await CollectAsync(service.StreamAsync(
            "agent-1",
            "session-1",
            "user-1",
            "telegram",
            "",
            null,
            [asset],
            [],
            [],
            null,
            null,
            null,
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        NaradPersistedEvent userRecorded = Assert.Single(events, item => item.EventType == NaradEventTypes.MessageUserRecorded);
        JsonElement body = NaradPersistedEventReader.ParseBody(userRecorded);
        Assert.Equal("asset-channel-1", body.GetProperty("attachmentIds")[0].GetString());
    }

    [Fact]
    public async Task StreamAsyncSkipsEarlyLifecycleWhenAlreadyPublished()
    {
        var publisher = new TestRuntimeEventPublisher();
        var historyReader = new RecordingRuntimeEventHistoryReader();
        AgentChatService service = CreateService(publisher, historyReader);

        string runId = Guid.NewGuid().ToString("N");
        historyReader.Seed(
            CreateLifecycleEvent(NaradEventTypes.RunStarted, runId, "session-1", NaradEventBodies.RunStarted("narad")),
            CreateLifecycleEvent(NaradEventTypes.MessageUserRecorded, runId, "session-1", NaradEventBodies.MessageUserRecorded(
                NaradEventBodies.BuildUserMessageId(runId, runId),
                "hello")));

        NaradPersistedEvent[] events = await CollectAsync(service.StreamAsync(
            "agent-1",
            "session-1",
            "user-1",
            "narad",
            "hello",
            null,
            [],
            [],
            [],
            null,
            null,
            new AgentRequestContext(
                RunId: runId,
                UserMessageId: NaradEventBodies.BuildUserMessageId(runId, runId),
                EarlyLifecycleEventsPublished: true),
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.DoesNotContain(events, item => item.EventType == NaradEventTypes.RunStarted);
        Assert.DoesNotContain(events, item => item.EventType == NaradEventTypes.MessageUserRecorded);
        Assert.Contains(events, item => item.EventType == NaradEventTypes.RunError);
        Assert.DoesNotContain(
            publisher.Published,
            item => item.EventType is NaradEventTypes.RunStarted or NaradEventTypes.MessageUserRecorded);
    }

    private static AgentChatService CreateService(
        TestRuntimeEventPublisher publisher,
        IRuntimeEventHistoryReader historyReader)
        => new(
            runtimeFactory: new UnavailableRuntimeFactory(),
            capabilityService: null!,
            observationStore: null!,
            mediator: null!,
            logger: NullLogger<AgentChatService>.Instance,
            approvalStore: null!,
            continuationStore: null!,
            resumeTokenProvider: null!,
            assetStore: null!,
            assetIngestionService: null!,
            gatewayState: null!,
            agentRunMetrics: new NoOpAgentRunMetrics(),
            usageCaptureAccessor: new GatewayUsageCaptureAccessor(),
            sessionStore: null!,
            agentToolInvocationLinkStore: null!,
            runWaitStateStore: null!,
            platformConfigurationProvider: null!,
            toolExecutionPolicyService: null!,
            runtimeEventPublisher: publisher,
            modelTurnOrdinalCounter: new ModelTurnOrdinalCounter(),
            runtimeEventHistoryReader: historyReader,
            executionReadinessService: new FixedReadinessService(UnavailableReadiness()),
            configuration: new ConfigurationBuilder().Build());

    private static AgentExecutionReadiness UnavailableReadiness()
        => new(
            false,
            "MANAGER_UNAVAILABLE",
            "Manager unavailable.",
            [new("manager", false, "MANAGER_UNAVAILABLE", "Manager unavailable.")]);

    private static async Task<NaradPersistedEvent[]> CollectAsync(IAsyncEnumerable<NaradPersistedEvent> source)
    {
        var events = new List<NaradPersistedEvent>();
        await foreach (NaradPersistedEvent persistedEvent in source.ConfigureAwait(false))
        {
            events.Add(persistedEvent);
        }

        return [.. events];
    }

    private static NaradPersistedEvent CreateLifecycleEvent(
        string eventType,
        string runId,
        string sessionId,
        JsonElement body)
    {
        NaradEventContext context = new("agent-1", sessionId, "user-1", runId, runId, SourceChannel: "narad");
        return NaradTestEventFactory.FromDraft(
            NaradAuthorityEventDraftFactory.CreateNew(eventType, body, context),
            context,
            sessionSequence: 1,
            rootSequence: 1);
    }

    private sealed class FullNaradIntentExecutionQueue : INaradIntentExecutionQueue
    {
        public bool TryEnqueue(NaradIntentExecutionWork work) => false;
    }

    private sealed class RecordingRuntimeEventHistoryReader : IRuntimeEventHistoryReader
    {
        private readonly List<NaradPersistedEvent> events = [];

        public void Seed(params NaradPersistedEvent[] seeded) => events.AddRange(seeded);

        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId)
            => events
                .Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal))
                .OrderBy(static item => item.Sequence ?? 0)
                .ToArray();

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId)
            => events
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(static item => item.SessionSequence)
                .ToArray();
    }

    private sealed class UnavailableRuntimeFactory : IAgentRuntimeFactory
    {
        public ValueTask<AgentRuntimeResult> BuildAsync(
            string agentId,
            string sessionId,
            string? userId,
            string? stageTransitionId,
            IReadOnlyList<InjectedMemoryFact> memoryFacts,
            bool isContinuationResume,
            bool requireApprovalForAllTools,
            string? sessionStateOverrideJson,
            string? effectiveBehaviorOverrideJson,
            CancellationToken ct)
            => throw new InvalidOperationException("Runtime should not be constructed for this test.");

        public ValueTask SaveSessionAsync(AgentRuntime runtime, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class NoOpAgentRunMetrics : IAgentRunMetrics
    {
        public void RecordRunStarted(AgentRunMetricContext context)
        {
        }

        public void RecordRunCompleted(AgentRunMetricContext context, AgentRunMetricObservation observation)
        {
        }
    }

    private sealed class FixedReadinessService(AgentExecutionReadiness readiness) : IAgentExecutionReadinessService
    {
        public Task<AgentExecutionReadiness> CheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(readiness);
    }
}
