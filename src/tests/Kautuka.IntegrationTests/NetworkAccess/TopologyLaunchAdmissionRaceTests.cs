using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccessController;
using ModelContextProtocol.Client;
using System.Net;
using System.Net.Sockets;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class TopologyLaunchAdmissionRaceTests
{
    [Fact]
    public async Task ProbeCanceledDuringMcpInitialize_HoldsLeaseUntilEphemeralCleanupFinishes()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The MCP subprocess lease regression uses a Linux Python process.");
        }

        string root = Path.Combine(Path.GetTempPath(), $"nac-stalled-mcp-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string server = "import socket,sys,time\ns=socket.create_connection(('127.0.0.1'," + port + "))\ns.sendall(b'started\\n')\ns.close()\n" +
            "for line in sys.stdin:\n time.sleep(60)\n";
        using var admission = new TestAdmissionState();
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
        var registry = new WorkloadRegistry();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.ContainerDeregistered += (_, _) =>
        {
            cleanupStarted.TrySetResult();
            BlockUntilReleased(releaseCleanup.Task);
        };
        var runner = new PodmanCommandRunner((args, _) =>
        {
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
                return Task.FromResult(new PodmanCommandResult(0, "sha256:" + new string('a', 64), string.Empty, ProcessTerminationReason.Completed));
            if (args[0] == "inspect" && args.Contains("--format", StringComparer.Ordinal))
                return Task.FromResult(new PodmanCommandResult(0, "true", string.Empty, ProcessTerminationReason.Completed));
            if (args[0] == "inspect")
            {
                RegistryEntry entry = Assert.Single(registry.GetAll());
                const string id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                string json = "[{\"Id\":\"" + id + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"State\":{\"Pid\":4242},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                return Task.FromResult(new PodmanCommandResult(0, json, string.Empty, ProcessTerminationReason.Completed));
            }
            if (args[0] == "unshare")
            {
                string output = args.Contains("cat", StringComparer.Ordinal) ? "1" : BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", Assert.Single(registry.GetAll()).Ip, StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
            }
            return Task.FromResult(new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit));
        });
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "stalled-mcp-cleanup", "localhost/test:latest", string.Empty, [], true, false,
            null, null, [], 30, 600);
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner),
            workloadLaunchGate: new WorkloadLaunchGate(admission, policyStore),
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage, string? ImageDigest)> probe =
                service.ProbeConnectorAsync(connector, 15, cancellation.Token, "user-a");
            using TcpClient serverStarted = await listener.AcceptTcpClientAsync(cancellation.Token);
            await cancellation.CancelAsync();

            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, admission.ActiveLeaseCount);

            releaseCleanup.TrySetResult();
            var result = await probe.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(result.Ok);
            Assert.Equal(0, admission.ActiveLeaseCount);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await cancellation.CancelAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeTransportConstructionFailureRemovesProvenNeverStartedIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-unstarted-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            using var admission = new TestAdmissionState();
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No Podman call is expected."));
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "transport-constructor-failure", "localhost/test:latest", string.Empty, [], true, false,
                null, null, [],
                30, 600);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
                workloadRegistry: registry,
                workloadLaunchGate: new WorkloadLaunchGate(admission, policyStore),
                stdioCommand: null!);

            var result = await service.ProbeConnectorAsync(
                connector,
                10,
                TestContext.Current.CancellationToken,
                "user-a");

            Assert.False(result.Ok);
            Assert.NotNull(result.FailureMessage);
            Assert.Empty(registry.GetAll());
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
    public async Task ReopeningAdmissionUsesFreshGenerationWhilePriorCancellationCallbackIsStalled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-admission-generation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var callbackStarted = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        IWorkloadLaunchLease? firstLease = null;
        IWorkloadLaunchLease? secondLease = null;
        try
        {
            var registry = new WorkloadRegistry();
            var runner = new PodmanCommandRunner((_, _) => Task.FromResult(
                new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed)));
            await using var manager = new EgressTopologyManager(
                runner,
                new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
                new WorkloadPostStartVerifier(runner, new WorkspaceDesiredStateStore()),
                registry,
                root);

            SetStartupGateOpen(manager, true);
            firstLease = await manager.AcquireLaunchLeaseAsync(TestContext.Current.CancellationToken);
            using CancellationTokenRegistration stalledCallback = firstLease.CancellationToken.Register(() =>
            {
                callbackStarted.Set();
                releaseCallback.Wait();
            });

            manager.CloseLaunchAdmission();
            Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

            SetStartupGateOpen(manager, true);
            secondLease = await manager.AcquireLaunchLeaseAsync(TestContext.Current.CancellationToken);
            Assert.True(firstLease.CancellationToken.IsCancellationRequested);
            Assert.False(secondLease.CancellationToken.IsCancellationRequested);

            releaseCallback.Set();
            await stalledCallback.DisposeAsync();
            await firstLease.DisposeAsync();
            firstLease = null;

            manager.CloseLaunchAdmission();
            await WaitUntilCancelledAsync(secondLease.CancellationToken, TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseCallback.Set();
            if (firstLease is not null)
            {
                await firstLease.DisposeAsync();
            }

            if (secondLease is not null)
            {
                await secondLease.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectorReconcile_BuilderRunIsCanceledWhenAdmissionCloses()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-builder-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string bundlePath = Path.Combine(root, "connector.mcpb");
        await using (FileStream bundleStream = new(bundlePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var archive = new ZipArchive(bundleStream, ZipArchiveMode.Create);
            archive.CreateEntry("manifest.json");
            archive.CreateEntry("server.py");
        }

        using var admission = new TestAdmissionState();
        var packageRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packageRunCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[]? packageRunArguments = null;
        var podmanCalls = new List<string>();
        var runner = new PodmanCommandRunner(async (args, cancellationToken) =>
        {
            podmanCalls.Add(string.Join(' ', args));
            if (args[0] == "inspect")
            {
                return new PodmanCommandResult(0, "[{\"Id\":\"existing-container\",\"HostConfig\":{},\"Config\":{},\"State\":{\"Status\":\"running\"}}]", string.Empty, ProcessTerminationReason.Completed);
            }

            if (args.Count >= 2 && args[0] == "image" && args[1] == "exists")
            {
                return new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit);
            }

            if (args[0] == "run")
            {
                packageRunArguments = [.. args];
                packageRunStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    packageRunCanceled.TrySetResult();
                    throw;
                }
            }

            return new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed);
        });
        var desired = new WorkspaceDesiredStateStore();
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var workspaceContext = new ContainerContext("agent", "agent-a", "user-a", workspace, "/workspace", "base", 30, "512m", 1, [], new Dictionary<string, string>());
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "builder-race", "localhost/test:latest", "python server.py", [], true, false,
            bundlePath, "python", [new KeyValuePair<string, string>("PIP_INDEX_URL", "http://packages.invalid/simple")], 30, 600);
        _ = desired.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = "localhost/test:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspaceContext], [connector], []);
        var allowlist = new RuntimeImageAllowlist(1, [new RuntimeImageEntry("test", "localhost/test:latest", null)]);
        var registry = new WorkloadRegistry();
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired,
            runner,
            new McpbImageMaterializer(
                runner,
                NullLogger<McpbImageMaterializer>.Instance,
                allowlist,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "builder-ip.json")),
                workloadRegistry: registry),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            workloadLaunchGate: new WorkloadLaunchGate(admission, policyStore));
        try
        {
            Task<IReadOnlyList<string>> reconcile = service.ReconcileDesiredConnectorsAsync([connector], TestContext.Current.CancellationToken);
            Task runOrReconcile = await Task.WhenAny(packageRunStarted.Task, reconcile)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (runOrReconcile == reconcile)
            {
                IReadOnlyList<string> notes = await reconcile.ConfigureAwait(false);
                Assert.Fail("Reconciliation completed without reaching the networked package-builder run. Notes: " + string.Join("; ", notes) + " Calls: " + string.Join(" | ", podmanCalls));
            }
            Assert.Contains("--network", packageRunArguments ?? []);
            Assert.Contains("kautuka-workload", string.Join(' ', packageRunArguments ?? []), StringComparison.Ordinal);

            admission.CloseLaunchAdmission();
            await packageRunCanceled.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reconcile.ConfigureAwait(false));
        }
        finally
        {
            admission.CloseLaunchAdmission();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileBoundaryDrift_CancelsAdmissionAndRescansLateLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-admission-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var releaseLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationCallbackStarted = new ManualResetEventSlim();
        using var releaseCancellationCallback = new ManualResetEventSlim();
        CancellationToken launchCancellationToken = CancellationToken.None;
        Task? launchTask = null;
        Task? reconcileTask = null;
        try
        {
            var registry = new WorkloadRegistry();
            bool workloadRunning = false;
            var calls = new List<string>();
            var podman = new PodmanCommandRunner((args, _) =>
            {
                calls.Add(string.Join(" ", args));
                if (args.SequenceEqual(["ps", "-a", "--filter", $"network={EgressTopologyManager.WorkloadNetworkName}", "--format", "json"]))
                {
                    string output = workloadRunning
                        ? "[{\"Id\":\"in-flight-cid\",\"State\":\"running\",\"Names\":[\"in-flight-workload\"]}]"
                        : "[]";
                    return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["stop", "-t", "2", "in-flight-cid"]))
                {
                    workloadRunning = false;
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var allocator = new WorkloadAddressAllocator(Path.Combine(root, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, root);
            var gate = new WorkloadLaunchGate(
                manager,
                new NetworkAccessPolicyStore(Path.Combine(root, "policy.json")));
            await PrepareSourceDriftAsync(manager, root);

            var admissionPassed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            launchTask = Task.Run(async () =>
            {
                // This models a launch path after its admission check but before Podman returns
                // the new container ID and Manager records the registry entry.
                await using IWorkloadLaunchLease launchLease = await gate
                    .AcquireLaunchLeaseAsync("user-a", TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
                launchCancellationToken = launchLease.CancellationToken;
                using CancellationTokenRegistration stalledCallback = launchCancellationToken.Register(() =>
                {
                    cancellationCallbackStarted.Set();
                    releaseCancellationCallback.Wait();
                });
                using CancellationTokenRegistration throwingCallback = launchCancellationToken.Register(
                    static () => throw new InvalidOperationException("synthetic cancellation callback failure"));
                admissionPassed.TrySetResult();
                await releaseLaunch.Task.ConfigureAwait(false);
                workloadRunning = true;
                registry.Register(new RegistryEntry(
                    "10.247.1.44", "user-a", "agent-a", "in-flight-cid", "agent-sandbox", null,
                    "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            }, TestContext.Current.CancellationToken);
            await admissionPassed.Task.WaitAsync(TestContext.Current.CancellationToken);

            reconcileTask = manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            // Incident closure schedules admission cancellation and scans without waiting for MCP
            // startup. The active lease keeps the scan incomplete until a later rescan.
            for (int attempt = 0; attempt < 100 && manager.IsStartupGateOpen; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            Assert.False(manager.IsStartupGateOpen);
            await reconcileTask.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(manager.TopologyContainmentCompleteForTests);
            Assert.True(cancellationCallbackStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.True(launchCancellationToken.IsCancellationRequested);
            releaseCancellationCallback.Set();

            releaseLaunch.TrySetResult();
            await launchTask.WaitAsync(TestContext.Current.CancellationToken);
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.Contains("stop -t 2 in-flight-cid", calls);
            Assert.False(workloadRunning);
            Assert.True(manager.TopologyContainmentCompleteForTests);
        }
        finally
        {
            releaseCancellationCallback.Set();
            releaseLaunch.TrySetResult();
            if (launchTask is not null)
            {
                await launchTask.ConfigureAwait(false);
            }

            if (reconcileTask is not null)
            {
                try
                {
                    await reconcileTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the test assertion while allowing the simulated launch to drain.
                }
            }

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
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        typeof(EgressTopologyManager)
            .GetField("launchAdmissionClosed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, false);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryNftSourceHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeReconcileConfigurationHash(rendered, envoy, corefile));
    }

    private static void SetStartupGateOpen(EgressTopologyManager manager, bool value)
    {
        typeof(EgressTopologyManager)
            .GetProperty("IsStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .SetValue(manager, value);
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken, CancellationToken testCancellationToken)
    {
        for (int attempt = 0; attempt < 100 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), testCancellationToken);
        }

        Assert.True(cancellationToken.IsCancellationRequested);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1849:Call async methods when in an async method",
        Justification = "WorkloadRegistry notifications are synchronous; the test blocks this notification to observe the caller's lease during cleanup.")]
    private static void BlockUntilReleased(Task release) => release.GetAwaiter().GetResult();

    private sealed class TestAdmissionState : IWorkloadStartupState, IWorkloadLaunchAdmission, IDisposable
    {
        private readonly object sync = new();
        private CancellationTokenSource admissionCancellation = new();
        private bool isOpen = true;
        private int activeLeaseCount;

        public int ActiveLeaseCount => Volatile.Read(ref activeLeaseCount);

        public bool IsStartupGateOpen
        {
            get
            {
                lock (sync)
                {
                    return isOpen;
                }
            }
        }

        public ValueTask<IWorkloadLaunchLease> AcquireLaunchLeaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!isOpen)
                {
                    throw new InvalidOperationException("Admission closed.");
                }

                Interlocked.Increment(ref activeLeaseCount);
                return ValueTask.FromResult<IWorkloadLaunchLease>(new TestLease(() => Interlocked.Decrement(ref activeLeaseCount), admissionCancellation.Token));
            }
        }

        public void CloseLaunchAdmission()
        {
            CancellationTokenSource cancel;
            lock (sync)
            {
                isOpen = false;
                cancel = admissionCancellation;
            }

            _ = CancelAsync(cancel);
        }

        private static async Task CancelAsync(CancellationTokenSource cancellation)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        public void Dispose() => admissionCancellation.Dispose();

        private sealed class TestLease(Action release, CancellationToken cancellationToken) : IWorkloadLaunchLease
        {
            private Action? release = release;

            public CancellationToken CancellationToken { get; } = cancellationToken;

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref release, null)?.Invoke();
                return ValueTask.CompletedTask;
            }
        }
    }
}
