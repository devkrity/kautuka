using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class SessionCompactionServiceTests
{
    [Fact]
    public async Task CompactAsyncBuildsStructuredPromptWhenCompactionModelIsConfigured()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();

        var sessionStore = new FakeSessionStore(CreateSession(messageCount: 10));
        var database = new KautukaDatabase(temp.Configuration);
        var mafSessionStore = new MafSessionStore(database);
        var chatClient = new RecordingChatClient();
        var provider = new RecordingAiClientProvider("test", chatClient);
        var service = new SessionCompactionService(
            sessionStore,
            new StaticPlatformConfigurationProvider(CreateConfigurationWithCompactionModel()),
            new KautukaAiClientFactory([provider], BuildEmptyCredentialResolver()),
            mafSessionStore,
            NullLogger<SessionCompactionService>.Instance);

        await service.CompactAsync("session-1", "manual", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(chatClient.LastPrompt);
        Assert.Contains("You are compacting a chat transcript for future continuation.", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Summarize only the \"Earlier messages to summarize\" block.", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("must not be summarized.", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("- File paths, symbols, commands, and tool outputs", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not restate the transcript verbatim unless exact wording is needed for correctness.", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("If assistant messages contain useful conclusions, decisions, or confirmed results", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Return plain text using exactly these headings:", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Session summary", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Durable facts", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Open threads", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Earlier messages to summarize:", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Recent messages already preserved separately:", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Equal(ChatResponseFormat.Text, chatClient.LastOptions?.ResponseFormat);
        Assert.Equal(512, chatClient.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task CompactAsyncDeletesPersistedMafStateAndUsesDeterministicFallbackWithoutCompactionModel()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();

        var sessionStore = new FakeSessionStore(CreateSession(messageCount: 10));
        var database = new KautukaDatabase(temp.Configuration);
        var mafSessionStore = new MafSessionStore(database);

        using JsonDocument state = JsonDocument.Parse("""
            {
              "stateBag": {
                "InMemoryChatHistoryProvider": {
                  "messages": [
                    { "role": "user", "contents": [{ "$type": "text", "text": "legacy session history" }] }
                  ]
                }
              }
            }
            """);

        await mafSessionStore.SaveAsync(
            "session-1",
            "agent-1",
            "agent",
            state.RootElement,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        var service = new SessionCompactionService(
            sessionStore,
            new StaticPlatformConfigurationProvider(new PlatformConfiguration()),
            new KautukaAiClientFactory([], BuildEmptyCredentialResolver()),
            mafSessionStore,
            NullLogger<SessionCompactionService>.Instance);

        GatewaySession? compacted = await service.CompactAsync("session-1", "manual", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(compacted);
        Assert.True(compacted!.Compacted);
        Assert.Equal("manual", compacted.LastCompactionReason);
        Assert.Contains("Session summary", compacted.CompactSummary, StringComparison.Ordinal);
        Assert.Contains("Deterministic fallback summary generated without a compaction model.", compacted.CompactSummary, StringComparison.Ordinal);
        Assert.Contains("Durable facts", compacted.CompactSummary, StringComparison.Ordinal);
        Assert.Contains("Open threads", compacted.CompactSummary, StringComparison.Ordinal);
        Assert.Contains("Most recent transcript excerpts:", compacted.CompactSummary, StringComparison.Ordinal);
        Assert.Null(await mafSessionStore.LoadAsync("session-1", TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    private static PlatformConfiguration CreateConfigurationWithCompactionModel()
        => new()
        {
            Providers =
            [
                new ProviderDefinition
                {
                    Id = "provider-1",
                    Type = "test",
                },
            ],
            Models =
            [
                new ModelDefinition
                {
                    Id = "model-compact",
                    ProviderId = "provider-1",
                    ModelName = "compact-model",
                },
            ],
            Memory = new MemorySettings
            {
                SessionCompactionModelId = "model-compact",
            },
        };

    private static GatewaySession CreateSession(int messageCount)
    {
        List<GatewayMessage> messages = [];
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        for (int index = 0; index < messageCount; index++)
        {
            bool isAssistant = index % 2 == 1;
            messages.Add(new GatewayMessage(
                Id: $"msg-{index + 1}",
                Role: isAssistant ? "assistant" : "user",
                Content: isAssistant
                    ? $"Assistant response {index + 1} referencing /src/file{index + 1}.cs and prior tool output."
                    : $"User request {index + 1} with constraint {index + 1} and unresolved follow-up.",
                Timestamp: timestamp.AddMinutes(index).ToString("O"),
                Compact: false,
                Thinking: null,
                Metrics: null,
                Sequence: index + 1));
        }

        return new GatewaySession(
            Id: "session-1",
            TargetType: "agent",
            TargetId: "agent-1",
            OwnerUserId: "user-1",
            Title: "Compaction session",
            CreatedAt: timestamp.ToString("O"),
            UpdatedAt: timestamp.AddMinutes(messageCount).ToString("O"),
            Messages: messages,
            Compacted: false,
            CompactSummary: null);
    }

    private static CredentialResolverService BuildEmptyCredentialResolver()
        => new(new Lazy<Kautuka.Protos.CredentialsService.CredentialsServiceClient>(
            new FakeCredentialsServiceClient()));

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration configuration) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => configuration;
    }

    private sealed class RecordingAiClientProvider(string providerType, RecordingChatClient chatClient) : IAiClientProvider
    {
        public string ProviderType => providerType;

        public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey) => chatClient;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatClientMetadata metadata = new("recording", null, null);

        public string LastPrompt { get; private set; } = string.Empty;

        public ChatOptions? LastOptions { get; private set; }

        public ChatClientMetadata Metadata => metadata;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join(
                "\n",
                messages
                    .SelectMany(static message => message.Contents)
                    .OfType<TextContent>()
                    .Select(static content => content.Text));
            LastOptions = options;

            return Task.FromResult(new ChatResponse([]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? key = null) => null;
    }

    private sealed class FakeSessionStore(GatewaySession storedSession) : IGatewaySessionStore
    {
        public GatewaySession StoredSession { get; private set; } = storedSession;

        public bool TryGetSession(string sessionId, out GatewaySession? session)
        {
            if (!string.Equals(StoredSession.Id, sessionId, StringComparison.Ordinal))
            {
                session = null;
                return false;
            }

            session = StoredSession;
            return true;
        }

        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null)
        {
            if (!string.Equals(StoredSession.Id, sessionId, StringComparison.Ordinal))
            {
                return null;
            }

            StoredSession = StoredSession with
            {
                Compacted = true,
                CompactSummary = summary,
                LastCompactionReason = reason,
                LastCompactedAt = DateTimeOffset.UtcNow.ToString("O"),
            };

            return StoredSession;
        }

        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => throw new NotSupportedException();
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => throw new NotSupportedException();
        public GatewaySession? ArchiveSession(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => throw new NotSupportedException();
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => throw new NotSupportedException();
        public bool DeleteSession(string sessionId) => throw new NotSupportedException();
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null) => throw new NotSupportedException();
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteMemoryFact(string factId) => throw new NotSupportedException();
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => throw new NotSupportedException();
        public IReadOnlyList<GatewayRun> ListRuns() => throw new NotSupportedException();
        public bool TryGetRun(string runId, out GatewayRun? run) => throw new NotSupportedException();
        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null) => throw new NotSupportedException();
        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => throw new NotSupportedException();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath;

        private TemporaryDatabase(string directoryPath, IConfiguration configuration)
        {
            this.directoryPath = directoryPath;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public static TemporaryDatabase Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-session-compaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                })
                .Build();

            return new TemporaryDatabase(directoryPath, configuration);
        }

        public void EnsureReady()
        {
            var database = new KautukaDatabase(Configuration);
            database.EnsureReady();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
