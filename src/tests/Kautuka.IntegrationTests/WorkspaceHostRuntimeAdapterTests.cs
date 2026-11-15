using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceHostRuntimeAdapterTests
{
    [Fact]
    public async Task MissingSandboxImageBuildsDirectlyWithoutTemporaryInstaller()
    {
        var commands = new List<string[]>();
        var podman = new PodmanCommandRunner((arguments, _) =>
        {
            string[] captured = arguments.ToArray();
            commands.Add(captured);
            int exitCode = captured.SequenceEqual(["image", "exists", "localhost/kautuka-sandbox:latest"]) ? 1 : 0;
            return Task.FromResult(new PodmanCommandResult(
                exitCode,
                string.Empty,
                string.Empty,
                exitCode == 0 ? ProcessTerminationReason.Completed : ProcessTerminationReason.NonZeroExit));
        });
        var adapter = new ManagerRuntimeAdapter(
            podman,
            new AlwaysAvailableHealthProbe(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagerRuntimeAdapter>.Instance);

        string? error = await adapter.PrepareSandboxImageAsync(
            new Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition { Image = "kautuka-sandbox:latest" },
            TestContext.Current.CancellationToken);

        Assert.Null(error);
        string[] build = Assert.Single(commands, command => command[0] == "build");
        Assert.Contains("-t", build);
        Assert.Contains("localhost/kautuka-sandbox:latest", build);
        Assert.Contains("--label", build);
        Assert.DoesNotContain(commands, command => command[0] is "run" or "commit" or "network");
    }

    [Fact]
    public async Task CurrentSandboxImageSkipsBuildAndInstaller()
    {
        string dockerfile = Path.Combine(AppContext.BaseDirectory, "sandbox", "Dockerfile");
        string currentHash = ManagerRuntimeAdapter.ComputeSandboxSourceHash(dockerfile);
        var commands = new List<string[]>();
        var podman = new PodmanCommandRunner((arguments, _) =>
        {
            string[] captured = arguments.ToArray();
            commands.Add(captured);
            string stdout = captured[0] == "image" && captured.ElementAtOrDefault(1) == "inspect" ? currentHash : string.Empty;
            return Task.FromResult(new PodmanCommandResult(0, stdout, string.Empty, ProcessTerminationReason.Completed));
        });
        var adapter = new ManagerRuntimeAdapter(
            podman,
            new AlwaysAvailableHealthProbe(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagerRuntimeAdapter>.Instance);

        string? error = await adapter.PrepareSandboxImageAsync(
            new Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition { Image = "kautuka-sandbox:latest" },
            TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.DoesNotContain(commands, command => command[0] is "build" or "run" or "commit" or "network");
    }

    [Fact]
    public void SandboxBuildUsesBakedUvAndDoesNotCreateTemporaryInstallerContainer()
    {
        IReadOnlyList<string> arguments = ManagerRuntimeAdapter.BuildSandboxImageArguments(
            "localhost/kautuka-sandbox:local",
            "sandbox/Dockerfile",
            "sandbox",
            "source-hash");

        Assert.Equal(
        [
            "build",
            "-t", "localhost/kautuka-sandbox:local",
            "--label", "io.kautuka.sandbox.source-hash=source-hash",
            "-f", "sandbox/Dockerfile",
            "sandbox",
        ],
            arguments);
        Assert.DoesNotContain(arguments, argument => argument is "run" or "commit" or "network");
        Assert.DoesNotContain(arguments, argument => argument.Contains("pip", StringComparison.OrdinalIgnoreCase));
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

    private sealed class AlwaysAvailableHealthProbe : IManagerRuntimeHealthProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
