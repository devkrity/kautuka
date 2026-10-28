using System.Text.Json.Nodes;

namespace Kautuka.IntegrationTests;

public sealed class AgentDefinitionRevisionDifferTests
{
    [Fact]
    public void DiffReturnsOnlyChangedNameSetting()
    {
        IReadOnlyList<AgentDefinitionSettingChange> changes = AgentDefinitionRevisionDiffer.Diff(
            [
                Snapshot(1, "name", "Name", "\"Alpha\""),
                Snapshot(1, "scope", "Scope", "\"user\""),
            ],
            [
                Snapshot(2, "name", "Name", "\"Alpha v2\""),
                Snapshot(2, "scope", "Scope", "\"user\""),
            ]);

        AgentDefinitionSettingChange change = Assert.Single(changes);
        Assert.Equal("name", change.SettingId);
        Assert.Equal("Name", change.SettingName);
        Assert.Equal("\"Alpha\"", change.Before?.ToJsonString());
        Assert.Equal("\"Alpha v2\"", change.After?.ToJsonString());
    }

    [Fact]
    public void DiffReturnsMcpBindingsWhenConnectorIsAdded()
    {
        IReadOnlyList<AgentDefinitionSettingChange> changes = AgentDefinitionRevisionDiffer.Diff(
            [Snapshot(1, "mcp_bindings", "MCP bindings", "[]")],
            [Snapshot(2, "mcp_bindings", "MCP bindings", """[{"connectorId":"mcp-shell","connectorName":"Shell Tools","context":"OwnContext"}]""")]);

        AgentDefinitionSettingChange change = Assert.Single(changes);
        Assert.Equal("mcp_bindings", change.SettingId);
        Assert.IsType<JsonArray>(change.After);
    }

    [Fact]
    public void DiffReturnsEmptyForIdenticalSnapshots()
    {
        IReadOnlyList<AgentDefinitionSettingSnapshot> snapshot =
        [
            Snapshot(1, "name", "Name", "\"Alpha\""),
            Snapshot(1, "session", "Session settings", """{"historyEnabled":true,"maxHistoryRuns":5}"""),
        ];

        Assert.Empty(AgentDefinitionRevisionDiffer.Diff(snapshot, snapshot));
    }

    [Fact]
    public void DiffIgnoresObjectKeyOrder()
    {
        IReadOnlyList<AgentDefinitionSettingChange> changes = AgentDefinitionRevisionDiffer.Diff(
            [Snapshot(1, "session", "Session settings", """{"maxHistoryRuns":5,"historyEnabled":true}""")],
            [Snapshot(2, "session", "Session settings", """{"historyEnabled":true,"maxHistoryRuns":5}""")]);

        Assert.Empty(changes);
    }

    private static AgentDefinitionSettingSnapshot Snapshot(long revision, string id, string name, string valueJson)
        => new(revision, id, name, valueJson);
}
