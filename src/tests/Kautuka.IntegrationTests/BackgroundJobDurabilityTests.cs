using Kautuka.Gateway.Domain.Jobs;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Usage;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
namespace Kautuka.IntegrationTests;

public sealed class BackgroundJobDurabilityTests
{
    [Fact]
    public void EveryHostedGatewayWorkerHasAnExplicitDeliveryClassification()
    {
        HashSet<string> classified = BackgroundWorkDeliveryCatalog.All
            .Select(static item => item.WorkerName)
            .ToHashSet(StringComparer.Ordinal);
        string[] hostedWorkers = [.. typeof(GatewayAppBootstrap).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(IHostedService).IsAssignableFrom(type))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)];

        Assert.NotEmpty(hostedWorkers);
        Assert.DoesNotContain(hostedWorkers, worker => !classified.Contains(worker));
        Assert.Equal(BackgroundWorkDeliveryCatalog.All.Count, classified.Count);
    }

    [Fact]
    public void UsageStagingSurvivesQueueReconstructionAndCompletesIdempotently()
    {
        using var fixture = new DatabaseFixture();
        KautukaDatabase database = fixture.CreateGatewayDatabase();
        database.EnsureReady();
        GatewayUsageFact expected = CreateUsageFact("run-1");

        using (var producer = new GatewayUsageQueue(database))
        {
            Assert.True(producer.TryEnqueue(expected));
            Assert.True(producer.TryEnqueue(expected));
        }

        using var recovered = new GatewayUsageQueue(database);
        Assert.True(recovered.TryDequeue(out GatewayUsageFact? actual));
        Assert.Equal(expected.RunId, actual!.RunId);
        recovered.Complete(actual);
        Assert.False(recovered.TryDequeue(out _));
    }

    [Fact]
    public void MemoryExtractionLeaseIsRecoveredAfterOwnerFailure()
    {
        using var fixture = new DatabaseFixture();
        KautukaDatabase database = fixture.CreateGatewayDatabase();
        database.EnsureReady();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-14T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var first = new MemoryExtractionJobStore(database, clock);
        Assert.True(first.Enqueue("agent", "agent-1", "session-1", "user", "assistant"));

        MemoryExtractionJob claimed = Assert.IsType<MemoryExtractionJob>(first.ClaimNext("worker-1", TimeSpan.FromMinutes(5)));
        var restarted = new MemoryExtractionJobStore(database, clock);
        Assert.Null(restarted.ClaimNext("worker-2", TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(6));
        MemoryExtractionJob recovered = Assert.IsType<MemoryExtractionJob>(restarted.ClaimNext("worker-2", TimeSpan.FromMinutes(5)));
        Assert.Equal(claimed.Id, recovered.Id);
        Assert.Equal(2, recovered.AttemptCount);
        restarted.Complete(recovered.Id);
        Assert.Null(restarted.ClaimNext("worker-2", TimeSpan.FromMinutes(5)));
        Assert.True(restarted.Enqueue("agent", "agent-1", "session-1", "user", "assistant"));
        Assert.Null(restarted.ClaimNext("worker-2", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void EmbeddingQueueRejectsOverflowWithoutSilentlyDroppingAcceptedWork()
    {
        var queue = new AgentMemoryEmbeddingQueue();
        GatewayMemoryFact fact = new("fact-1", "agent", "agent-1", "user-1", "title", "content", "category", "test", 1, "now", "now");
        for (int i = 0; i < 128; i++)
        {
            Assert.True(queue.TryEnqueue(new AgentMemoryEmbeddingWork(AgentMemoryEmbeddingOperation.Persist, fact with { Id = $"fact-{i}" })));
        }

        Assert.False(queue.TryEnqueue(new AgentMemoryEmbeddingWork(AgentMemoryEmbeddingOperation.Persist, fact with { Id = "overflow" })));
        for (int i = 0; i < 128; i++) Assert.True(queue.TryDequeue(out _));
        Assert.False(queue.TryDequeue(out _));
    }

    private static GatewayUsageFact CreateUsageFact(string runId) => new(
        Id: $"usage-{runId}", UsageFactVersion: 1, RunId: runId, SessionId: "session-1",
        TargetType: "agent", TargetId: "agent-1", UserId: "user-1", UsernameSnapshot: "user",
        UserDisplayNameSnapshot: null, AgentId: "agent-1", AgentNameSnapshot: "Agent",
        ModelId: "model-1", ModelNameSnapshot: "Model", ProviderId: "provider-1",
        ProviderNameSnapshot: "Provider", RunStatus: "completed", SourceChannel: "web",
        RunStartedAt: "2026-07-14T00:00:00Z", FirstModelOutputAt: "2026-07-14T00:00:01Z",
        RunEndedAt: "2026-07-14T00:00:02Z", TotalDurationMs: 2000, FirstTokenLatencyMs: 1000,
        TokenIn: 10, TokenOut: 20, TotalTokens: 30, ProviderRequestCount: 1, ModelTurnCount: 1,
        AssistantTurnCount: 1, ToolCallCount: 0, CostBasis: null, InputCost: null, OutputCost: null,
        TotalCost: null, CostCurrency: null, PricingSource: null, UsageDate: "2026-07-14",
        CreatedAt: "2026-07-14T00:00:02Z");

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-job-tests", Guid.NewGuid().ToString("N"));
        private readonly IConfiguration configuration;

        public DatabaseFixture()
        {
            Directory.CreateDirectory(root);
            configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
                .Build();
        }

        public KautukaDatabase CreateGatewayDatabase() => new(configuration);
        public KautukaUsageDatabase CreateUsageDatabase() => new(configuration);

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
