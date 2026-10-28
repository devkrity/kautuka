namespace Kautuka.Gateway.Domain.Skills;

internal sealed class EffectiveSkillManifestResolver : IEffectiveSkillManifestResolver
{
    public IReadOnlyList<EffectiveSkillManifestEntry> Resolve(
        PlatformConfiguration configuration,
        AgentDefinition agentDefinition,
        string? actingUserId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(agentDefinition);

        Dictionary<string, SkillDefinition> skills = SkillIdentity.Index(configuration.Skills, static skill => skill.SkillId);
        Dictionary<string, AgentSkillAttachmentDefinition> attachments = SkillIdentity.Index(
            agentDefinition.SkillAttachments.Where(static attachment => attachment.Mode != SkillAttachmentMode.Disabled),
            static attachment => attachment.SkillId);
        return [.. attachments.Values
            .Select(attachment => TryCreateAgentEntry(agentDefinition.Id, attachment, skills, actingUserId))
            .Where(static entry => entry is not null)
            .Cast<EffectiveSkillManifestEntry>()];
    }

    private static EffectiveSkillManifestEntry? TryCreateAgentEntry(
        string agentId,
        AgentSkillAttachmentDefinition attachment,
        Dictionary<string, SkillDefinition> skills,
        string? actingUserId)
    {
        if (!skills.TryGetValue(attachment.SkillId, out SkillDefinition? skill))
        {
            return null;
        }

        if (!skill.Enabled)
        {
            return null;
        }

        if (!GatewayOwnershipAccess.CallerCanUse(actingUserId, skill.Scope, skill.OwnerUserId))
        {
            return null;
        }

        return new EffectiveSkillManifestEntry
        {
            SkillId = skill.SkillId,
            SourceScope = EffectiveSkillSourceScope.Agent,
            SourceOwnerId = agentId,
            Mode = attachment.Mode,
            HasResources = skill.HasResources,
            HasScripts = skill.HasScripts,
            Name = skill.Name,
            Description = skill.Description,
            PackageHash = skill.PackageHash,
            ImportTimestamp = skill.ImportTimestamp,
        };
    }

}
