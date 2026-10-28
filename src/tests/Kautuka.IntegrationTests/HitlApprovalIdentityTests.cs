namespace Kautuka.IntegrationTests;

public sealed class HitlApprovalIdentityTests
{
    [Fact]
    public void ResolveMcpServerIdUsesAskFilterWhenExactlyOneConnectorGatesTheTool()
    {
        PlatformConfiguration config = CreateConfig(
            requireMcpIds: ["github", "slack"],
            filters: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["github"] = ["create_issue"],
                ["slack"] = ["post_message"],
            });

        Assert.Equal("github", HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "create_issue"));
        Assert.Equal("slack", HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "post_message"));
        Assert.Null(HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "run_shell"));
    }

    [Fact]
    public void ResolveMcpServerIdReturnsNullWhenTwoConnectorsBothGateTheSameToolName()
    {
        PlatformConfiguration config = CreateConfig(
            requireMcpIds: ["alpha", "beta"],
            filters: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["alpha"] = ["search"],
                ["beta"] = ["search"],
            });

        Assert.Null(HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "search"));
    }

    [Fact]
    public void ResolveMcpServerIdFallsBackToDiscoveredToolNamesWhenPolicyDoesNotGate()
    {
        PlatformConfiguration config = CreateConfig(
            requireMcpIds: [],
            filters: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            toolNames: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["github"] = ["create_issue"],
                ["slack"] = ["post_message"],
            });

        Assert.Equal("github", HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "create_issue"));
    }

    [Fact]
    public void ResolveMcpServerIdFallsBackToUniqueCatalogToolOwner()
    {
        var config = new PlatformConfiguration
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = "agent-1",
                    Name = "Agent One",
                    McpBindings = [],
                    ApprovalPolicy = new ApprovalPolicy(),
                },
            ],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "kautuka-web-tools",
                    Name = "Web",
                    ToolNames = ["kautuka_web_fetch"],
                },
                new McpServerDefinition
                {
                    Id = "markitdown",
                    Name = "MarkItDown",
                    ToolNames = ["convert"],
                },
            ],
        };

        Assert.Equal(
            "kautuka-web-tools",
            HitlApprovalIdentity.ResolveMcpServerId(config, "agent-1", "kautuka_web_fetch"));
    }

    [Fact]
    public void TryReadSkillIdReadsCanonicalArgument()
    {
        Assert.Equal("repo-inspector", HitlApprovalIdentity.TryReadSkillId("""{"skillId":"repo-inspector","scriptPath":"run.py"}"""));
        Assert.Null(HitlApprovalIdentity.TryReadSkillId("""{"path":"run.py"}"""));
        Assert.Null(HitlApprovalIdentity.TryReadSkillId("not-json"));
    }

    private static PlatformConfiguration CreateConfig(
        IReadOnlyList<string> requireMcpIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? toolNames = null)
    {
        string[] connectorIds = [.. requireMcpIds.Concat(filters.Keys).Concat(toolNames?.Keys ?? []).Distinct(StringComparer.Ordinal)];
        return new PlatformConfiguration
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = "agent-1",
                    Name = "Agent One",
                    McpBindings = [.. connectorIds.Select(id => new AgentMcpBindingDefinition { ConnectorId = id })],
                    ApprovalPolicy = new ApprovalPolicy
                    {
                        RequireMcpIds = requireMcpIds,
                        RequireMcpFunctionFilters = filters,
                    },
                },
            ],
            McpServers =
            [
                .. connectorIds.Select(id => new McpServerDefinition
                {
                    Id = id,
                    Name = id,
                    ToolNames = toolNames is not null && toolNames.TryGetValue(id, out IReadOnlyList<string>? names)
                        ? names
                        : [id],
                }),
            ],
        };
    }
}
