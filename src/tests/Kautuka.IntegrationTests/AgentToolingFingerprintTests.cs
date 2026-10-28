using System.Text.Json.Nodes;

namespace Kautuka.IntegrationTests;

public sealed class AgentToolingFingerprintTests
{
    [Fact]
    public void PersonaSystemPromptAndTestConnectionMetadataDoNotChangeFingerprint()
    {
        AgentDefinition baseline = CreateAgent();
        PlatformConfiguration config = CreateConfig("ghcr.io/example/mcp:v1", toolCount: 2, toolNames: ["search"]);

        AgentDefinition personaChanged = CreateAgent(systemPrompt: "other prompt", personaTone: "formal");
        PlatformConfiguration metadataChanged = CreateConfig("ghcr.io/example/mcp:v1", toolCount: 9, toolNames: ["other"], updatedAt: "2026-08-27T00:00:00.0000000+00:00");

        Assert.Equal(AgentToolingFingerprint.Compute(baseline, config), AgentToolingFingerprint.Compute(personaChanged, config));
        Assert.Equal(AgentToolingFingerprint.Compute(baseline, config), AgentToolingFingerprint.Compute(baseline, metadataChanged));
    }

    [Fact]
    public void BindingOrConnectorImageChangesFingerprint()
    {
        AgentDefinition agent = CreateAgent();
        PlatformConfiguration config = CreateConfig("ghcr.io/example/mcp:v1");
        string original = AgentToolingFingerprint.Compute(agent, config);

        AgentDefinition rebound = CreateAgent(connectorId: "other-mcp");
        PlatformConfiguration imageChanged = CreateConfig("ghcr.io/example/mcp:v2");

        Assert.NotEqual(original, AgentToolingFingerprint.Compute(rebound, config));
        Assert.NotEqual(original, AgentToolingFingerprint.Compute(agent, imageChanged));
    }

    [Fact]
    public void CaseVariantSkillIdsAreRejected()
    {
        var unrelated = new AgentDefinition
        {
            Id = "plain-agent",
            Name = "Plain",
            SkillAttachments =
            [
                new AgentSkillAttachmentDefinition
                {
                    SkillId = "roll-dice",
                    Mode = SkillAttachmentMode.Trusted,
                },
            ],
        };
        var config = new PlatformConfiguration
        {
            Skills =
            [
                new SkillDefinition { SkillId = "AuditSkill", PackageHash = "audit-upper" },
                new SkillDefinition { SkillId = "auditskill", PackageHash = "audit-lower" },
                new SkillDefinition { SkillId = "roll-dice", PackageHash = "dice" },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => AgentToolingFingerprint.Compute(unrelated, config));
    }

    [Fact]
    public void CallerAndEndpointChangeTheCacheFingerprint()
    {
        AgentDefinition agent = CreateAgent();
        PlatformConfiguration config = CreateConfig("ghcr.io/example/mcp:v1");
        string shared = AgentToolingFingerprint.Compute(agent, config);

        string first = AgentToolingFingerprint.WithCaller(shared, "user-a", [("docs-mcp", "https://a.example/mcp")]);
        string second = AgentToolingFingerprint.WithCaller(shared, "user-b", [("docs-mcp", "https://b.example/mcp")]);

        Assert.Equal(shared, AgentToolingFingerprint.WithCaller(shared, null, []));
        Assert.NotEqual(shared, first);
        Assert.NotEqual(first, second);
    }

    private static AgentDefinition CreateAgent(string connectorId = "docs-mcp", string systemPrompt = "hello", string personaTone = "warm")
        => new()
        {
            Id = "agent-alpha",
            Name = "Agent Alpha",
            SystemPrompt = systemPrompt,
            Persona = new JsonObject { ["tone"] = personaTone },
            McpBindings =
            [
                new AgentMcpBindingDefinition
                {
                    ConnectorId = connectorId,
                    Context = "OwnContext",
                },
            ],
        };

    private static PlatformConfiguration CreateConfig(
        string image,
        int? toolCount = 1,
        IReadOnlyList<string>? toolNames = null,
        string updatedAt = "2026-01-01T00:00:00.0000000+00:00")
        => new()
        {
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "docs-mcp",
                    Name = "Docs",
                    Transport = McpTransport.Container,
                    ContainerImage = image,
                    Enabled = true,
                    ToolCount = toolCount,
                    ToolNames = toolNames,
                    UpdatedAt = updatedAt,
                },
                new McpServerDefinition
                {
                    Id = "other-mcp",
                    Name = "Other",
                    Transport = McpTransport.Container,
                    ContainerImage = image,
                    Enabled = true,
                },
            ],
        };
}
