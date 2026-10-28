namespace Kautuka.IntegrationTests;

public sealed class TopologyProjectionTests
{
    [Fact]
    public void TopologyProjectionCreatesWorkerRegistrationsFromConfiguration()
    {
        var provider = new TestPlatformConfigurationProvider();
        var projector = new ServiceTopologyProjector(provider);

        var topology = projector.Snapshot();

        Assert.Equal("gateway", topology.GatewayServiceName);
        Assert.Contains(topology.AgentServices, service => service.ServiceName == "agent-fasterBrain");
        Assert.Contains(topology.AgentServices, service => service.ServiceName == "agent-simpleBrain");
    }

    private sealed class TestPlatformConfigurationProvider : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => new()
        {
            Providers =
            [
                new ProviderDefinition { Id = "local", Type = "openai", Settings = new Dictionary<string, object?>() },
            ],
            Models =
            [
                new ModelDefinition { Id = "phi", ProviderId = "local", ModelName = "phi", Settings = new Dictionary<string, object?>() },
            ],
            Agents =
            [
                new AgentDefinition { Id = "fasterBrain", Name = "Faster Brain", ModelId = "phi", SystemPrompt = "Fast", SuperExecMode = "trusted" },
                new AgentDefinition { Id = "simpleBrain", Name = "Simple Brain", ModelId = "phi", SystemPrompt = "Simple", SuperExecMode = "trusted" },
            ],
        };
    }
}
