using System.Threading.Channels;

namespace Kautuka.Gateway.Domain.Memory;

internal enum AgentMemoryEmbeddingOperation
{
    Persist,
    Delete,
}

internal sealed record AgentMemoryEmbeddingWork(AgentMemoryEmbeddingOperation Operation, GatewayMemoryFact Fact);

internal interface IAgentMemoryEmbeddingQueue
{
    bool TryEnqueue(AgentMemoryEmbeddingWork work);

    bool TryDequeue(out AgentMemoryEmbeddingWork? work);

    IAsyncEnumerable<AgentMemoryEmbeddingWork> DequeueAllAsync(CancellationToken cancellationToken);
}

internal sealed class AgentMemoryEmbeddingQueue : IAgentMemoryEmbeddingQueue
{
    private readonly Channel<AgentMemoryEmbeddingWork> channel = Channel.CreateBounded<AgentMemoryEmbeddingWork>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(AgentMemoryEmbeddingWork work) => channel.Writer.TryWrite(work);

    public bool TryDequeue(out AgentMemoryEmbeddingWork? work)
    {
        bool success = channel.Reader.TryRead(out AgentMemoryEmbeddingWork? value);
        work = success ? value : null;
        return success;
    }

    public IAsyncEnumerable<AgentMemoryEmbeddingWork> DequeueAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed partial class AgentMemoryEmbeddingReconciliationService(
    GatewayState state,
    IAgentDefinitionStore agentDefinitionStore,
    IAgentMemoryEmbeddingService embeddingService,
    IPlatformConfigurationProvider configProvider,
    ILogger<AgentMemoryEmbeddingReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!configProvider.GetCurrent().Memory.EmbeddingsEnabled)
        {
            return;
        }

        foreach ((string agentId, string storageLocator) in state.AgentStorageLocators.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayMemoryFact[] facts = [.. state.MemoryStore.List("agent", agentId)];
            HashSet<string> authoritativeIds = facts.Select(static fact => fact.Id).ToHashSet(StringComparer.Ordinal);
            IReadOnlyDictionary<string, AgentMemoryEmbeddingState> existing = agentDefinitionStore.ListMemoryEmbeddingStates(storageLocator);
            foreach (string existingId in existing.Keys)
            {
                if (!authoritativeIds.Contains(existingId))
                {
                    agentDefinitionStore.DeleteMemoryEmbedding(storageLocator, existingId);
                }
            }

            string? expectedModelKey = ResolveModelKey(agentId);
            GatewayMemoryFact[] missingOrStale = [.. facts.Where(fact =>
                !existing.TryGetValue(fact.Id, out AgentMemoryEmbeddingState? embedding)
                || !string.Equals(embedding.UpdatedAt, fact.UpdatedAt, StringComparison.Ordinal)
                || (expectedModelKey is not null && !string.Equals(embedding.ModelKey, expectedModelKey, StringComparison.Ordinal)))];
            await embeddingService.PersistFactsAsync("agent", agentId, missingOrStale, cancellationToken).ConfigureAwait(false);
            LogReconciled(logger, agentId, facts.Length, missingOrStale.Length);
        }
    }

    private string? ResolveModelKey(string agentId)
    {
        if (!state.Agents.TryGetValue(agentId, out GatewayAgent? agent)
            || !state.Models.TryGetValue(agent.ModelId, out GatewayModel? model))
        {
            return null;
        }
        return $"{model.ProviderId}:{model.ModelName}";
    }

    [LoggerMessage(EventId = 172, Level = LogLevel.Information, Message = "Reconciled memory embeddings for agent {AgentId}; authoritativeFactCount={FactCount}, refreshedCount={RefreshedCount}")]
    private static partial void LogReconciled(ILogger logger, string agentId, int factCount, int refreshedCount);
}

internal sealed partial class AgentMemoryEmbeddingDispatcherService(
    IAgentMemoryEmbeddingQueue queue,
    IAgentMemoryEmbeddingService embeddingService,
    ILogger<AgentMemoryEmbeddingDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (AgentMemoryEmbeddingWork work in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    switch (work.Operation)
                    {
                        case AgentMemoryEmbeddingOperation.Persist:
                            await embeddingService.PersistFactAsync(work.Fact, stoppingToken).ConfigureAwait(false);
                            break;
                        case AgentMemoryEmbeddingOperation.Delete:
                            await embeddingService.DeleteFactAsync(work.Fact, stoppingToken).ConfigureAwait(false);
                            break;
                        default:
                            LogUnknownOperation(logger, work.Operation, work.Fact.Id, work.Fact.TargetId);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Provider timeouts arrive as TaskCanceledException; do not stop the host.
                    LogEmbeddingWorkFailed(logger, ex, work.Operation, work.Fact.Id, work.Fact.TargetId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(EventId = 170, Level = LogLevel.Warning, Message = "Embedding reconciliation failed for {Operation} on fact {FactId} for agent {AgentId}")]
    private static partial void LogEmbeddingWorkFailed(ILogger logger, Exception exception, AgentMemoryEmbeddingOperation operation, string factId, string agentId);

    [LoggerMessage(EventId = 171, Level = LogLevel.Warning, Message = "Embedding reconciliation skipped unknown operation {Operation} for fact {FactId} on agent {AgentId}")]
    private static partial void LogUnknownOperation(ILogger logger, AgentMemoryEmbeddingOperation operation, string factId, string agentId);
}
