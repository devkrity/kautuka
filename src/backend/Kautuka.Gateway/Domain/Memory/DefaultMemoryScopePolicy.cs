namespace Kautuka.Gateway.Domain.Memory;

internal sealed class DefaultMemoryScopePolicy : IMemoryScopePolicy
{
    public IReadOnlyList<GatewayMemoryScope> GetReadScopes(string targetType, string targetId)
    {
        return [new GatewayMemoryScope("agent", targetId)];
    }

    public GatewayMemoryScope GetWriteScope(string targetType, string targetId)
    {
        return new GatewayMemoryScope("agent", targetId);
    }
}
