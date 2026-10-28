namespace Kautuka.Gateway.Domain.Memory;

internal interface IAgentMemoryEmbeddingService
{
    bool EnqueuePersistFacts(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts);

    Task PersistFactsAsync(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts, CancellationToken cancellationToken = default);

    Task PersistFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default);

    Task DeleteFactAsync(GatewayMemoryFact fact, CancellationToken cancellationToken = default);
}
