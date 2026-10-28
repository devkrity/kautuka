using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewaySessionStoreCompactionEstimateTests
{
    [Fact]
    public void CompactSessionShrinksEffectiveEstimateButPreservesRawTranscriptEstimate()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = CreateStore(state);

        GatewaySession session = store.CreateSession("agent", "ops-agent", "user-123");
        session = AppendTranscript(store, session, pairCount: 12);

        int beforeCompactionEffective = Assert.IsType<int>(session.EstimatedContextTokens);
        int beforeCompactionRaw = Assert.IsType<int>(session.RawTranscriptEstimatedTokens);

        GatewaySession compacted = Assert.IsType<GatewaySession>(store.CompactSession(
            session.Id,
            "manual",
            "Session summary\nA compact summary of earlier work.\n\nDurable facts\n- Important detail.\n\nOpen threads\n- Pending follow-up."));

        Assert.True(compacted.Compacted);
        Assert.True(Assert.IsType<int>(compacted.EstimatedContextTokens) < beforeCompactionEffective);
        Assert.Equal(beforeCompactionRaw, compacted.RawTranscriptEstimatedTokens);
        Assert.True(Assert.IsType<int>(compacted.RawTranscriptEstimatedTokens) > Assert.IsType<int>(compacted.EstimatedContextTokens));
    }

    [Fact]
    public void AppendingMessagesAfterCompactionGrowsReducedEffectiveBaselineInsteadOfRevertingToRawTranscriptEstimate()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = CreateStore(state);

        GatewaySession session = store.CreateSession("agent", "ops-agent", "user-123");
        session = AppendTranscript(store, session, pairCount: 12);
        GatewaySession compacted = Assert.IsType<GatewaySession>(store.CompactSession(
            session.Id,
            "manual",
            "Session summary\nCompressed earlier history.\n\nDurable facts\n- Keep key facts.\n\nOpen threads\n- One unresolved task."));

        int compactedEffective = Assert.IsType<int>(compacted.EstimatedContextTokens);
        int compactedRaw = Assert.IsType<int>(compacted.RawTranscriptEstimatedTokens);

        GatewaySession grown = store.AppendMessages(
            compacted.Id,
            new GatewayMessage(
                "msg-user-next",
                "user",
                "Continue with the next step and record the final command. Include the updated rollout checklist, the exact podman invocation, the file path /workspace/src/Deployment/ApplyUpdate.cs, the rollback note, and the pending verification steps that still need follow-up after compaction.",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null),
            new GatewayMessage(
                "msg-assistant-next",
                "assistant",
                "Next step recorded with the final command and outcome. The final command was podman exec manager apply-update --channel beta --confirm, the verification output was successful, and the unresolved follow-up is to inspect /workspace/logs/update-run.log plus confirm the retained runtime session metadata after compaction.",
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                null,
                null));

        Assert.True(grown.Compacted);
        Assert.True(Assert.IsType<int>(grown.EstimatedContextTokens) > compactedEffective);
        Assert.True(Assert.IsType<int>(grown.EstimatedContextTokens) < Assert.IsType<int>(grown.RawTranscriptEstimatedTokens));
        Assert.True(Assert.IsType<int>(grown.RawTranscriptEstimatedTokens) > compactedRaw);
    }

    private static GatewaySession AppendTranscript(GatewaySessionStore store, GatewaySession session, int pairCount)
    {
        GatewaySession current = session;
        for (int index = 0; index < pairCount; index++)
        {
            current = store.AppendMessages(
                current.Id,
                new GatewayMessage(
                    $"msg-user-{index}",
                    "user",
                    $"User request {index} with a detailed file path /workspace/src/File{index}.cs and a command dotnet test --filter Case{index}.",
                    DateTimeOffset.UtcNow.AddMinutes(index).ToString("O"),
                false,
                null,
                null),
                new GatewayMessage(
                    $"msg-assistant-{index}",
                    "assistant",
                    $"Assistant response {index} with tool output, next action, and a concrete decision that should survive compaction.",
                    DateTimeOffset.UtcNow.AddMinutes(index).AddSeconds(30).ToString("O"),
                    false,
                    null,
                    null,
                    ToolCalls: [new GatewayToolCall($"call-{index}", "run_shell", "ops-agent", "{\"command\":\"echo test\"}", "ok", "done", "superexec")]));
        }

        return current;
    }

    private static GatewaySessionStore CreateStore(GatewayState state)
        => new(
            state,
            new NoOpGatewayContinuationStore(),
            new NoOpConversationAssetStore(),
            new NoOpAgentMemoryEmbeddingQueue(),
            new SessionTitleService(),
            NullLogger<GatewaySessionStore>.Instance,
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Memory = new MemorySettings
                {
                    InjectionTokenBudget = 1024,
                },
            }));

    private static GatewayState CreateState(string sqlitePath)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(config);
        database.EnsureReady();

        return GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(config),
            config,
            new NoOpWorkspaceRuntimeTool());
    }

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
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

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string directoryPath;

        public TemporaryGatewayStateDirectory()
        {
            directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-session-store-compaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
        }

        public string SqlitePath => Path.Combine(directoryPath, "kautuka.db");

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
