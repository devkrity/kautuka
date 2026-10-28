using System.Globalization;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class RuntimeEventHistoryTests
{
    [Fact]
    public async Task PublisherPersistsRootHistoryAndDeliversLiveEvents()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-parent", "agent-parent");

        var definitionStore = new Kautuka.Gateway.Infrastructure.Configuration.SqliteAgentDefinitionStore(temp.Configuration, new KautukaDatabase(temp.Configuration));
        var streamHub = new RuntimeEventStreamHub();
        using var publisher = new RuntimeEventPublisher(definitionStore, streamHub);
        using RuntimeEventStreamSubscription subscription = streamHub.SubscribeForRoot("run-root");
        var context = new NaradEventContext(
            "agent-parent",
            "session-parent",
            "user-1",
            "run-root",
            "run-root");

        await publisher.PublishAsync(Draft("event-1", NaradEventTypes.RunStarted, context), context, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(Draft("event-2", NaradEventTypes.MessageTextDelta, context, Body("delta")), context, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(Draft("event-3", NaradEventTypes.RunFinished, context, Body("done")), context, TestContext.Current.CancellationToken);

        var delivered = new List<NaradPersistedEvent>();
        while (delivered.Count < 3)
        {
            delivered.Add(await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        IReadOnlyList<NaradPersistedEvent> history = new RuntimeEventHistoryReader(definitionStore).ReadRootHistory("run-root");
        Assert.Equal([1L, 2L, 3L], history.Select(static item => item.Sequence));
        Assert.Equal(history.Select(static item => item.EventId), delivered.Select(static item => item.EventId));
    }

    [Fact]
    public async Task ConcurrentPublishersDeliverRootEventsInSequenceOrder()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-parent", "agent-parent");

        var definitionStore = new Kautuka.Gateway.Infrastructure.Configuration.SqliteAgentDefinitionStore(temp.Configuration, new KautukaDatabase(temp.Configuration));
        var streamHub = new RuntimeEventStreamHub();
        using var publisher = new RuntimeEventPublisher(definitionStore, streamHub);
        using RuntimeEventStreamSubscription subscription = streamHub.SubscribeForRoot("run-concurrent");
        var context = new NaradEventContext(
            "agent-parent",
            "session-parent",
            "user-1",
            "run-concurrent",
            "run-concurrent");

        const int eventCount = 32;
        Task<NaradPersistedEvent>[] publishes = Enumerable.Range(0, eventCount)
            .Select(index => Task.Run(async () => await publisher.PublishAsync(
                Draft($"event-{index}", "activity.entry", context, Body(index.ToString(CultureInfo.InvariantCulture))),
                context,
                TestContext.Current.CancellationToken)))
            .ToArray();

        await Task.WhenAll(publishes);
        var published = new List<NaradPersistedEvent>(eventCount);
        for (int index = 0; index < eventCount; index++)
        {
            published.Add(await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            Enumerable.Range(1, eventCount).Select(static value => (long)value),
            published.Select(static runtimeEvent => runtimeEvent.Sequence ?? 0));
    }

    [Fact]
    public async Task StalledSubscriberIsDisconnectedAndHistoryRetainsBurst()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-parent", "agent-parent");
        var definitionStore = new Kautuka.Gateway.Infrastructure.Configuration.SqliteAgentDefinitionStore(temp.Configuration, new KautukaDatabase(temp.Configuration));
        var streamHub = new RuntimeEventStreamHub(subscriberCapacity: 2);
        using var publisher = new RuntimeEventPublisher(definitionStore, streamHub);
        using RuntimeEventStreamSubscription subscription = streamHub.SubscribeForRoot("run-burst");
        var context = new NaradEventContext(
            "agent-parent", "session-parent", "user-1", "run-burst", "run-burst");

        for (int index = 0; index < 20; index++)
        {
            await publisher.PublishAsync(
                Draft($"event-{index}", "activity.entry", context, Body(index.ToString(CultureInfo.InvariantCulture))),
                context,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, streamHub.SlowSubscriberDisconnectCount);
        Assert.Equal(0, streamHub.SubscriberCount);
        Assert.Equal(20, new RuntimeEventHistoryReader(definitionStore).ReadRootHistory("run-burst").Count);
        Assert.True(await subscription.Reader.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.True(subscription.Reader.TryRead(out _));
        Assert.True(subscription.Reader.TryRead(out _));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await subscription.Reader.Completion);
    }

    private static NaradAuthorityEventDraft Draft(
        string eventId,
        string eventType,
        NaradEventContext context,
        JsonElement? body = null)
        => new(
            eventId,
            eventType,
            context.SessionId,
            body ?? Body(eventId),
            DateTimeOffset.UtcNow.ToString("O"),
            NaradRecordClasses.Authority,
            NaradProtocolVersions.V1,
            context.RootRunId,
            context.RunId);

    private static JsonElement Body(string value)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"value":"{{value}}"}""");
        return document.RootElement.Clone();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath;

        private TemporaryDatabase(string directoryPath) => this.directoryPath = directoryPath;

        public IConfiguration Configuration { get; private init; } = null!;

        public static TemporaryDatabase Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-runtime-history", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = directoryPath })
                .Build();
            return new TemporaryDatabase(directoryPath) { Configuration = configuration };
        }

        public void EnsureReady() => new KautukaDatabase(Configuration).EnsureReady();

        public void InsertAgent(string agentId, string storageLocatorKey)
        {
            new KautukaDatabase(Configuration).EnsureReady();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new KautukaDatabase(Configuration).ConnectionString);
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at)
                VALUES ($id, 'user', 1, 'user-1', $storage, $now, $now)
                """;
            command.Parameters.AddWithValue("$id", agentId);
            command.Parameters.AddWithValue("$storage", storageLocatorKey);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(directoryPath, recursive: true); }
            catch (IOException) { }
        }
    }
}
