using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ControlledMcpMaterializationTests
{
    [Fact]
    public void PythonOnlyCallerIndexesSupplyTheActualVenvInstaller()
    {
        var indexes = McpbImageMaterializer.ResolveEgressIndexEnvironment(
            [new("PIP_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/")], "python");
        Assert.Contains(indexes, pair => pair.Key == "UV_INDEX_URL" && pair.Value == "http://kautuka-pkgs-proxy:20101/pypi/simple/");
        Assert.Throws<InvalidOperationException>(() => McpbImageMaterializer.ResolveEgressIndexEnvironment(
            [new("PIP_INDEX_URL", "https://pypi.org/simple/")], "python"));
        Assert.Contains("uv venv", McpbImageMaterializer.BuildEgressInstallScript("python"), StringComparison.Ordinal);
        var launch = AgentSandboxInstallContract.RewriteLaunch("python", "python3", ["/bundle/server.py"], "/bundle", true);
        Assert.Equal("/bundle/.venv/bin/python", launch.Command);
        foreach (string command in new[] { "/usr/bin/python3", "/usr/local/bin/python", "python3.14" })
            Assert.Equal("/bundle/.venv/bin/python", AgentSandboxInstallContract.RewriteLaunch("python", command, [], "/bundle", true).Command);
        Assert.Equal("/custom/python3", AgentSandboxInstallContract.RewriteLaunch("python", "/custom/python3", [], "/bundle", true).Command);
    }

    [Fact]
    public void OfflineSetupDoesNotInstallLanguagePackagesAndRejectsUnsafeRoot()
    {
        string setup = McpbImageMaterializer.BuildEgressSystemContainerfile("localhost/kautuka-base:fixture", "node", "/opt/kautuka/mcpb", true);
        Assert.Contains("COPY --chown=1000:1000 bundle/", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN npm", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("microdnf", setup, StringComparison.Ordinal);
        Assert.Contains("python3 -I -c", McpbImageMaterializer.BuildEgressSystemContainerfile("localhost/kautuka-base:fixture", "binary", "/opt/kautuka/mcpb", true), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => McpbImageMaterializer.BuildEgressSystemContainerfile("localhost/kautuka-base:fixture", "node", "/tmp/root\nRUN false", true));
    }

    [Fact]
    public void OwnerProjectionDoesNotTreatSharedAgentAsSharedConnectorAuthority()
    {
        var owner = Workspace("a");
        Assert.True(WorkspaceContainerConnectorExecutionService.ConnectorBelongsToWorkspace("a::mail", owner));
        Assert.False(WorkspaceContainerConnectorExecutionService.ConnectorBelongsToWorkspace("b::mail", owner));
        Assert.False(WorkspaceContainerConnectorExecutionService.ConnectorBelongsToWorkspace("system::mail", owner));
        Assert.False(WorkspaceContainerConnectorExecutionService.ConnectorBelongsToWorkspace("mail", owner));
        Assert.True(WorkspaceContainerConnectorExecutionService.ConnectorBelongsToWorkspace("mail", Workspace("system")));
    }

    [Fact]
    public async Task PublicProvisioningRejectsForeignOwnerBeforeResolvingOrBuilding()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No container operation may occur."));
        await using var service = new WorkspaceContainerConnectorExecutionService(new WorkspaceDesiredStateStore(), runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new Kautuka.Manager.RuntimeContainer.Sandbox.PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAgentSandboxProvisionedAsync(Workspace("a"), ["b::mail"], TestContext.Current.CancellationToken));
        Assert.Contains("another user's connector", error.Message, StringComparison.Ordinal);
    }

    private static ContainerContext Workspace(string owner) => new("agent", "shared", owner, "/tmp/workspace", "/workspace", "localhost/kautuka-base:fixture", 1, "512m", 20, [], new Dictionary<string, string>());
}
