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

    void SwitchMcpCredentialAssociation(string serverId, string credentialNamespace, string expectedSource, string associationId, string operationId) => throw new NotSupportedException();

    PlatformConfiguration DeleteMcpServer(string serverId);

    PlatformConfiguration ClearMcpServerDiscovery(string serverId) => throw new NotSupportedException();

    PlatformConfiguration SaveSkill(SkillDefinition skill);

    PlatformConfiguration DeleteSkill(string skillId);
}
