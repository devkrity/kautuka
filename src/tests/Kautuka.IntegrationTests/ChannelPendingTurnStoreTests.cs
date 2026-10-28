using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class SessionTurnQueueStoreTests
{
    [Fact]
    public void MessageSessionMappingsAreImmutableAndScopedAcrossRestart()
    {
        using var fixture = new Fixture();
        var registration = new GatewayChannelRegistration("reg", "telegram", "owner", "bot", true,
            new JsonObject { ["telegram_bot_id"] = "bot-1" }, "now", "now");
        fixture.OpenAnchors().SaveMessageSession(registration, "chat", "101", "session-a", "42");
        fixture.OpenAnchors().SaveMessageSession(registration, "chat", "101", "session-b", "42");
        Assert.Equal("session-a", fixture.OpenAnchors().ResolveMessageSession(registration, "chat", "101", "42"));
        Assert.Null(fixture.OpenAnchors().ResolveMessageSession(registration, "chat", "102", "42"));
        Assert.Null(fixture.OpenAnchors().ResolveMessageSession(registration, "other", "101", "42"));
        Assert.Null(fixture.OpenAnchors().ResolveMessageSession(registration with { OwnerUserId = "other" }, "chat", "101", "42"));
        Assert.Null(fixture.OpenAnchors().ResolveMessageSession(registration with { Id = "reg-bot-2", Settings = new JsonObject { ["telegram_bot_id"] = "bot-2" } }, "chat", "101", "42"));
    }

    [Fact]
    public void VisibleActivityRefreshesUnchangedPanelOnceAndIsScopedAcrossRestart()
    {
        using var fixture = new Fixture();
        ChannelQueueViewStore store = fixture.OpenView();
        long first = store.NextQueueViewRevision("owner", "reg:session", "chat", "101", "same", onlyIfChanged: true);
        store.Delivered("owner", "reg:session", "chat", "101", first, "old-card");
        store.RecordActivity("owner", "reg:session", "chat", "102", "other-topic");
        Assert.Equal(0, store.NextQueueViewRevision("owner", "reg:session", "chat", "101", "same", onlyIfChanged: true));
        store.RecordActivity("owner", "reg:session", "chat", "101", "approval-1");
        store.RecordActivity("owner", "reg:session", "chat", "101", "answer-2");
        Assert.Equal(first + 1, fixture.OpenView().NextQueueViewRevision("owner", "reg:session", "chat", "101", "same", onlyIfChanged: true));
        store.RecordActivity("owner", "reg:session", "chat", "101", "answer-2");
        Assert.Equal(0, store.NextQueueViewRevision("owner", "reg:session", "chat", "101", "same", onlyIfChanged: true));
    }

    [Fact]
    public void PromptAnchorsSurviveRestartAndNeverCrossOwnerBotChatTopicSessionOrRun()
    {
        using var fixture = new Fixture();
        var registration = new GatewayChannelRegistration("reg", "telegram", "owner", "bot", true,
            new JsonObject { ["telegram_bot_id"] = "bot-1" }, "now", "now");
        fixture.OpenAnchors().Save(registration, "chat", "101", "session", "run", null);
        Assert.Equal((true, (string?)null), fixture.OpenAnchors().Read(registration, "chat", "101", "session", "run"));
        fixture.OpenAnchors().Save(registration, "chat", "101", "session", "run", "42");
        fixture.OpenAnchors().Save(registration, "chat", "101", "session", "run", "wrong-retry");
        Assert.Equal((true, "42"), fixture.OpenAnchors().Read(registration, "chat", "101", "session", "run"));
        Assert.False(fixture.OpenAnchors().Read(registration with { OwnerUserId = "other" }, "chat", "101", "session", "run").Expected);
        Assert.False(fixture.OpenAnchors().Read(registration with { Id = "reg-bot-2", Settings = new JsonObject { ["telegram_bot_id"] = "bot-2" } }, "chat", "101", "session", "run").Expected);
        Assert.False(fixture.OpenAnchors().Read(registration, "other", "101", "session", "run").Expected);
        Assert.False(fixture.OpenAnchors().Read(registration, "chat", "102", "session", "run").Expected);
        Assert.False(fixture.OpenAnchors().Read(registration, "chat", "101", "other", "run").Expected);
        Assert.False(fixture.OpenAnchors().Read(registration, "chat", "101", "session", "other").Expected);
        fixture.OpenAnchors().Save(registration, "chat", "101", "session", "root", "42", "Dice");
        Assert.Null(fixture.OpenAnchors().ReadSessionLabel(registration, "chat", "101", "session", "run"));
        Assert.Equal("Dice", fixture.OpenAnchors().ReadSessionLabel(registration, "chat", "101", "session", "root"));
        fixture.OpenAnchors().Save(registration, "chat", "101", "session", "run", "wrong-retry", "Wrong session label");
        Assert.Null(fixture.OpenAnchors().ReadSessionLabel(registration, "chat", "101", "session", "run"));
    }

    [Fact]
    public void ViewPaginationAndClosedPickerTombstonesSurviveRestart()
    {
        using var fixture = new Fixture();
        ChannelQueueViewStore store = fixture.OpenView();
        long first = store.NextQueueViewRevision("owner", "reg:picker", "chat", "101", pageOffset: 5);
        store.Delivered("owner", "reg:picker", "chat", "101", first, "picker-message");
        Assert.Equal(5, fixture.OpenView().Read("owner", "reg:picker", "chat", "101")!.PageOffset);
        store.Close("owner", "reg:picker", "chat", "101");
        store.Delivered("owner", "reg:picker", "chat", "101", first, "late-delivery");
        ChannelQueueView closed = fixture.OpenView().Read("owner", "reg:picker", "chat", "101")!;
        Assert.Null(closed.MessageId);
        Assert.Equal(first + 1, closed.DeliveredRevision);
        Assert.Equal(0, closed.PageOffset);
        Assert.Equal(first + 2, store.NextQueueViewRevision("owner", "reg:picker", "chat", "101"));
    }

    [Fact]
    public void SessionActionsSurviveRestartAndAreBoundToOwnerBotChatAndTopic()
    {
        using var fixture = new Fixture();
        string token = fixture.OpenView().CreateAction("owner", "reg:bot-1", "chat", "101", "clear", "session-original", "42")[3..];
        ChannelUiAction action = Assert.IsType<ChannelUiAction>(fixture.OpenView().ReadAction(token, "owner", "reg:bot-1", "chat", "101"));
        Assert.Equal("session-original", action.SessionId);
        Assert.Equal("42", action.Value);
        Assert.Null(fixture.OpenView().ReadAction(token, "other-owner", "reg:bot-1", "chat", "101"));
        Assert.Null(fixture.OpenView().ReadAction(token, "owner", "reg:bot-2", "chat", "101"));
        Assert.Null(fixture.OpenView().ReadAction(token, "owner", "reg:bot-1", "other-chat", "101"));
        Assert.Null(fixture.OpenView().ReadAction(token, "owner", "reg:bot-1", "chat", "202"));
    }

    [Fact]
    public void AutomaticRefreshSkipsAnAlreadyDeliveredIdenticalSnapshotButExplicitRefreshDoesNot()
    {
        using var fixture = new Fixture();
        ChannelQueueViewStore store = fixture.OpenView();
        Assert.Equal(1, store.NextQueueViewRevision("owner", "reg", "chat", "101", "session:a,b", onlyIfChanged: true));
        store.Delivered("owner", "reg", "chat", "101", 1, "card-1");
        Assert.Equal(0, fixture.OpenView().NextQueueViewRevision("owner", "reg", "chat", "101", "session:a,b", onlyIfChanged: true));
        Assert.Equal(2, store.NextQueueViewRevision("owner", "reg", "chat", "101", "session:a,b"));
        Assert.Equal(3, store.NextQueueViewRevision("owner", "reg", "chat", "101", "session:b", onlyIfChanged: true));
    }

    [Fact]
    public void QueueCardRevisionsSurviveRestartAndRemainScopedToDestination()
    {
        using var fixture = new Fixture();
        ChannelQueueViewStore store = fixture.OpenView();
        Assert.Equal(1, store.NextQueueViewRevision("owner", "reg", "chat", "101"));
        Assert.Equal(2, fixture.OpenView().NextQueueViewRevision("owner", "reg", "chat", "101"));
        Assert.Equal(1, store.NextQueueViewRevision("owner", "reg", "chat", "202"));
        Assert.Equal(1, store.NextQueueViewRevision("owner", "other-reg", "chat", "101"));
        Assert.Equal(1, store.NextQueueViewRevision("other-owner", "reg", "chat", "101"));
    }

    [Fact]
    public void QueueCapacityRejectsWithoutDroppingExistingWorkAndRecoversAfterRemoval()
    {
        using var fixture = new Fixture();
        SessionTurnQueueStore store = fixture.Open();
        for (int i = 0; i < 100; i++) store.Enqueue($"turn-{i}", "session", "agent", Payload("waiting"));
        Assert.Throws<SessionQueueRejectedException>(() => store.Enqueue("over-limit", "session", "agent", Payload("new message")));
        Assert.Equal(100, store.ListSession("owner", "session").Count);
        Assert.Equal(1, store.RemoveSessionWaiting("owner", "session", "turn-0"));
        Assert.True(store.Enqueue("over-limit", "session", "agent", Payload("new message")));
        Assert.False(store.Enqueue("turn-0", "session", "agent", Payload("must not replay removed work")));
        Assert.Throws<SessionQueueRejectedException>(() => store.Enqueue("oversized", "other-session", "agent", new string('x', 1024 * 1024 + 1)));
    }

    [Fact]
    public void QueueSurvivesRestartKeepsSessionOrderAndDoesNotRepeatAdmission()
    {
        using var fixture = new Fixture();
        SessionTurnQueueStore store = fixture.Open();
        string a = SessionTurnQueueStore.BuildId("reg", "a");
        string b = SessionTurnQueueStore.BuildId("reg", "b");
        Assert.True(store.Enqueue(a, "session", "agent", Payload("446")));
        Assert.True(store.Enqueue(b, "session", "agent", Payload("777")));
        Assert.False(store.Enqueue(a, "session", "agent", Payload("changed")));
        store = fixture.Open();
        Assert.Equal(a, Assert.Single(store.ListHeads()).Id);
        QueuedSessionTurn leased = Assert.IsType<QueuedSessionTurn>(store.TryLease(store.ListHeads()[0]));
        Assert.Null(store.TryLease(leased));
        Assert.Empty(store.ListHeads());
        store.Complete(leased);
        Assert.Equal(b, Assert.Single(store.ListHeads()).Id);
        Assert.True(store.HasQueued("session"));
    }

    [Fact]
    public void QueueControlsAreScopedAndCannotDeleteAnActiveTurnOrFutureMessages()
    {
        using var fixture = new Fixture();
        SessionTurnQueueStore store = fixture.Open();
        store.Enqueue("first", "session", "agent", Payload("446"));
        long snapshot = Assert.Single(store.ListSession("owner", "session")).Order;
        store.Enqueue("second", "session", "agent", Payload("777"));
        Assert.Equal(0, store.RemoveSessionWaiting("other-owner", "session", "first"));
        Assert.Equal(0, store.RemoveSessionWaiting("owner", "other-session", "first"));
        Assert.Equal(0, store.RemoveSessionWaiting("owner", "another-session", "first"));
        Assert.Equal(0, store.RemoveSessionWaiting("owner", "third-session", "first"));
        Assert.Equal(1, store.RemoveSessionWaiting("owner", "session", null, snapshot));
        Assert.Equal("second", Assert.Single(store.ListSession("owner", "session")).Id);
        QueuedSessionTurn leased = Assert.IsType<QueuedSessionTurn>(store.TryLease(store.ListHeads()[0]));
        Assert.Equal(0, store.RemoveSessionWaiting("owner", "session", "second"));
        store.Retry(leased);
        Assert.Equal(1, store.RemoveSessionWaiting("owner", "session", "second"));
        Assert.False(store.HasQueued("session"));
    }

    [Fact]
    public async Task SessionExecutionGateSerializesOneSessionAndCancellationDoesNotLeakIt()
    {
        string key = Guid.NewGuid().ToString("N");
        using IDisposable first = await SessionExecutionGate.EnterAsync(key, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var cancelled = new CancellationTokenSource();
        Task<IDisposable> waiting = SessionExecutionGate.EnterAsync(key, cancelled.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        using IDisposable independent = await SessionExecutionGate.EnterAsync(key + "-other", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).ConfigureAwait(true);
        first.Dispose();
        using IDisposable next = await SessionExecutionGate.EnterAsync(key, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static string Payload(string message) => new JsonObject
    {
        ["ownerUserId"] = "owner",
        ["registrationId"] = "reg",
        ["chatId"] = "chat",
        ["queueTopic"] = "101",
        ["message"] = message,
    }.ToJsonString();

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-turn-queue-" + Guid.NewGuid().ToString("N"));
        private readonly KautukaDatabase database;
        public Fixture()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            database = new KautukaDatabase(config);
            database.EnsureReady();
        }
        public SessionTurnQueueStore Open() => new(database);
        public ChannelQueueViewStore OpenView() => new(database);
        public ChannelPromptAnchorStore OpenAnchors() => new(database);
        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
