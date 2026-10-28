namespace Kautuka.Gateway.Infrastructure.Configuration;

internal interface ITopologyConfigurationWriter
{
    PlatformConfiguration SaveProvider(ProviderDefinition provider);

    PlatformConfiguration DeleteProvider(string providerId);

    PlatformConfiguration SaveModel(ModelDefinition model);

    PlatformConfiguration DeleteModel(string modelId);

    PlatformConfiguration SaveAgent(AgentRegistryEntry agent);

    PlatformConfiguration DeleteAgent(string agentId);

    PlatformConfiguration SaveMcpServer(McpServerDefinition server);

    PlatformConfiguration DeleteMcpServer(string serverId);

    PlatformConfiguration SaveSkill(SkillDefinition skill);

    PlatformConfiguration DeleteSkill(string skillId);
}
