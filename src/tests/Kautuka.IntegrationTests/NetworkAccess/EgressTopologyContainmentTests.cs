using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class EgressTopologyContainmentTests
{
    [Fact]
    public async Task ProductionDnsBoundaryAllowsOnlyTheCoreDnsGateway()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-dns-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args.SequenceEqual(["network", "inspect", EgressTopologyManager.OutsideNetworkName]))
                return Task.FromResult(new PodmanCommandResult(0, "[{\"subnets\":[{\"gateway\":\"192.168.20.1\"}]}]", ""));
            if (args.SequenceEqual(["pod", "exists", EgressTopologyManager.EgressPodName]))
                return Task.FromResult(new PodmanCommandResult(1, "", "not found"));
            throw new InvalidOperationException("Stop after rendering the production configuration.");
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, root);
        try
        {
            MethodInfo method = typeof(EgressTopologyManager).GetMethod("EnsureEgressPodAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await (Task)method.Invoke(manager, [false, null, false, null, TestContext.Current.CancellationToken])!);
            string boundary = await File.ReadAllTextAsync(Path.Combine(root, "nac", "boundary", "boundary.nft"), TestContext.Current.CancellationToken);
            string corefile = await File.ReadAllTextAsync(Path.Combine(root, "nac", "dns", "Corefile"), TestContext.Current.CancellationToken);
            Assert.Contains(corefile.Split('\n'), line => string.Equals(line.Trim(), "forward . 10.248.0.1", StringComparison.Ordinal));
            Assert.Contains("ip daddr 10.248.0.1 meta l4proto { tcp, udp } th dport 53 counter accept comment \"dns_upstream\"", boundary, StringComparison.Ordinal);
            Assert.DoesNotContain("192.168.20.1", boundary, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[{\"State\":\"running\"}]")]
    [InlineData("[{\"Id\":\"\",\"State\":\"running\"}]")]
    [InlineData("[{\"Id\":\"truncated\",\"State\":\"running\"}]")]
    public async Task IncompletePodmanSnapshotDoesNotDeregisterRunningWorkload(string snapshot)
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-incomplete-ps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry("10.247.1.41", "alice", "agent", "running-container", "agent-sandbox", null,
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            var podman = new PodmanCommandRunner((args, _) => Task.FromResult(new PodmanCommandResult(
                0, args.SequenceEqual(["ps", "-a", "--no-trunc", "--format", "json"]) ? snapshot : string.Empty, string.Empty, ProcessTerminationReason.Completed)));
            await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
                new WorkloadPostStartVerifier(podman), registry, root);
            MethodInfo method = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool completed = await (Task<bool>)method.Invoke(manager, [registry, TestContext.Current.CancellationToken])!;
            Assert.False(completed);
            Assert.NotNull(registry.FindByIp("10.247.1.41"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ValidEmptyPodmanSnapshotDeregistersTerminatedWorkload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-empty-ps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry("10.247.1.41", "alice", "agent", "running-container", "agent-sandbox", null,
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            var podman = new PodmanCommandRunner((args, _) => Task.FromResult(new PodmanCommandResult(
                0, args.SequenceEqual(["ps", "-a", "--no-trunc", "--format", "json"]) ? "[]" : string.Empty, string.Empty, ProcessTerminationReason.Completed)));
            await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
                new WorkloadPostStartVerifier(podman), registry, root);
            MethodInfo method = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool completed = await (Task<bool>)method.Invoke(manager, [registry, TestContext.Current.CancellationToken])!;
            Assert.True(completed);
            Assert.Null(registry.FindByIp("10.247.1.41"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StoppedPackageProxySignalsTheWorkspaceSupervisorOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-package-proxy-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string containerId = new('a', 64);
            var registry = new WorkloadRegistry();
            var store = new WorkspaceDesiredStateStore();
            var podman = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(
                0, $$"""[{"Id":"{{containerId}}","State":"exited"}]""", "")));
            await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
                new WorkloadPostStartVerifier(podman), registry, root, desiredStateStore: store);
            manager.RegisterVerifiedPackageProxyContainer(containerId);
            manager.SetPackageProxyReady(true);
            MethodInfo reconcile = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.True(await (Task<bool>)reconcile.Invoke(manager, [registry, TestContext.Current.CancellationToken])!);
            Assert.False(manager.IsPackageProxyReady);
            Assert.True(store.Updates.TryRead(out WorkspaceReconcileTriggerKind trigger));
            Assert.Equal(WorkspaceReconcileTriggerKind.RecoveryRetry, trigger);

            Assert.True(await (Task<bool>)reconcile.Invoke(manager, [registry, TestContext.Current.CancellationToken])!);
            Assert.False(store.Updates.TryRead(out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));

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
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
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
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
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
        string envoyDirectory = Path.Combine(root, "nac", "envoy");
        string dnsDirectory = Path.Combine(root, "nac", "dns");
        Directory.CreateDirectory(boundaryDirectory);
        Directory.CreateDirectory(envoyDirectory);
        Directory.CreateDirectory(dnsDirectory);
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig("/run/kautuka-admin/admin.sock", isWindowsMtls: false);
        string corefile = BoundaryRulesetRenderer.RenderCorefile(EgressTopologyManager.EgressPodIp);
        await File.WriteAllTextAsync(
            Path.Combine(boundaryDirectory, "boundary.nft"),
            rendered + "# drift",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(envoyDirectory, "envoy.yaml"), envoy, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dnsDirectory, "Corefile"), corefile, TestContext.Current.CancellationToken);
        SetPrivateField(manager, "isStartupGateOpen", true);
        SetPrivateField(manager, "expectedBoundaryNftSourceHash", EgressTopologyManager.ComputeReconcileConfigurationHash(rendered, envoy, corefile));
    }

    private static void SetPrivateField(EgressTopologyManager manager, string name, object value)
    {
        FieldInfo field = typeof(EgressTopologyManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected private field '{name}'.");
        field.SetValue(manager, value);
    }
}
