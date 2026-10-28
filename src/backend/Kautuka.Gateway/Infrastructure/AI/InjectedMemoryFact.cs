namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed record InjectedMemoryFact(
    string SourceScope,
    string SourceId,
    string Title,
    string Content,
    string Category,
    string UpdatedAt);
