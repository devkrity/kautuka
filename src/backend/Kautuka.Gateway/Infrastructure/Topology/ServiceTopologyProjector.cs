namespace Kautuka.Gateway.Infrastructure.Topology;

internal sealed class ServiceTopologyProjector(IPlatformConfigurationProvider configurationProvider)
{
    public PlatformTopology Snapshot()
    {
        PlatformConfiguration configuration = configurationProvider.GetCurrent();

        return new PlatformTopology(
            GatewayServiceName: "gateway",
            AgentServices: [.. configuration.Agents.Select(agent => new WorkerServiceRegistration($"agent-{agent.Id}", agent.Id, "agent"))]);
    }
}

internal sealed record PlatformTopology(
    string GatewayServiceName,
    IReadOnlyList<WorkerServiceRegistration> AgentServices);

internal sealed record WorkerServiceRegistration(string ServiceName, string DefinitionId, string Kind);
