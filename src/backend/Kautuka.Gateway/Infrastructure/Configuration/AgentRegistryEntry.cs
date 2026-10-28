namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed record AgentRegistryEntry(
    string Id,
    string Scope,
    bool Enabled,
    string OwnerUserId,
    string StorageLocatorKey);
