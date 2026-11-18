using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_StartupDoubleFailureTests
{
    [Fact]
    public async Task StartupAbortsWhenRestoredWorkloadHasNoImmutableIdentity()
    {
        const string reusableName = "reusable-restored-name";
        const string ip = "10.247.1.51";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = reusableName, State = "running" } }), ""));
            if (args[0] == "inspect")
            {
                var inspected = new Dictionary<string, object?>
                {
                    ["Id"] = reusableName,
                    ["Config"] = new Dictionary<string, object?> { ["Labels"] = new Dictionary<string, string> { ["io.kautuka.user"] = "u1", ["io.kautuka.agent"] = "a1", ["io.kautuka.exec-context"] = "agent-sandbox", ["io.kautuka.kind"] = "agent-sandbox", ["io.kautuka.egress"] = "normal" } },
                    ["NetworkSettings"] = new Dictionary<string, object?> { ["Networks"] = new Dictionary<string, object?> { ["kautuka-workload"] = new Dictionary<string, string> { ["IPAddress"] = ip } } }
                };
                return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { inspected }), ""));
            }
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken));
            Assert.False(manager.IsStartupGateOpen);
            Assert.False(registry.TryGetByIp(ip, out _));
            var incident = typeof(EgressTopologyManager).GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(incident);
            Assert.False((bool)incident.GetValue(manager)!);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAbortsAndKeepsGateClosedWhenQuarantineAndRemovalFail(bool throwOnRemove)
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.50";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        string blocker = Path.Combine(temp, "blocked"); await File.WriteAllTextAsync(blocker, "file", TestContext.Current.CancellationToken);
        var desired = new WorkspaceDesiredStateStore(Path.Combine(blocker, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running" } }), ""));
            if (args[0] == "inspect")
            {
                var inspected = new Dictionary<string, object?>
                {
                    ["Id"] = cid,
                    ["Config"] = new Dictionary<string, object?> { ["Labels"] = new Dictionary<string, string> { ["io.kautuka.user"] = "u1", ["io.kautuka.agent"] = "a1", ["io.kautuka.exec-context"] = "agent-sandbox", ["io.kautuka.kind"] = "agent-sandbox", ["io.kautuka.egress"] = "normal" } },
                    ["State"] = new Dictionary<string, int> { ["Pid"] = 4242 },
                    ["NetworkSettings"] = new Dictionary<string, object?> { ["Networks"] = new Dictionary<string, object?> { ["kautuka-workload"] = new Dictionary<string, string> { ["IPAddress"] = ip } } }
                };
                return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { inspected }), ""));
            }
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string badRules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal).Replace("10.247.0.2", "10.247.0.99", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, badRules, ""));
            }
            if (args[0] == "rm")
            {
                if (throwOnRemove) throw new IOException("injected rm exception");
                return Task.FromResult(new PodmanCommandResult(125, "", "injected rm failure"));
            }
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            Exception? ex = null;
            try { await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken); }
            catch (Exception caught) { ex = caught; }
            Assert.Contains(calls, call => call[0] == "rm");
            Assert.NotNull(ex);
            Assert.Contains("Startup must remain closed", ex.Message, StringComparison.Ordinal);
            Assert.False(manager.IsStartupGateOpen);
            Assert.False(registry.TryGetByIp(ip, out _));
            Assert.Contains(calls, call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal) && call.Contains(cid, StringComparer.Ordinal));
            Assert.False((bool)typeof(EgressTopologyManager).GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
