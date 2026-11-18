using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_ReconcileRulesDriftTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransientVerificationFailureDeniesThenRecoversWithoutDeletingWorkload(bool failInspection)
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.23";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        bool failAttempt = true;
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running", Names = new[] { "agent" } } }), ""));
            if (args[0] == "inspect") return Task.FromResult(failAttempt && failInspection
                ? new PodmanCommandResult(124, "", "timeout", ProcessTerminationReason.TimedOut)
                : new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = new { Pid = 4242 } } }), ""));
            if (args.Contains("nft", StringComparer.Ordinal)) return Task.FromResult(failAttempt && !failInspection
                ? new PodmanCommandResult(124, "", "timeout", ProcessTerminationReason.TimedOut)
                : new PodmanCommandResult(0, BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal), ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var registry = new WorkloadRegistry();
        var restricted = new RegistryEntry(ip, "u1", "a1", cid, "agent-sandbox", null, "agent-sandbox", "deny-all", "none", null, DateTimeOffset.UtcNow);
        Assert.True(registry.TryRegisterQuarantined(restricted));
        Assert.True(registry.TryPromoteVerified(restricted, restricted with { Egress = "normal", Attribution = "user-agent" }));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(calls, call => call[0] == "rm");
            Assert.False(desired.IsWorkloadQuarantined(cid));
            Assert.Equal("deny-all", registry.FindByIp(ip)?.Egress);
            failAttempt = false;
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.Equal("normal", registry.FindByIp(ip)?.Egress);
            Assert.True(registry.IsVerified(ip, cid));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task ReconcileRemovesAndQuarantinesWorkloadWhenRulesHashDrifts()
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.21";
        const string containerName = "reusable-workload-name";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running", Names = new[] { containerName } } }), ""));
            if (args[0] == "inspect") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = new { Pid = 4242 } } }), ""));
            if (args.Any(arg => arg.Contains("disable_ipv6", StringComparison.Ordinal))) return Task.FromResult(new PodmanCommandResult(0, "1", ""));
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal).Replace("10.247.0.2", "10.247.0.99", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, nft, ""));
            }
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var desired = new WorkspaceDesiredStateStore(Path.Combine(temp, "manager-desired-state.json"), NullLogger<WorkspaceDesiredStateStore>.Instance);
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(ip, "u1", "a1", containerName, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired);
        try
        {
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.Contains(calls, call => call.Contains("nft", StringComparer.Ordinal));
            Assert.False(registry.TryGetByIp(ip, out _));
            Assert.Contains(calls, call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal) && call.Contains(cid, StringComparer.Ordinal));
            Assert.True(desired.IsWorkloadQuarantined(cid));
            Assert.DoesNotContain(calls, call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal) && call.Contains(containerName, StringComparer.Ordinal));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }



    [Theory]
    [InlineData("exited")]
    [InlineData("created")]
    public async Task InactiveNamedWorkloadIsNotTreatedAsRulesDrift(string state)
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string name = "prestart-workload";
        const string ip = "10.247.1.22";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = state, Names = new[] { name } } }), ""));
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(ip, "u1", "a1", name, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.True(registry.TryGetByIp(ip, out RegistryEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal(cid, entry.ContainerId);
            Assert.DoesNotContain(calls, call => call.Contains("nft", StringComparer.Ordinal) || call[0] == "rm");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}

