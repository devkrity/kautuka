using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class EgressTopologyContainmentTests
{
    [Fact]
    public async Task ReconcileBoundaryDrift_StopsRegisteredRunningWorkload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.41", "user-a", "agent-a", "workload-cid", "agent-sandbox", null,
                "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

            bool workloadRunning = true;
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                if (args.SequenceEqual(["ps", "-a", "--filter", $"network={EgressTopologyManager.WorkloadNetworkName}", "--format", "json"]))
                {
                    string state = workloadRunning ? "running" : "exited";
                    return Task.FromResult(new PodmanCommandResult(
                        0,
                        "[{\"Id\":\"workload-cid\",\"State\":\"" + state + "\"}]",
                        string.Empty,
                        ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["stop", "-t", "2", "workload-cid"]))
                {
                    workloadRunning = false;
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var allocator = new WorkloadAddressAllocator(Path.Combine(root, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, root);

            // Model the manager after a successful startup canary. The persisted boundary source
            // has since drifted, which causes ReconcileOnceAsync to close its launch gate.
            await PrepareSourceDriftAsync(manager, root);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(manager.IsStartupGateOpen);
            Assert.Contains("stop -t 2 workload-cid", calls);
            Assert.False(workloadRunning);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileBoundaryDrift_StopsUnregisteredNetworkWorkloadsAndSkipsExactInfraIds()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-containment-unregistered-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            bool workloadRunning = true;
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                if (args.SequenceEqual(["ps", "-a", "--filter", $"network={EgressTopologyManager.WorkloadNetworkName}", "--format", "json"]))
                {
                    // `dead` is treated as unknown and contained conservatively.
                    string workloadState = workloadRunning ? "dead" : "exited";
                    return Task.FromResult(new PodmanCommandResult(
                        0,
                        "[{\"Id\":\"validated-infra-cid\",\"State\":\"running\"},{\"Id\":\"unregistered-cid\",\"State\":\"" + workloadState + "\"}]",
                        string.Empty,
                        ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["stop", "-t", "2", "unregistered-cid"]))
                {
                    workloadRunning = false;
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var allocator = new WorkloadAddressAllocator(Path.Combine(root, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, root);
            HashSet<string> infraIds = (HashSet<string>)typeof(EgressTopologyManager)
                .GetField("knownInfraContainerIds", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager)!;
            infraIds.Add("validated-infra-cid");
            await PrepareSourceDriftAsync(manager, root);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(manager.IsStartupGateOpen);
            Assert.Contains("stop -t 2 unregistered-cid", calls);
            Assert.DoesNotContain(calls, call => call.Contains("validated-infra-cid", StringComparison.Ordinal));
            Assert.False(workloadRunning);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileBoundaryDrift_ValidEmptyNetworkListingStillStopsRegisteredWorkload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-containment-empty-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.43", "user-a", "agent-a", "registered-cid", "agent-sandbox", null,
                "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            bool workloadRunning = true;
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                if (args.SequenceEqual(["ps", "-a", "--filter", $"network={EgressTopologyManager.WorkloadNetworkName}", "--format", "json"]))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["stop", "-t", "2", "registered-cid"]))
                {
                    workloadRunning = false;
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var allocator = new WorkloadAddressAllocator(Path.Combine(root, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, root);
            await PrepareSourceDriftAsync(manager, root);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(manager.IsStartupGateOpen);
            Assert.Contains("stop -t 2 registered-cid", calls);
            Assert.False(workloadRunning);
            Assert.True(manager.TopologyContainmentCompleteForTests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileBoundaryDrift_RetriesWhenStopAndKillFail()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-containment-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.42", "user-a", "agent-a", "surviving-cid", "agent-sandbox", null,
                "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            int containmentAttempts = 0;
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                if (args.SequenceEqual(["ps", "-a", "--filter", $"network={EgressTopologyManager.WorkloadNetworkName}", "--format", "json"]))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["stop", "-t", "2", "surviving-cid"]))
                {
                    containmentAttempts++;
                }

                return Task.FromResult(new PodmanCommandResult(125, string.Empty, "injected failure", ProcessTerminationReason.NonZeroExit));
            });
            var allocator = new WorkloadAddressAllocator(Path.Combine(root, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, root);
            await PrepareSourceDriftAsync(manager, root);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(manager.IsStartupGateOpen);
            Assert.Equal(2, containmentAttempts);
            Assert.True(registry.TryGetByIp("10.247.1.42", out RegistryEntry? retained));
            Assert.Equal("surviving-cid", retained?.ContainerId);
            Assert.Equal(2, calls.Count(call => call == "kill surviving-cid"));
            Assert.Equal(2, calls.Count(call => call == "inspect surviving-cid --format json"));
            Assert.False(manager.TopologyContainmentCompleteForTests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task PrepareSourceDriftAsync(EgressTopologyManager manager, string root)
    {
        string rendered = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true);
        string boundaryDirectory = Path.Combine(root, "nac", "boundary");
        Directory.CreateDirectory(boundaryDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(boundaryDirectory, "boundary.nft"),
            rendered + "# drift",
            TestContext.Current.CancellationToken);
        SetPrivateField(manager, "isStartupGateOpen", true);
        SetPrivateField(manager, "expectedBoundaryNftSourceHash", EgressTopologyManager.ComputeBoundaryNftSourceHash(rendered));
    }

    private static void SetPrivateField(EgressTopologyManager manager, string name, object value)
    {
        FieldInfo field = typeof(EgressTopologyManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected private field '{name}'.");
        field.SetValue(manager, value);
    }
}
