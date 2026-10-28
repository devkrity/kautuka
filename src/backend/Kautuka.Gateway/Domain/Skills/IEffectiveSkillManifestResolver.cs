namespace Kautuka.Gateway.Domain.Skills;

internal interface IEffectiveSkillManifestResolver
{
    IReadOnlyList<EffectiveSkillManifestEntry> Resolve(
        PlatformConfiguration configuration,
        AgentDefinition agentDefinition,
        string? actingUserId);
}
