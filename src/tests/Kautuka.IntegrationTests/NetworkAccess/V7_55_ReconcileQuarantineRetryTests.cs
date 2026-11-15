using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_ReconcileQuarantineRetryTests
{
    private const string Cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Ip = "10.247.1.60";

    [Fact]
    public async Task RepairedQuarantinedWorkloadRetriesRemovalBeforeRulesCheck()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        string statePath = Path.Combine(temp, "manager-desired-state.json");
        var desired = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
        desired.QuarantineWorkload(Cid);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = Cid, State = "running" } }), ""));
            if (args[0] == "rm") throw new IOException("transient rm exception");
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", Cid, "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            await InvokeReconcileAsync(manager, registry);
            Assert.Contains(calls, call => call[0] == "rm" && call.Contains(Cid, StringComparer.Ordinal));
            Assert.DoesNotContain(calls, call => call.Contains("nft", StringComparer.Ordinal));
            Assert.DoesNotContain(calls, call => call[0] == "inspect");
            Assert.False(manager.IsStartupGateOpen);
            Assert.True(registry.TryGetByIp(Ip, out RegistryEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal("deny-all", entry.Egress);
            Assert.True(desired.IsWorkloadQuarantined(Cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task RulesDriftThrownRemovalKeepsRegistryDenyAllAndClosesGate()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = Cid, State = "running" } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = Cid, State = new { Pid = 4242 } } }), ""));
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string wrong = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", Ip, StringComparison.Ordinal).Replace("10.247.0.2", "10.247.0.99", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, wrong, ""));
            }
            if (args[0] == "rm") throw new IOException("transient rm exception");
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(Ip, "u1", "a1", Cid, "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            await InvokeReconcileAsync(manager, registry);
            Assert.Contains(calls, call => call[0] == "rm" && call.Contains(Cid, StringComparer.Ordinal));
            Assert.False(manager.IsStartupGateOpen);
            Assert.True(registry.TryGetByIp(Ip, out RegistryEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal("deny-all", entry.Egress);
            Assert.True(desired.IsWorkloadQuarantined(Cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task InvokeReconcileAsync(EgressTopologyManager manager, IWorkloadRegistry registry)
    {
        var method = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(manager, new object[] { registry, TestContext.Current.CancellationToken })!;
    }
}
