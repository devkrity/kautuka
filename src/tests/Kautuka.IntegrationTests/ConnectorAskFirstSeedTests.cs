using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorAskFirstSeedTests
{
    [Fact]
    public void FirstSeedAddsAskMeFirst()
    {
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        GatewayApprovalConfigPayload? next = ConnectorAskFirstSeed.Next(
            new GatewayApprovalConfigPayload(),
            "kautuka-m365-mail",
            "mail_send",
            seeded,
            "agent-1");

        Assert.NotNull(next);
        Assert.Equal(["mail_send"], next.RequireMcpFunctionFilters["kautuka-m365-mail"]);
        Assert.Contains("agent-1", seeded);
    }

    [Fact]
    public void SeededAgentKeepsAllowed()
    {
        var seeded = new HashSet<string>(StringComparer.Ordinal) { "agent-1" };

        GatewayApprovalConfigPayload? next = ConnectorAskFirstSeed.Next(
            new GatewayApprovalConfigPayload(),
            "kautuka-m365-mail",
            "mail_send",
            seeded,
            "agent-1");

        Assert.Null(next);
    }

    [Fact]
    public void BlockedToolIsMarkedSeededWithoutAddingAsk()
    {
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        var current = new GatewayApprovalConfigPayload
        {
            DisabledMcpFunctions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["kautuka-m365-mail"] = ["mail_send"],
            },
        };

        GatewayApprovalConfigPayload? next = ConnectorAskFirstSeed.Next(
            current,
            "kautuka-m365-mail",
            "mail_send",
            seeded,
            "agent-1");

        Assert.Null(next);
        Assert.Contains("agent-1", seeded);
    }
}
