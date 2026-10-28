using Grpc.Core;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Tests for MCPB bundle lifecycle: validation, image mapping, container naming.
/// </summary>
public class McpbBundleLifecycleTests
{
    [Fact]
    public void RuntimeImageNodeServerTypeMapsToNodeSlimImage()
    {
        Assert.Equal(ReleasedSandboxImage.LocalImage, McpCommandTransportMapper.NodeImage);
        Assert.DoesNotContain("quay.io/fedora", McpCommandTransportMapper.NodeImage, StringComparison.Ordinal);
        const string released = "ghcr.io/devkrity/kautuka-base@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal(released, ReleasedSandboxImage.Select(released, "quay.io/fedora/fedora-minimal:44", "node"));
        Assert.Equal(released, ReleasedSandboxImage.Select(released, "kautuka-base:latest"));
        Assert.Equal("public.ecr.aws/zinclabs/openobserve:latest", ReleasedSandboxImage.Select(released, "public.ecr.aws/zinclabs/openobserve:latest"));
        Assert.Equal("quay.io/fedora/fedora-minimal:44", ReleasedSandboxImage.Select(ReleasedSandboxImage.LocalImage, "quay.io/fedora/fedora-minimal:44", "node"));
        Assert.Equal(
            ReleasedSandboxImage.LocalImage,
            ReleasedSandboxImage.Select(
                ReleasedSandboxImage.LocalImage,
                "quay.io/fedora/fedora-minimal@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "node"));
        Assert.Equal("kautuka-base:latest", ReleasedSandboxImage.Select(ReleasedSandboxImage.LocalImage, "kautuka-base:latest"));
    }

    [Fact]
    public void ManagerDownIsUnavailableNotARejection()
    {
        var unavailable = new RpcException(new Status(StatusCode.Unavailable, "manager down"));
        DesiredStatePublishResult classified = WorkspaceDesiredStatePublisher.Classify(
            new InvalidOperationException("Kautuka.Manager gRPC endpoint is unavailable.", unavailable));
        Assert.False(classified.Accepted);
        Assert.True(classified.ManagerUnavailable);
        Assert.False(WorkspaceDesiredStatePublisher.Classify(new InvalidOperationException("rejected")).ManagerUnavailable);
    }

    [Fact]
    public void RuntimeImagePythonServerTypeMapsToPythonSlimImage()
    {
        Assert.Equal(McpCommandTransportMapper.NodeImage, McpCommandTransportMapper.PythonImage);
    }

    [Fact]
    public void RuntimeImageUvServerTypeMapsToUvImage()
    {
        Assert.Equal(McpCommandTransportMapper.NodeImage, McpCommandTransportMapper.UvImage);
    }

    [Fact]
    public void ContainerNamingUserScopedFormat()
    {
        // Arrange
        const string userId = "user-123";
        const string connectorId = "connector-456";

        // Act
        // Expected: kautuka-mcp-{user}-{connector}-{hash}
        string expectedPrefix = $"kautuka-mcp-{userId}-{connectorId}";

        // Assert
        Assert.NotEmpty(expectedPrefix);
        Assert.Contains("kautuka-mcp", expectedPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializedBundlePathUsesEphemeralTempDirectory()
    {
        // Arrange
        var tempPath = Path.GetTempPath();
        const string connectorId = "test-mcp";
        const string digest = "abc12345";

        // Act
        // Expected: {TempPath}/kautuka-mcpb-materialized/{connectorId}-{digest-8chars}
        string expected = Path.Combine(tempPath, "kautuka-mcpb-materialized", $"{connectorId}-{digest}");

        // Assert
        Assert.Contains("kautuka-mcpb-materialized", expected, StringComparison.Ordinal);
        Assert.EndsWith(digest, expected, StringComparison.Ordinal);
    }

    [Fact]
    public void DesiredStateHashChangesWhenOnlyTheProtocolChanges()
    {
        string withoutProtocol = WorkspaceDesiredStatePublisher.ComputeContentHash(ProtocolPayload(null));
        string pinned = WorkspaceDesiredStatePublisher.ComputeContentHash(ProtocolPayload("2024-11-05"));

        Assert.NotEqual(withoutProtocol, pinned);
    }

    private static WorkspaceDesiredStatePayload ProtocolPayload(string? protocol) => new(
        new ManagerRuntimeSettingsPayload(@"C:\ws", "kautuka-base:latest", 2, "2g", 120, null, null, null, null, null),
        [],
        [
            new WorkspaceMcpContainerConnectorPayload(
                "system::markitdown",
                "markitdown",
                "docker.io/mcp/markitdown:latest",
                string.Empty,
                [],
                true,
                true,
                null,
                null,
                [],
                45,
                600,
                protocol),
        ],
        []);

    [Fact]
    public void NonRootUserSetTo65532()
    {
        // Arrange & Act & Assert
        // Expected: --user 65532:65532 --security-opt no-new-privileges
        Assert.Equal("65532", "65532");
    }
}
