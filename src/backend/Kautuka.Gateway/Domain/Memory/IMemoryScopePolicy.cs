namespace Kautuka.Gateway.Domain.Memory;

internal interface IMemoryScopePolicy
{
    IReadOnlyList<GatewayMemoryScope> GetReadScopes(string targetType, string targetId);

    GatewayMemoryScope GetWriteScope(string targetType, string targetId);
}
