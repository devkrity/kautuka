namespace Kautuka.Gateway.Infrastructure.Agents;

internal interface IAgentToolSurfaceStore
{
    AgentToolSurfaceSnapshot? TryGet(string agentId, string fingerprint);

    void Save(string agentId, string fingerprint, AgentToolSurfaceCatalogue catalogue);
}
