using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_DoubleFailureRetryTests
{
    [Fact]
    public async Task PersistenceAndRemovalDoubleFailureClosesGateThenRetriesSuccessfully()
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.40";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        string blocker = Path.Combine(temp, "blocked"); await File.WriteAllTextAsync(blocker, "block", TestContext.Current.CancellationToken);
        string statePath = Path.Combine(blocker, "manager-desired-state.json");
        var desired = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
        int removals = 0;
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running" } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = new { Pid = 4242 } } }), ""));
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string badRules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal).Replace("10.247.0.2", "10.247.0.99", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, badRules, ""));
            }
            if (args[0] == "rm") return Task.FromResult(++removals == 1 ? new PodmanCommandResult(1, "", "first remove failed") : new PodmanCommandResult(0, "", ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(ip, "u1", "a1", cid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(manager, true);
            await ReconcileRegistryAsync(manager, registry);
            Assert.False(manager.IsStartupGateOpen);
            var incident = typeof(EgressTopologyManager).GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(incident);
            Assert.False((bool)incident.GetValue(manager)!);
            Assert.True(desired.IsWorkloadQuarantined(cid));
            Assert.True(registry.FindByIp(ip)?.Egress == "deny-all");
            Assert.Equal(1, removals);

            File.Delete(blocker);
            await ReconcileRegistryAsync(manager, registry);
            Assert.Equal(2, removals);
            Assert.False(registry.TryGetByIp(ip, out _));
            var reopened = new WorkspaceDesiredStateStore(statePath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            Assert.True(reopened.IsWorkloadQuarantined(cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task ReconcileRegistryAsync(EgressTopologyManager manager, IWorkloadRegistry registry)
    {
        var method = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(manager, new object[] { registry, TestContext.Current.CancellationToken })!;
    }
}



