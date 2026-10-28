namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Authoritative store for durable agent memory facts. GatewayMemoryStore is a reconstructible projection.
/// </summary>
internal interface IAgentMemoryFactRepository
{
    IReadOnlyList<GatewayMemoryFact> ListMemoryFacts(string storageLocatorKey);

    void UpsertMemoryFacts(string storageLocatorKey, IReadOnlyList<GatewayMemoryFact> facts);

    bool DeleteMemoryFact(string storageLocatorKey, string factId);
}
