using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceHostRuntimeAdapterTests
{
    [Fact]
    public async Task ManagedPreparationUsesTheReleasedConfigIdWithoutLegacyPullOrBuild()
    {
        string source = "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
        string config = "sha256:" + new string('b', 64);
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            Assert.Equal(config, args[^1]);
            return Task.FromResult(new PodmanCommandResult(0, args[1] == "inspect"
                ? "[{\"Id\":\"" + config[7..] + "\",\"Os\":\"linux\",\"Architecture\":\"" + architecture + "\"}]" : "", ""));
        });
        var inventory = new RuntimeImageAllowlist(1, [new("kautuka-base", source, "unused.oci", new string('0', 64), "linux/" + architecture, config)]);
        using var egress = new ContainerEgressRuntime(runner, new Kautuka.Manager.RuntimeContainer.Configuration.ContainerEgressOptions { Enabled = true }, inventory, Path.GetTempPath());
        var adapter = new ManagerRuntimeAdapter(runner, new AvailableRuntime(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagerRuntimeAdapter>.Instance, inventory, null, egress);
        Assert.Null(await adapter.PrepareSandboxImageAsync(new Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition
        { Image = "localhost/kautuka-base:latest" }, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls.Count);
        Assert.All(calls, args => Assert.Equal("image", args[0]));
    }

    private sealed class AvailableRuntime : IManagerRuntimeHealthProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [Theory]
    [InlineData("ABC123", "ABC123", true)]
    [InlineData("abc123", "ABC123", true)]
    [InlineData("DEF456", "ABC123", false)]
    [InlineData(null, "ABC123", false)]
    [InlineData("", "ABC123", false)]
    public void IsSandboxImageSourceCurrentReturnsExpectedResult(string? currentSourceHash, string expectedSourceHash, bool expected)
    {
        bool actual = ManagerRuntimeAdapter.IsSandboxImageSourceCurrent(currentSourceHash, expectedSourceHash);

        Assert.Equal(expected, actual);
    }
}
