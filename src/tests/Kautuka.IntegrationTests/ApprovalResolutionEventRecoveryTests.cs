using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ApprovalResolutionEventRecoveryTests
{
    [Fact]
    public void DuplicateApprovalIdIsIdempotentOnlyForTheSameImmutablePayload()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();

        store.CreateRequest(request);
        store.CreateRequest(request);
        Assert.Throws<InvalidOperationException>(() => store.CreateRequest(request with
        {
            ArgumentsJson = "{\"different\":true}",
        }));
        Assert.Equal("{}", store.GetById(request.Id)?.ArgumentsJson);
    }

    [Fact]
    public async Task ConcurrentBrowserAndChannelDecisionsHaveOneFirstValidWinner()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var browser = CreateGatewayApprovalStore(database);
        var channel = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        browser.CreateRequest(request);

        Task<bool> browserDecision = Task.Run(() => browser.Resolve(
            request.Id, approved: true, reason: "browser", decidedBy: "admin", resolutionSource: "chat"));
        Task<bool> channelDecision = Task.Run(() => channel.Resolve(
            request.Id, approved: false, reason: "channel", decidedBy: "telegram-user", resolutionSource: "telegram"));

        bool[] winners = await Task.WhenAll(browserDecision, channelDecision);

        Assert.Equal(1, winners.Count(static value => value));
        GatewayToolApprovalRequest committed = Assert.IsType<GatewayToolApprovalRequest>(browser.GetById(request.Id));
        Assert.Equal(winners[0] ? "approved" : "denied", committed.Decision);
        Assert.False(browser.Resolve(request.Id, approved: !winners[0], reason: "late", decidedBy: "late", resolutionSource: "channel"));
        Assert.Equal(winners[0] ? "approved" : "denied", browser.GetById(request.Id)?.Decision);
    }

    [Fact]
    public void LateResolutionForPriorApprovalCannotAffectFreshApprovalCycle()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest first = MakeRequest() with { Id = "approval-a" };
        GatewayToolApprovalRequest second = MakeRequest() with { Id = "approval-b" };
        store.CreateRequest(first);
        Assert.True(store.Resolve(first.Id, approved: true, reason: null, decidedBy: "admin", resolutionSource: "chat"));
        store.CreateRequest(second);

        Assert.False(store.Resolve(first.Id, approved: false, reason: "late", decidedBy: "channel-user", resolutionSource: "telegram"));
        Assert.Equal("approved", store.GetById(first.Id)?.Decision);
        Assert.Null(store.GetById(second.Id)?.Decision);
    }

    [Fact]
    public async Task CrashAfterCanonicalAppendRecoversWithoutVisibleDuplicate()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var durableStore = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        durableStore.CreateRequest(request);
        ApprovalResolutionEventWork staged = MakeEvent(request);
        Assert.True(durableStore.Resolve(
            request.Id,
            approved: true,
            reason: null,
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation: null,
            staged));

        var publisher = new IdempotentPublisher();
        DateTimeOffset crashTime = DateTimeOffset.UtcNow;
        ApprovalResolutionEventLease crashedLease = Assert.IsType<ApprovalResolutionEventLease>(
            durableStore.TryLeaseResolutionEvent("crashed-process", crashTime, crashTime.AddMinutes(2), request.Id));
        await ((IRuntimeEventPublisher)publisher).PublishWithIdAsync(
            crashedLease.Work.EventId,
            NaradEventTypes.InterruptResolved,
            crashedLease.Work.Payload,
            crashedLease.Work.Context,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, publisher.PublishAttempts);
        Assert.Single(publisher.VisibleEventIds);
        Assert.Null(durableStore.TryLeaseResolutionEvent("early-recovery", crashTime.AddMinutes(1), crashTime.AddMinutes(3), request.Id));

        var recoveredTime = new FixedTimeProvider(crashTime.AddMinutes(3));
        var recoveredProcess = new ApprovalResolutionEventDispatcher(
            durableStore,
            publisher,
            new ApprovalResolutionSideEffectApplier(new ThreadCommandTestSupport.NoOpRunStopCommandService()),
            recoveredTime,
            NullLogger<ApprovalResolutionEventDispatcher>.Instance);
        await recoveredProcess.DispatchAsync(request.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, publisher.PublishAttempts);
        Assert.Single(publisher.VisibleEventIds);
        Assert.Null(durableStore.TryLeaseResolutionEvent("later-process", recoveredTime.GetUtcNow(), recoveredTime.GetUtcNow().AddMinutes(2), request.Id));
        Assert.Equal(staged.EventId, Assert.Single(publisher.VisibleEventIds));
    }

    [Fact]
    public void LeasedStopRunResolutionKeepsStopRunForSideEffects()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        ApprovalResolutionEventWork staged = new(
            $"approval-resolution:{request.Id}",
            request.Id,
            NaradEventBodies.InterruptResolved(
                request.Id,
                NaradEventBodies.ApprovalWaitId(request.Id),
                ApprovalActionCatalog.StopRun),
            new NaradEventContext(
                request.AgentId!,
                request.SessionId!,
                request.UserId!,
                request.RunId!,
                request.RunId!,
                ParentSessionId: "session-1"),
            request.ToolCallId,
            request.CheckpointId,
            StopRun: true,
            StopThreadId: "session-1");
        Assert.True(store.Resolve(
            request.Id,
            "stopped",
            "stopped",
            "user-1",
            "chat",
            continuation: null,
            staged));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApprovalResolutionEventLease lease = Assert.IsType<ApprovalResolutionEventLease>(
            store.TryLeaseResolutionEvent("lease-owner", now, now.AddMinutes(2), request.Id));
        Assert.True(lease.Work.StopRun);
        Assert.Equal("session-1", lease.Work.StopThreadId);
        Assert.Equal(request.RunId, lease.Work.Context.RootRunId);
    }

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath),
            })
            .Build();
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private static GatewayToolApprovalRequest MakeRequest() => new(
        "apr-recovery",
        "session-1",
        "run-1",
        null,
        "agent-1",
        "user-1",
        "tool-call-1",
        "tool-1",
        "mcp",
        null,
        "{}",
        DateTimeOffset.UtcNow.ToString("O"),
        null,
        null,
        null,
        null,
        null);

    private static ApprovalResolutionEventWork MakeEvent(GatewayToolApprovalRequest request) => new(
        $"approval-resolution:{request.Id}",
        request.Id,
        JsonSerializer.SerializeToElement(new { approvalRequestId = request.Id, approved = true }),
        new NaradEventContext(request.AgentId!, request.SessionId!, request.UserId!, request.RunId!, request.RunId!),
        request.ToolCallId,
        request.CheckpointId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-approval-event-recovery", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory() => Directory.CreateDirectory(root);

        public string SqlitePath => Path.Combine(root, "kautuka.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class IdempotentPublisher : IRuntimeEventPublisher
    {
        public int PublishAttempts { get; private set; }

        public HashSet<string> VisibleEventIds { get; } = new(StringComparer.Ordinal);

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishAttempts++;
            VisibleEventIds.Add(draft.EventId);
            return ValueTask.FromResult(NaradTestEventFactory.FromDraft(draft, context, PublishAttempts, PublishAttempts));
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }
}
