using Kautuka.IntegrationTests.Fixtures;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;

using WorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;
using SandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;

namespace Kautuka.IntegrationTests;

public sealed class ContainerEgressIntegrationTests
{
    [Fact]
    public void ScopeSeparatesOwnersAgentsAndManagementWithoutUsingMountPermissions()
    {
        ContainerContext alpha = Workspace("alpha", "owner");
        ContainerEgressRuntime.Scope first = ContainerEgressRuntime.ConnectorScope("owner::mail", alpha);
        Assert.Equal(first, ContainerEgressRuntime.ConnectorScope("owner::another", alpha));
        Assert.NotEqual(first.NetworkName, ContainerEgressRuntime.ConnectorScope("owner::mail", Workspace("beta", "owner")).NetworkName);
        Assert.NotEqual(first.NetworkName, ContainerEgressRuntime.ConnectorScope("other::mail", Workspace("alpha", "other")).NetworkName);
        Assert.NotEqual(ContainerEgressRuntime.ConnectorScope("owner::mail", null).NetworkName,
            ContainerEgressRuntime.ConnectorScope("owner::mail", Workspace("management", "owner")).NetworkName);
        Assert.Throws<InvalidOperationException>(() => ContainerEgressRuntime.ConnectorScope("other::mail", alpha));
    }

    [Theory]
    [InlineData("bundle")]
    [InlineData("language")]
    [InlineData("agent-sandbox")]
    public async Task UnintegratedPathsNeverStartLegacyContainersOrDiagnosticRetries(string path)
    {
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            throw new InvalidOperationException("No Podman operation should occur for this unsupported path.");
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        var connector = new WorkspaceMcpContainerConnectorDefinition("owner::test", "localhost/kautuka-fixture:test", "", [], true, false,
            path == "bundle" ? "/fixture.mcpb" : null, path == "language" ? "node" : null, [], 20, 600);
        ContainerContext workspace = Workspace("alpha", "owner");
        var store = new WorkspaceDesiredStateStore();
        store.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new WorkspaceDefinition { BaseRoot = Path.GetTempPath() }, new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition()), [workspace], [connector], []);
        await using var service = new WorkspaceContainerConnectorExecutionService(store, runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner, runtime),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, runtime);
        var result = await service.ListToolsAsync("owner::test", null, 20, false, workspace,
            path == "agent-sandbox" ? WorkspaceMcpExecutionContext.AgentSandbox : WorkspaceMcpExecutionContext.Isolated,
            TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.NotNull(result.FailureMessage);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task SupervisorProvisioningCannotStartLegacyMcpArtifactInstallContainers()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No legacy build/run may occur."));
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        await using var service = new WorkspaceContainerConnectorExecutionService(new WorkspaceDesiredStateStore(), runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner, runtime),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, runtime);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAgentSandboxProvisionedAsync(
            Workspace("alpha", "owner"), ["owner::mail"], TestContext.Current.CancellationToken));
        Assert.Contains("legacy install containers are disabled", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentShellRequiresCanonicalAgentScopeBeforeAnyPodmanOperation()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No Podman command is allowed."));
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        var tool = new PodmanContainerTool(runner, runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.RunShellAsync(Workspace("alpha", "owner") with { ScopeType = "user" }, "true", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposableLinuxFixtureRunsTheActualManagerLifecycle()
    {
        string? input = Environment.GetEnvironmentVariable("KAUTUKA_MANAGER_EGRESS_FIXTURE");
        Assert.SkipWhen(!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(input), "Requires the disposable rootless Linux topology fixture.");
        var report = await ContainerEgressManagerFixture.RunAsync(input!, TestContext.Current.CancellationToken);
        Assert.True(report["passed"]!.GetValue<bool>());
    }

    private static ContainerContext Workspace(string agent, string owner)
        => new("agent", agent, owner, Path.Combine(Path.GetTempPath(), "kautuka-egress-scope", owner, agent), "/workspace",
            "localhost/kautuka-fixture:test", 1, "512m", 20, [], new Dictionary<string, string>());
}
