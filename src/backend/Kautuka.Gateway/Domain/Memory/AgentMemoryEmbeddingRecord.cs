namespace Kautuka.Gateway.Domain.Memory;

internal sealed record AgentMemoryEmbeddingRecord(
    string FactId,
    string TargetType,
    string TargetId,
    string ModelKey,
    int Dimensions,
    float[] Vector,
    string SourceText,
    string CreatedAt,
    string UpdatedAt);

internal sealed record AgentMemoryEmbeddingState(string ModelKey, string UpdatedAt);
