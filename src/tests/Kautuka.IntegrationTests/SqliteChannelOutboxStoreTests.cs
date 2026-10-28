using System.Text.Json;

using Kautuka.Gateway.Domain.Channels;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class SqliteChannelOutboxStoreTests
{
    [Fact]
    public void UserFacingMessagesAreLeasedBeforeRefreshableSessionFacts()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-channel-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        try
        {
            var database = new KautukaDatabase(configuration);
            database.EnsureReady();
            var outbox = new SqliteChannelOutboxStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            outbox.Enqueue("slack", "slack:owner:default", "session_facts", Facts("D-slack", null));
            outbox.Enqueue("slack", "slack:owner:default", "send_text", "{}");

            ChannelOutboxEntry[] leased = [.. outbox.LeasePending("test", 1, TimeSpan.FromMinutes(1), now)];

            Assert.Equal("send_text", Assert.Single(leased).ActionType);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NewSessionFactsSupersedeOnlyPendingFactsForTheSameRegistrationAndChatThread()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-channel-outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        try
        {
            var database = new KautukaDatabase(configuration);
            database.EnsureReady();
            var outbox = new SqliteChannelOutboxStore(database);

            outbox.Enqueue("telegram", "telegram:owner:default", "session_facts", Facts("chat-1", null));
            outbox.Enqueue("telegram", "telegram:owner:default", "session_facts", Facts("chat-1", "thread-1"));
            outbox.Enqueue("telegram", "telegram:owner:default", "session_facts", Facts("chat-2", null));
            outbox.Enqueue("slack", "slack:owner:default", "session_facts", Facts("chat-1", null));
            outbox.Enqueue("telegram", "telegram:owner:default", "session_facts", Facts("chat-1", null, revision: 2));

            ChannelOutboxEntry[] pending = [.. outbox.LeasePending("test", 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow)];

            Assert.Equal(4, pending.Length);
            Assert.Contains(pending, entry => entry.RegistrationId == "telegram:owner:default"
                && FactsMatch(entry.PayloadJson, "chat-1", "thread-1"));
            Assert.Contains(pending, entry => entry.RegistrationId == "telegram:owner:default"
                && FactsMatch(entry.PayloadJson, "chat-2", null));
            Assert.Contains(pending, entry => entry.RegistrationId == "slack:owner:default"
                && FactsMatch(entry.PayloadJson, "chat-1", null));
            Assert.Contains(pending, entry => entry.RegistrationId == "telegram:owner:default"
                && FactsMatch(entry.PayloadJson, "chat-1", null) && entry.PayloadJson.Contains("\"revision\":2", StringComparison.Ordinal));
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Facts(string conversationId, string? threadId, long revision = 1)
        => JsonSerializer.Serialize(new { conversationId, conversationThreadId = threadId, revision });

    private static bool FactsMatch(string payloadJson, string conversationId, string? threadId)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        JsonElement root = document.RootElement;
        string? actualThread = root.GetProperty("conversationThreadId").ValueKind == JsonValueKind.String
            ? root.GetProperty("conversationThreadId").GetString()
            : null;
        return root.GetProperty("conversationId").GetString() == conversationId && actualThread == threadId;
    }
}
