namespace Kautuka.Gateway.Domain.Memory;

internal sealed record AgentMemorySearchHit(
    string FactId,
    double Score,
    string UpdatedAt);
