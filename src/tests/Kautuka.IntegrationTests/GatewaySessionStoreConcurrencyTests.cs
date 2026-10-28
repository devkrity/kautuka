using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewaySessionStoreConcurrencyTests
{
    [Fact]
    public async Task ConcurrentSessionWritersPreserveEveryMessageWithUniqueContiguousSequences()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession session = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] tasks = Enumerable.Range(0, 20)
            .SelectMany(index => new[]
            {
                Task.Run(async () =>
                {
                    await start.Task;
                    fixture.Store.AppendUserMessage(session.Id, Message($"parent-{index}", "user", "parent"));
                }),
                Task.Run(async () =>
                {
                    await start.Task;
                    fixture.Store.AppendAssistantMessage(session.Id, Message($"approval-{index}", "assistant", "approval"));
                }),
                Task.Run(async () =>
                {
                    await start.Task;
                    fixture.Store.AppendMessages(
                        session.Id,
                        Message($"channel-{index}", "user", "channel"),
                        Message($"aaat-{index}", "assistant", "aaat"));
                }),
            })
            .ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);

        Assert.True(fixture.Store.TryGetSession(session.Id, out GatewaySession? actual));
        Assert.NotNull(actual);
        Assert.Equal(80, actual.Messages.Count);
        Assert.Equal(80, actual.Messages.Select(static message => message.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(80, actual.Messages.Select(static message => message.Sequence).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 80), actual.Messages.Select(static message => message.Sequence).Order());
        Assert.Equal(["aaat", "approval", "channel", "parent"], actual.Messages.Select(static message => message.Source).Distinct().Order());

        GatewaySession persistedSession = Assert.Single(fixture.State.AgentRuntimeSessionRepository.ListRuntimeSessions(fixture.AgentId));
        Assert.Equal(actual.Messages.Select(static message => message.Id).Order(), persistedSession.Messages.Select(static message => message.Id).Order());
        Assert.Equal(Enumerable.Range(1, 80), persistedSession.Messages.Select(static message => message.Sequence));
    }

    [Fact]
    public async Task ConcurrentRetryOfSameMessageIdIsIdempotent()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession session = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123");
        GatewayMessage retriedMessage = Message("retry-message", "assistant", "hitl");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                fixture.Store.AppendAssistantMessage(session.Id, retriedMessage);
            }))
            .ToArray();

        start.SetResult();
        await Task.WhenAll(tasks);

        Assert.True(fixture.Store.TryGetSession(session.Id, out GatewaySession? actual));
        GatewayMessage stored = Assert.Single(Assert.IsType<GatewaySession>(actual).Messages);
        Assert.Equal("retry-message", stored.Id);
        Assert.Equal(1, stored.Sequence);

        GatewaySession persistedSession = Assert.Single(fixture.State.AgentRuntimeSessionRepository.ListRuntimeSessions(fixture.AgentId));
        Assert.Single(persistedSession.Messages);
    }

    [Fact]
    public void RuntimeSessionMessageSchemaRejectsDuplicateSequenceAndMessageId()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession session = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123");
        fixture.Store.AppendUserMessage(session.Id, Message("first", "user", "parent"));

        using var connection = new SqliteConnection($"Data Source={fixture.AgentDatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_session_messages (session_id, sequence, message_id, message_json)
            VALUES ($sessionId, 1, 'different-id', '{}');
            """;
        command.Parameters.AddWithValue("$sessionId", session.Id);

        SqliteException exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void SessionProjectionReconstructsFromAuthoritativeRepository()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession created = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123", "autopilot");
        _ = fixture.Store.AppendMessages(
            created.Id,
            Message("rebuild-user", "user", "channel"),
            Message("rebuild-assistant", "assistant", "aaat"));
        _ = fixture.Store.UpdateSessionTitle(created.Id, "Durable title");
        _ = fixture.Store.CompactSession(created.Id, "test", "Durable compact summary");
        _ = fixture.Store.ArchiveSession(created.Id);
        GatewayRun createdRun = fixture.Store.AppendRun("agent", fixture.AgentId, "user-123", created.Id, "input", "output");
        GatewayMemoryFact createdFact = MemoryFact("durable-fact", fixture.AgentId, created.Id);
        fixture.State.UpsertAgentMemoryFacts("agent", fixture.AgentId, [createdFact]);

        GatewayState reloadedState = fixture.ReloadState();

        GatewaySession reloaded = Assert.Single(reloadedState.Sessions.Values);
        Assert.Equal(created.Id, reloaded.Id);
        Assert.Equal("Durable title", reloaded.Title);
        Assert.Equal("autopilot", reloaded.Originator);
        Assert.True(reloaded.Compacted);
        Assert.Equal("Durable compact summary", reloaded.CompactSummary);
        Assert.NotNull(reloaded.ArchivedAt);
        Assert.Equal(["rebuild-user", "rebuild-assistant"], reloaded.Messages.Select(static message => message.Id));
        Assert.Equal(createdRun.Id, Assert.Single(reloadedState.Runs).Id);
        Assert.Equal(createdFact.Id, Assert.Single(reloadedState.MemoryStore.List("agent", fixture.AgentId)).Id);
    }

    [Fact]
    public void FailedAuthoritativeUpdateDoesNotMutateSessionProjection()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession created = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123");
        using IDisposable failure = fixture.BlockAgentDatabase();

        _ = Assert.ThrowsAny<Exception>(() => fixture.Store.UpdateSessionTitle(created.Id, "Must not appear"));

        Assert.True(fixture.Store.TryGetSession(created.Id, out GatewaySession? projected));
        Assert.Equal(created.Title, Assert.IsType<GatewaySession>(projected).Title);
    }

    [Fact]
    public void FailedAuthoritativeRunCreateDoesNotMutateRunProjection()
    {
        using var fixture = new SessionStoreFixture();
        GatewaySession session = fixture.Store.CreateSession("agent", fixture.AgentId, "user-123");
        using IDisposable failure = fixture.BlockAgentDatabase();

        _ = Assert.ThrowsAny<Exception>(() => fixture.Store.AppendRun("agent", fixture.AgentId, "user-123", session.Id, "input", "output"));

        Assert.Empty(fixture.Store.ListRuns());
    }

    [Fact]
    public void FailedAuthoritativeMemoryUpsertDoesNotMutateMemoryProjection()
    {
        using var fixture = new SessionStoreFixture();
        GatewayMemoryFact fact = MemoryFact("failed-fact", fixture.AgentId, null);
        using IDisposable failure = fixture.BlockAgentDatabase();

        _ = Assert.ThrowsAny<Exception>(() => fixture.State.UpsertAgentMemoryFacts("agent", fixture.AgentId, [fact]));

        Assert.False(fixture.State.MemoryStore.TryGet(fact.Id, out _));
    }

    private static GatewayMessage Message(string id, string role, string source)
        => new(
            id,
            role,
            $"content-{id}",
            DateTimeOffset.UtcNow.ToString("O"),
            Compact: false,
            Thinking: null,
            Metrics: null,
            Source: source,
            Sequence: 999);

    private static GatewayMemoryFact MemoryFact(string id, string agentId, string? sessionId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayMemoryFact(id, "agent", agentId, "user-123", "Title", "Content", "profile", "test", 1.0, now, now, sessionId);
    }

    private sealed class SessionStoreFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-session-concurrency-tests", Guid.NewGuid().ToString("N"));
        private readonly IConfiguration configuration;

        public SessionStoreFixture()
        {
            Directory.CreateDirectory(root);
            configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = root,
                })
                .Build();

            var database = new KautukaDatabase(configuration);
            database.EnsureReady();
            var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
            State = GatewayState.FromDatabase(
                database,
                NullLogger<GatewayState>.Instance,
                new GatewayMemoryStore(),
                definitionStore,
                configuration,
                new NoOpWorkspaceRuntimeTool());

            State.Agents[AgentId] = new GatewayAgent(
                AgentId,
                "Concurrency Agent",
                "model-test",
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
            State.AgentStorageLocators[AgentId] = AgentId;
            ((IAtomicAgentDefinitionStore)State.AgentDefinitionStore).SaveDefinitionAndRegistryAtomically(
                new AgentRegistryEntry(
                    AgentId,
                    GatewayOwnershipScopes.User,
                    Enabled: true,
                    OwnerUserId: "user-123",
                    StorageLocatorKey: AgentId),
                new AgentDefinition
                {
                    Id = AgentId,
                    Scope = GatewayOwnershipScopes.User,
                    Enabled = true,
                    Name = "Concurrency Agent",
                    ModelId = "model-test",
                    SystemPrompt = "prompt",
                    SuperExecMode = "trusted",
                });

            Store = new GatewaySessionStore(
                State,
                new NoOpGatewayContinuationStore(),
                new NoOpConversationAssetStore(),
                new NoOpAgentMemoryEmbeddingQueue(),
                new SessionTitleService(),
                NullLogger<GatewaySessionStore>.Instance);
        }

        public string AgentId { get; } = "concurrency-agent";

        public GatewayState State { get; }

        public GatewaySessionStore Store { get; }

        public string AgentDatabasePath => Path.Combine(root, "agents", AgentId, "agent.db");

        public GatewayState ReloadState()
        {
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

        public IDisposable BlockAgentDatabase()
        {
            string backupPath = $"{AgentDatabasePath}.backup";
            File.Move(AgentDatabasePath, backupPath);
            Directory.CreateDirectory(AgentDatabasePath);
            return new RestoreDatabaseFile(AgentDatabasePath, backupPath);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class RestoreDatabaseFile(string blockedPath, string backupPath) : IDisposable
        {
            public void Dispose()
            {
                Directory.Delete(blockedPath);
                File.Move(backupPath, blockedPath);
            }
        }
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => Context(agentId, userId);
        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => Context(agentId, userId);
        public bool DeleteAgentWorkspace(string agentId) => true;
        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(Sandbox(agentId, userId));
        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));
        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(Sandbox(agentId, userId));
        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static WorkspaceRuntimeContext Context(string agentId, string? userId)
            => new("agent", agentId, userId, Path.GetTempPath(), "/workspace", "/workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession Sandbox(string agentId, string? userId)
            => new($"agent-{agentId}", false, "agent", agentId, userId);
    }
}
