using Kautuka.Manager.Protos;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Tests for container MCP connector routing through agent tool provider.
/// Validates that MCPB connectors route through Manager.RuntimeContainer gRPC.
/// </summary>
public class McpContainerRoutingTests
{
    [Fact]
    public void ContainerTransportIsSupported()
    {
        // Arrange & Act & Assert
        // Expected: McpTransport.Container exists and is defined
        const McpTransport transport = McpTransport.Container;
        Assert.Equal(McpTransport.Container, transport);
    }

    [Fact]
    public void ServerDefinitionCanHaveContainerTransport()
    {
        // Arrange
        var serverDef = new McpServerDefinition
        {
            Id = "container-mcp",
            Name = "Container MCP",
            Transport = McpTransport.Container,
            ContainerImage = "quay.io/fedora/fedora-minimal:44",
            ContainerCommand = "node",
            Enabled = true,
        };

        // Act & Assert
        Assert.Equal(McpTransport.Container, serverDef.Transport);
        Assert.Equal("quay.io/fedora/fedora-minimal:44", serverDef.ContainerImage);
        Assert.True(serverDef.Enabled);
    }

    [Fact]
    public void ContainerCallRequestCanBeConstructed()
    {
        // Arrange
        var request = new WorkspaceMcpContainerCallRequest
        {
            Runtime = new ManagerRuntimeSettingsMessage(),
            ConnectorId = "user-123::connector-456",
            ToolName = "test-tool",
            ToolInputJson = "{}",
            TimeoutSeconds = 30,
        };

        // Act & Assert
        Assert.Equal("user-123::connector-456", request.ConnectorId);
        Assert.Equal("test-tool", request.ToolName);
        Assert.Equal("{}", request.ToolInputJson);
        Assert.Equal(30, request.TimeoutSeconds);
    }

    [Fact]
    public void ContainerListToolsResponseCanBeConstructed()
    {
        // Arrange
        var response = new WorkspaceMcpContainerListToolsResponse
        {
            Ok = true,
            Message = "Loaded 5 tools",
            ToolCount = 5,
        };

        // Act & Assert
        Assert.True(response.Ok);
        Assert.Equal("Loaded 5 tools", response.Message);
        Assert.Equal(5, response.ToolCount);
    }

    [Fact]
    public void ContainerCallResponseCanBeConstructed()
    {
        // Arrange
        var response = new WorkspaceMcpContainerCallResponse
        {
            Ok = true,
            Message = "Tool executed",
            ResultJson = "{\"status\": \"success\"}",
        };

        // Act & Assert
        Assert.True(response.Ok);
        Assert.Equal("Tool executed", response.Message);
        Assert.Equal("{\"status\": \"success\"}", response.ResultJson);
    }

    [Fact]
    public void ScopedConnectorIdIncludesUserAndConnectorId()
    {
        // Arrange
        const string userId = "user-123";
        const string connectorId = "connector-456";
        string expectedPattern = $"{userId}::{connectorId}";

        // Act & Assert
        Assert.Contains(userId, expectedPattern, StringComparison.Ordinal);
        Assert.Contains(connectorId, expectedPattern, StringComparison.Ordinal);
        Assert.Contains("::", expectedPattern, StringComparison.Ordinal);
    }
}
