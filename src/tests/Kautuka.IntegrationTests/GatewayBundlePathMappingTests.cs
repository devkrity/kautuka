using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewayBundlePathMappingTests
{
    [Fact]
    public async Task ManagedGatewayPathIsUsedForProbeMaterializationAndSandboxDigest()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-bundle-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            string host = Path.Combine(root, "gateway-data", "mcpb-bundles", "web", "bundle.mcpb");
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            await File.WriteAllTextAsync(host, "fixture-bundle-content", TestContext.Current.CancellationToken);
            var paths = Paths(root, "prod", true);
            var runner = new PodmanCommandRunner((args, _) => Task.FromResult(new PodmanCommandResult(0, args.Count > 1 && args[0] == "image" && args[1] == "inspect" ? "[{\"Id\":\"" + new string('b', 64) + "\",\"Os\":\"linux\",\"Architecture\":\"amd64\"}]" : "", "")));
            var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, runtimePaths: paths);
            var input = new WorkspaceMcpContainerConnectorDefinition("owner::web", "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "python", ["server.py"], true, false, "/data/mcpb-bundles/web/bundle.mcpb", "python", [], 30, 600);
            var mapped = materializer.ResolveGatewayBundle(input);
            Assert.Equal(host, mapped.BundlePath);
            Assert.Equal(AgentSandboxInstallContract.BuildInstallRoot(input with { BundlePath = host }), AgentSandboxInstallContract.BuildInstallRoot(mapped));
            // Reconciliation uses this controlled image resolver before it inspects an existing container.
            Assert.Equal(materializer.ResolveControlledRuntimeConnector(input with { BundlePath = host }).Image,
                materializer.ResolveControlledRuntimeConnector(input).Image);
            using var egress = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var controlled = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, egressRuntime: egress, runtimePaths: paths);
            var reconciled = await controlled.ResolveControlledExecutionAsync(input, materialize: false, null, TestContext.Current.CancellationToken);
            Assert.Equal(materializer.ResolveControlledRuntimeConnector(input with { BundlePath = host }).Image, reconciled.Image);
            await using var service = new WorkspaceContainerConnectorExecutionService(new WorkspaceDesiredStateStore(), runner, materializer,
                new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
            MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService).GetMethod("PrepareRuntimeConnectorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = (Task<WorkspaceMcpContainerConnectorDefinition>)method.Invoke(service, [input, TestContext.Current.CancellationToken, null])!;
            WorkspaceMcpContainerConnectorDefinition prepared = await pending;
            Assert.StartsWith("localhost/kautuka-mcpb-", prepared.Image, StringComparison.Ordinal);
            Assert.Null(prepared.BundlePath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("/data/mcpb-bundles/../private/file.mcpb")]
    [InlineData("/data/mcpb-bundles/./file.mcpb")]
    [InlineData("/data/mcpb-bundles/a//file.mcpb")]
    [InlineData("/data/mcpb-bundles/a\\..\\file.mcpb")]
    [InlineData("/data/mcpb-bundles/C:/file.mcpb")]
    public void ManagedPathsRejectTraversal(string input)
        => Assert.Throws<InvalidOperationException>(() => McpbImageMaterializer.ResolveGatewayBundlePath(input, Path.GetTempPath()));

    [Fact]
    public void OtherPathsAndDevelopmentHostModeRemainUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-bundle-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("/data/mcpb-bundles-extra/a", McpbImageMaterializer.ResolveGatewayBundlePath("/data/mcpb-bundles-extra/a", root));
            Assert.Equal("/tmp/existing.mcpb", McpbImageMaterializer.ResolveGatewayBundlePath("/tmp/existing.mcpb", root));
            var runner = new PodmanCommandRunner();
            var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, runtimePaths: Paths(root, "dev", false));
            var connector = new WorkspaceMcpContainerConnectorDefinition("x", "image", "python", [], true, false, "/data/mcpb-bundles/a", "python", [], 30, 600);
            Assert.Equal(connector.BundlePath, materializer.ResolveGatewayBundle(connector).BundlePath);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SymlinkInsideManagedBundleTreeIsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation requires additional privileges.
        string root = Path.Combine(Path.GetTempPath(), "kautuka-bundle-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            string bundles = Path.Combine(root, "gateway-data", "mcpb-bundles");
            Directory.CreateDirectory(bundles);
            Directory.CreateSymbolicLink(Path.Combine(bundles, "escape"), Path.GetTempPath());
            Assert.Throws<InvalidOperationException>(() => McpbImageMaterializer.ResolveGatewayBundlePath("/data/mcpb-bundles/escape/secret", root));
            string outside = Path.Combine(root, "outside.mcpb");
            File.WriteAllText(outside, "outside");
            File.CreateSymbolicLink(Path.Combine(bundles, "linked.mcpb"), outside);
            Assert.Throws<InvalidOperationException>(() => McpbImageMaterializer.ResolveGatewayBundlePath("/data/mcpb-bundles/linked.mcpb", root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WorkspaceRuntimePaths Paths(string root, string mode, bool managed) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:Mode"] = mode, ["Kautuka:ContainerStateRoot"] = root, ["CoreServices:Enabled"] = managed.ToString() }).Build(),
        new HostEnvironment(), NullLogger<WorkspaceRuntimePaths>.Instance);

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
