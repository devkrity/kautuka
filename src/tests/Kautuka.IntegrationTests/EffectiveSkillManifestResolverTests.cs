using Kautuka.Gateway.Domain.Skills;

namespace Kautuka.IntegrationTests;

public sealed class EffectiveSkillManifestResolverTests
{
    private readonly EffectiveSkillManifestResolver resolver = new();

    [Fact]
    public void ResolveStandaloneIncludesOnlyEnabledAgentSkills()
    {
        PlatformConfiguration configuration = new()
        {
            Skills =
            [
                CreateSkill("docs-skill", hasResources: true, hasScripts: false),
                CreateSkill("shell-skill", hasResources: true, hasScripts: true),
                CreateSkill("disabled-catalog-skill", hasResources: true, hasScripts: true, enabled: false),
            ],
        };

        AgentDefinition agent = new()
        {
            Id = "agent-a",
            SkillAttachments =
            [
                new AgentSkillAttachmentDefinition { SkillId = "docs-skill", Mode = SkillAttachmentMode.Trusted },
                new AgentSkillAttachmentDefinition { SkillId = "shell-skill", Mode = SkillAttachmentMode.RequireApproval },
                new AgentSkillAttachmentDefinition { SkillId = "disabled-catalog-skill", Mode = SkillAttachmentMode.Trusted },
                new AgentSkillAttachmentDefinition { SkillId = "disabled-skill", Mode = SkillAttachmentMode.Disabled },
            ],
        };

        IReadOnlyList<EffectiveSkillManifestEntry> manifest = resolver.Resolve(configuration, agent, actingUserId: null);

        Assert.Collection(
            manifest.OrderBy(entry => entry.SkillId, StringComparer.Ordinal),
            entry =>
            {
                Assert.Equal("docs-skill", entry.SkillId);
                Assert.Equal(EffectiveSkillSourceScope.Agent, entry.SourceScope);
                Assert.Equal(SkillAttachmentMode.Trusted, entry.Mode);
                Assert.True(entry.HasResources);
                Assert.False(entry.HasScripts);
            },
            entry =>
            {
                Assert.Equal("shell-skill", entry.SkillId);
                Assert.Equal(EffectiveSkillSourceScope.Agent, entry.SourceScope);
                Assert.Equal(SkillAttachmentMode.RequireApproval, entry.Mode);
                Assert.True(entry.HasResources);
                Assert.True(entry.HasScripts);
            });
    }

    [Fact]
    public void ResolveOmitsAnotherUsersSkill()
    {
        PlatformConfiguration configuration = new()
        {
            Skills =
            [
                CreateSkill("shared-skill", hasResources: true, hasScripts: false),
                CreateSkill("own-skill", hasResources: true, hasScripts: true, scope: "user", ownerUserId: "user-a"),
                CreateSkill("foreign-skill", hasResources: true, hasScripts: true, scope: "user", ownerUserId: "user-b"),
            ],
        };

        AgentDefinition agent = new()
        {
            Id = "agent-a",
            SkillAttachments =
            [
                new AgentSkillAttachmentDefinition { SkillId = "shared-skill", Mode = SkillAttachmentMode.Trusted },
                new AgentSkillAttachmentDefinition { SkillId = "own-skill", Mode = SkillAttachmentMode.Trusted },
                new AgentSkillAttachmentDefinition { SkillId = "foreign-skill", Mode = SkillAttachmentMode.Trusted },
            ],
        };

        IReadOnlyList<EffectiveSkillManifestEntry> manifest = resolver.Resolve(configuration, agent, "user-a");

        Assert.Equal(["own-skill", "shared-skill"], manifest.Select(entry => entry.SkillId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void ResolveRejectsSkillIdsThatDifferOnlyByCase()
    {
        PlatformConfiguration configuration = new()
        {
            Skills =
            [
                CreateSkill("docs-skill", hasResources: true, hasScripts: false),
                CreateSkill("Docs-Skill", hasResources: false, hasScripts: true),
            ],
        };

        AgentDefinition agent = new()
        {
            Id = "agent-a",
            SkillAttachments =
            [
                new AgentSkillAttachmentDefinition { SkillId = "docs-skill", Mode = SkillAttachmentMode.Trusted },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => resolver.Resolve(configuration, agent, actingUserId: null));
    }

    private static SkillDefinition CreateSkill(
        string skillId,
        bool hasResources,
        bool hasScripts,
        bool enabled = true,
        string scope = "system",
        string? ownerUserId = null)
        => new()
        {
            SkillId = skillId,
            Name = skillId,
            Description = $"Description for {skillId}",
            ImportTimestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PackageHash = $"hash-{skillId}",
            Status = "ready",
            PackagePath = $"data/skills/{skillId}",
            HasResources = hasResources,
            HasScripts = hasScripts,
            Enabled = enabled,
            Scope = scope,
            OwnerUserId = ownerUserId,
        };
}
