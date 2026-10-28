using Kautuka.Gateway.Domain.Channels;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class SqliteChannelConversationMapStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TelegramBindingPromotionPreservesSessionAcrossExpiryCleanupAndRestart(bool restart)
    {
        using var temp = new TemporarySqliteDirectory();
        KautukaDatabase database = CreateDatabase(temp.DatabasePath);
        var store = new SqliteChannelConversationMapStore(database);
        ChannelConversationMapEntry legacy = store.CreateTransientThread("telegram", "reg", "agent", "agent-1", "chat", "101", "session-old", DateTimeOffset.UtcNow.AddHours(-9));
        store.CreateTransientThread("telegram", "reg", "agent", "agent-1", "chat", "task:expired", "task-session", DateTimeOffset.UtcNow.AddHours(-9));
        if (restart) store = new SqliteChannelConversationMapStore(database);
        else store.GetOrCreatePermanentTopic("telegram", "reg", "agent", "agent-1", "chat", "101", null);
        Assert.Equal(1, store.CleanupExpiredTransientThreads(DateTimeOffset.UtcNow.AddDays(30)));
        ChannelConversationMapEntry? retained = store.TryResolve("telegram", "reg", "chat", "101");
        Assert.NotNull(retained);
        Assert.Equal(legacy.Id, retained.Id);
        Assert.Equal("session-old", retained.SessionId);
        Assert.Null(retained.ExpiresAt);
        store.AttachSession(legacy.Id, "session-new");
        var reopened = new SqliteChannelConversationMapStore(database);
        Assert.Equal("session-new", reopened.TryResolve("telegram", "reg", "chat", "101")?.SessionId);
    }

    [Fact]
    public void PermanentTopicIsReusedForSameConversationAndRegistration()
    {
        using var temp = new TemporarySqliteDirectory();
        var store = new SqliteChannelConversationMapStore(CreateDatabase(temp.DatabasePath));

        ChannelConversationMapEntry first = store.GetOrCreatePermanentTopic(
            "telegram",
            "registration-a",
            "agent",
            "AgentAlpha",
            "chat-100",
            "topic-900",
            sessionId: null);

        ChannelConversationMapEntry second = store.GetOrCreatePermanentTopic(
            "telegram",
            "registration-a",
            "agent",
            "AgentAlpha",
            "chat-100",
            "topic-900",
            sessionId: null);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("permanent_topic", second.ConversationKind);
    }

    [Fact]
    public void TransientThreadAttachSessionPersistsAndCanBeResolved()
    {
        using var temp = new TemporarySqliteDirectory();
        var store = new SqliteChannelConversationMapStore(CreateDatabase(temp.DatabasePath));

        ChannelConversationMapEntry created = store.CreateTransientThread(
            "telegram",
            "registration-b",
            "agent",
            "simpleBrain",
            "chat-200",
            "task:abc",
            sessionId: null,
            DateTimeOffset.UtcNow.AddMinutes(10));

        ChannelConversationMapEntry? attached = store.AttachSession(created.Id, "session-123", DateTimeOffset.UtcNow.AddMinutes(30));
        ChannelConversationMapEntry? resolved = store.TryResolve("telegram", "registration-b", "chat-200", "task:abc");

        Assert.NotNull(attached);
        Assert.Equal("session-123", attached!.SessionId);
        Assert.NotNull(resolved);
        Assert.Equal("session-123", resolved!.SessionId);
        Assert.Equal("transient_thread", resolved.ConversationKind);
    }

    [Fact]
    public void CleanupExpiredTransientThreadsRemovesOnlyExpiredTransientEntries()
    {
        using var temp = new TemporarySqliteDirectory();
        var store = new SqliteChannelConversationMapStore(CreateDatabase(temp.DatabasePath));

        ChannelConversationMapEntry expired = store.CreateTransientThread(
            "telegram",
            "registration-c",
            "agent",
            "simpleBrain",
            "chat-300",
            "task:expired",
            sessionId: null,
            DateTimeOffset.UtcNow.AddMinutes(-5));

        ChannelConversationMapEntry active = store.CreateTransientThread(
            "telegram",
            "registration-c",
            "agent",
            "simpleBrain",
            "chat-300",
            "task:active",
            sessionId: null,
            DateTimeOffset.UtcNow.AddMinutes(15));

        ChannelConversationMapEntry permanent = store.GetOrCreatePermanentTopic(
            "telegram",
            "registration-c",
            "agent",
            "AgentAlpha",
            "chat-300",
            "topic-1",
            sessionId: null);

        int removed = store.CleanupExpiredTransientThreads(DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.Null(store.TryResolve("telegram", "registration-c", "chat-300", expired.ProviderThreadId));
        Assert.NotNull(store.TryResolve("telegram", "registration-c", "chat-300", active.ProviderThreadId));
        Assert.NotNull(store.TryResolve("telegram", "registration-c", "chat-300", permanent.ProviderThreadId));
    }

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private sealed class TemporarySqliteDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-channel-tests", Guid.NewGuid().ToString("N"));

        public TemporarySqliteDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string DatabasePath => Path.Combine(root, "memories.db");

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
