using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_RestartQuarantineCleanupTests
{
    [Fact]
    public async Task ReconcileRetriesPersistedQuarantinedSurvivorWithoutAdoptingIt()
    {
        const string cid = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        desired.QuarantineWorkload(cid);
        int removals = 0;
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running" } }), ""));
            if (args[0] == "rm") return Task.FromResult(++removals == 1 ? new PodmanCommandResult(125, "", "first rm failed") : new PodmanCommandResult(0, "", ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            await InvokeReconcileAsync(manager, registry);
            Assert.False(manager.IsStartupGateOpen);
            Assert.False(registry.TryGetByIp("10.247.1.31", out _));
            await InvokeReconcileAsync(manager, registry);
            Assert.Equal(2, removals);
            Assert.False(registry.TryGetByIp("10.247.1.31", out _));
            Assert.True(desired.IsWorkloadQuarantined(cid));
            Assert.DoesNotContain(calls, call => call[0] == "inspect");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task InvokeReconcileAsync(EgressTopologyManager manager, IWorkloadRegistry registry)
    {
        var method = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(manager, new object[] { registry, TestContext.Current.CancellationToken })!;
    }

    [Fact]
    public async Task RestartRetriesRemovalOfPreviouslyQuarantinedLiveWorkload()
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.30";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string statePath = Path.Combine(temp, "manager-desired-state.json");
        var desired = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
        desired.QuarantineWorkload(cid);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running" } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, Config = new { Labels = new Dictionary<string, string> { ["io.kautuka.user"] = "u1", ["io.kautuka.agent"] = "a1", ["io.kautuka.exec-context"] = "agent-sandbox", ["io.kautuka.kind"] = "agent-sandbox", ["io.kautuka.egress"] = "normal" } }, NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = ip } } } } }), ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);
            Assert.Contains(calls, call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal) && call.Contains(cid, StringComparer.Ordinal));
            Assert.False(registry.TryGetByIp(ip, out _));
            Assert.True(desired.IsWorkloadQuarantined(cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
