using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_LiveQuarantineRestartTests
{
    [Fact]
    public async Task RealPodmanQuarantineSurvivesRegistryRebuild()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("KAUTUKA_TEST_LIVE_NAC_RESTART") != "1")
            Assert.Skip("Opt in on a disposable rootless Podman store to run the live quarantine test.");

        var ct = TestContext.Current.CancellationToken;
        var real = new PodmanCommandRunner();
        const string network = "kautuka-workload";
        const string ip = "10.247.1.21";
        string container = "nac-live-restart-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "nac-live-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool networkCreated = false;
        string? containerId = null;
        try
        {
            PodmanCommandResult exists = await real.RunAsync(["network", "exists", network], 10, ct);
            if (exists.ExitCode == 0) Assert.Skip("The workload network already exists; refusing to alter it.");
            Assert.Equal(1, exists.ExitCode);
            PodmanCommandResult created = await real.RunAsync(["network", "create", "--internal", "--subnet", "10.247.0.0/16", network], 30, ct);
            Assert.True(created.ExitCode == 0, created.StdErr);
            networkCreated = true;
            PodmanCommandResult started = await real.RunAsync([
                "run", "-d", "--name", container, "--network", network, "--ip", ip,
                "--user", "65532:65532", "--cap-drop", "ALL", "--read-only",
                "--label", "io.kautuka.user=test-user", "--label", "io.kautuka.agent=test-agent",
                "--label", "io.kautuka.kind=agent-sandbox", "--label", "io.kautuka.exec-context=agent-sandbox",
                "--label", "io.kautuka.egress=normal", "--entrypoint", "/bin/sleep",
                "docker.io/curlimages/curl:8.11.1", "300"], 30, ct);
            Assert.True(started.ExitCode == 0, started.StdErr);
            containerId = started.StdOut.Trim();
            Assert.Equal(64, containerId.Length);

            string statePath = Path.Combine(root, "manager-desired-state.json");
            var firstState = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            var registry = new WorkloadRegistry();
            var failRemoval = new PodmanCommandRunner((args, token) =>
                args[0] is "kill" or "rm" ? Task.FromResult(new PodmanCommandResult(125, "", "injected containment failure")) : real.RunAsync(args, 30, token));
            await using (var first = new EgressTopologyManager(failRemoval, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")), new WorkloadPostStartVerifier(failRemoval, firstState), registry, root, desiredStateStore: firstState))
                await first.RebuildRegistryFromLiveContainersAsync(registry, ct);
            Assert.True(firstState.IsWorkloadQuarantined(containerId));
            Assert.False(registry.TryGetByIp(ip, out _));
            Assert.Equal(0, (await real.RunAsync(["container", "exists", containerId], 10, ct)).ExitCode);

            var reopenedState = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            Assert.True(reopenedState.IsWorkloadQuarantined(containerId));
            var restartedRegistry = new WorkloadRegistry();
            var calls = new List<string>();
            var traced = new PodmanCommandRunner(async (args, token) =>
            {
                PodmanCommandResult result = await real.RunAsync(args, args[0] == "rm" ? 5 : 30, token);
                calls.Add(string.Join(' ', args) + $" => {result.ExitCode}: {result.StdErr}");
                return result;
            });
            await using (var restarted = new EgressTopologyManager(traced, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")), new WorkloadPostStartVerifier(traced, reopenedState), restartedRegistry, root, desiredStateStore: reopenedState))
                await restarted.RebuildRegistryFromLiveContainersAsync(restartedRegistry, ct);
            Assert.False(restartedRegistry.TryGetByIp(ip, out _));
            Assert.True((await real.RunAsync(["container", "exists", containerId], 10, ct)).ExitCode == 1, string.Join("\n", calls));
        }
        finally
        {
            if (containerId is not null) await real.RunAsync(["rm", "-f", containerId], 15, CancellationToken.None);
            if (networkCreated) await real.RunAsync(["network", "rm", network], 15, CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }
}
