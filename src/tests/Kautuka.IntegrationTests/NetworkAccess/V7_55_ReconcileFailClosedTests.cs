using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_ReconcileFailClosedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPodmanListingClosesGateWithoutLatching(bool throws)
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", Cid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        var podman = new PodmanCommandRunner((_, _) => throws
            ? throw new IOException("Podman unavailable")
            : Task.FromResult(new PodmanCommandResult(124, "", "timeout")));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            var reconcile = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            bool completed = await (Task<bool>)reconcile.Invoke(manager, [registry, TestContext.Current.CancellationToken])!;
            Assert.False(completed);
            Assert.False(manager.IsStartupGateOpen);
            var recovery = typeof(EgressTopologyManager).GetField("transientRecoveryPending", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True((bool)recovery.GetValue(manager)!);
            Assert.NotNull(registry.FindByIp(Ip));
            var incident = typeof(EgressTopologyManager).GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.False((bool)incident.GetValue(manager)!);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
    private const string Cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Ip = "10.247.1.31";

    [Fact]
    public async Task RunningInspectFailureDeniesAndRetriesWithoutRemovingWorkload()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = Cid, State = "running" } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(1, "", "transient inspect failure"));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", Cid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            var reconcile = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)reconcile.Invoke(manager, new object[] { registry, TestContext.Current.CancellationToken })!);
            Assert.DoesNotContain(calls, call => call[0] == "rm");
            Assert.True(registry.TryGetByIp(Ip, out RegistryEntry? denied));
            Assert.NotNull(denied);
            Assert.Equal("deny-all", denied.Egress);
            Assert.Equal("none", denied.Attribution);
            Assert.False(desired.IsWorkloadQuarantined(Cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task InactiveCanonicalWorkloadRemainsRegistered()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = Cid, State = "exited" } }), ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", Cid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.True(registry.TryGetByIp(Ip, out RegistryEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal(Cid, entry.ContainerId);
            Assert.DoesNotContain(calls, call => call[0] == "rm" || call.Contains("nft", StringComparer.Ordinal));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task UnidentifiableRunningWorkloadIsDeniedWithoutTopologyLatch()
    {
        const string name = "unverified-workload-name";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = name, State = "running" } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(1, "", "inspect unavailable"));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", name, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            var reconcile = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await ((Task)reconcile.Invoke(manager, new object[] { registry, TestContext.Current.CancellationToken })!);
            Assert.False(manager.IsStartupGateOpen);
            Assert.True(registry.TryGetByIp(Ip, out RegistryEntry? denied));
            Assert.NotNull(denied);
            Assert.Equal("normal", denied.Egress);
            var incident = typeof(EgressTopologyManager).GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(incident);
            Assert.False((bool)incident.GetValue(manager)!);
            Assert.DoesNotContain(calls, call => call[0] is "kill" or "network");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}

