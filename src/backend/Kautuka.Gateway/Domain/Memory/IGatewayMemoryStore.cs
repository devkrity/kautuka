namespace Kautuka.Gateway.Domain.Memory;

internal interface IGatewayMemoryStore
{
    void Restore(IReadOnlyList<GatewayMemoryFact> facts);

    IReadOnlyList<GatewayMemoryFact> Snapshot();

    IReadOnlyList<GatewayMemoryFact> List(string targetType, string targetId);

    bool TryGet(string factId, out GatewayMemoryFact? fact);

    void AddRange(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts);

    bool Remove(string factId);

}
