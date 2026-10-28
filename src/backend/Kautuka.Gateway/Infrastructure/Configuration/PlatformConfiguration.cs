using System.Collections.ObjectModel;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class PlatformConfiguration
{
    public Collection<ProviderDefinition> Providers { get; init; } = [];

    public Collection<ModelDefinition> Models { get; init; } = [];

    public Collection<AgentDefinition> Agents { get; init; } = [];

    public Collection<McpServerDefinition> McpServers { get; init; } = [];

    public Collection<SkillDefinition> Skills { get; init; } = [];

    public InfrastructureDefinition Kautuka { get; init; } = new();

    public SystemDefinition System { get; init; } = new();

    public ProviderPathPolicySettings ProviderPathPolicy { get; init; } = new();

    public MemorySettings Memory { get; init; } = new();
}
