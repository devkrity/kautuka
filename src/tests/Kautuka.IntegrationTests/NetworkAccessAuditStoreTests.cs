using System.Text.Json.Nodes;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAccessAuditStoreTests : IDisposable
{
    [Fact]
    public void AuthorizationDenialOnMatchedRouteIsRecordedAsDenied()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        string allocation = Allocate(store);
        string line = Line(allocation, chain: "upstream-allowed");
        int separator = line.IndexOf(' ', StringComparison.Ordinal);
        JsonObject record = JsonNode.Parse(line[(separator + 1)..])!.AsObject();
        record["response_flags"] = "UAEX";
        store.Observe(envoy, [line[..(separator + 1)] + record.ToJsonString()], false);
        NetworkAuditEvent entry = Assert.Single(store.Read(alice, 0, 50, null).Events);
        Assert.Equal("denied", entry.Outcome);
        Assert.False(entry.Connected);
    }

    [Fact]
    public void InternalIngestionDoesNotBecomeExternalArtifactAudit()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        static string HostLine(string method, string authority, int second)
            => $"2026-11-07T12:00:{second:00}.123456789Z " + new JsonObject
            {
                ["phase"] = "host-artifact",
                ["request_method"] = method,
                ["authority"] = authority,
                ["status"] = 403,
            }.ToJsonString();
        store.Observe(envoy, [HostLine("POST", "kautuka-telemetry.internal", 1),
            HostLine("POST", "kautuka-telemetry.internal:80", 2),
            HostLine("CONNECT", "kautuka-telemetry.internal:443", 3)], false);
        NetworkAuditEvent entry = Assert.Single(store.Read(admin, 0, 50, null).Events);
        Assert.Equal("kautuka-telemetry.internal", entry.Destination);
        Assert.Equal(443, entry.Port);
    }

    [Fact]
    public async Task CollectorDetectsShortSameContainerRotationAndDoesNotRewriteIdleHistory()
    {
        string runtimeRoot = Path.Combine(root, "runtime");
        string auditRoot = Path.Combine(runtimeRoot, "egress", "audit");
        var allocations = new NetworkAccessAuditStore(auditRoot);
        string allocation = Allocate(allocations);
        string logs = Line(allocation) + "\n";
        string id = new('a', 64);
        string inspected = JsonSerializer.Serialize(new[] { new { Id = id, Name = "/kautuka-egress-envoy", HostConfig = new { }, State = new { Status = "running", Running = true }, Config = new
        {
            Labels = new Dictionary<string, string> { ["kautuka.egress"] = "true", ["kautuka.managed"] = "manager" },
        } } });
        var runner = new PodmanCommandRunner((args, _) => Task.FromResult(args[0] switch
        {
            "inspect" => new PodmanCommandResult(0, inspected, ""),
            "logs" => new PodmanCommandResult(0, "", logs),
            _ => throw new InvalidOperationException("Audit collection must not bootstrap the runtime."),
        }));
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, runtimeRoot);
        await runtime.CollectNetworkAuditAsync(TestContext.Current.CancellationToken);
        string stateFile = Path.Combine(auditRoot, "state.json");
        DateTime old = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(stateFile, old);
        await runtime.CollectNetworkAuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(old, File.GetLastWriteTimeUtc(stateFile));
        Assert.Empty(runtime.GetNetworkAccessPolicy(alice).Policy.Requests);
        logs = string.Empty; // The same container's native log was rotated/truncated.
        await runtime.CollectNetworkAuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.GetNetworkAudit(alice).CoverageGaps);
        File.SetLastWriteTimeUtc(stateFile, old);
        await runtime.CollectNetworkAuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(old, File.GetLastWriteTimeUtc(stateFile));
        Assert.Equal(1, runtime.GetNetworkAudit(alice).CoverageGaps);
        logs = Line(allocation, 1, second: 3) + "\n";
        await runtime.CollectNetworkAuditAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.GetNetworkAudit(alice).CoverageGaps);
        Assert.Empty(runtime.GetNetworkAccessPolicy(alice).Policy.Requests);
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-audit-tests-" + Guid.NewGuid().ToString("N"));
    private readonly NetworkAccessActor alice = new("alice", false);
    private readonly NetworkAccessActor bob = new("bob", false);
    private readonly NetworkAccessActor admin = new("admin", true);
    private readonly NetworkAccessIdentity identity = new("agent", "alice", "agent-1", "connector", "alice::mail");
    private const string envoy = "test-envoy-container";
    private string AuditRoot => Path.Combine(root, "audit");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    private string Allocate(NetworkAccessAuditStore store, NetworkAccessIdentity? owner = null)
        => store.RecordAllocations([new("10.89.1.10", owner is null ? "original-container" : "replacement-container", owner ?? identity)]);

    private static string Line(string allocation, int connection = 0, string phase = "listener", string chain = "deny", string type = "NotSet", int second = 2)
        => $"2026-11-07T12:00:{second:00}.123456789Z " + new JsonObject
        {
            ["phase"] = phase,
            ["allocation"] = allocation,
            ["publication"] = new string('f', 64),
            ["source"] = "10.89.1.10",
            ["source_address"] = "10.89.1.10:50000",
            ["destination"] = "203.0.113.20:443",
            ["sni"] = "Graph.Microsoft.com.",
            ["filter_chain"] = chain,
            ["connection_id"] = connection,
            ["connection_started"] = $"1794052800.{connection:000000000}",
            ["stream_id"] = null,
            ["event_type"] = type,
            ["timestamp"] = "2026-11-07T12:00:00Z",
            ["bytes_sent"] = 42,
            ["bytes_received"] = 64,
            ["duration_ms"] = 123,
        }.ToJsonString();

    [Fact]
    public void DelayedDenialUsesItsEmittedAllocationAfterAddressReuse()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        string old = Allocate(store);
        string replacement = Allocate(store, identity with { UserId = "bob", ComponentId = "bob::mail" });
        Assert.NotEqual(old, replacement);
        store.Observe(envoy, [Line(replacement, 1), Line(old, 2, second: 3)], false);
        NetworkAuditEvent first = Assert.Single(store.Read(alice, 0, 50, null).Events);
        Assert.Equal("original-container", first.ContainerId);
        NetworkAuditEvent second = Assert.Single(store.Read(bob, 0, 50, null).Events);
        Assert.Equal("replacement-container", second.ContainerId);
    }

    [Fact]
    public void CrashReplayBeforeCursorCommitDoesNotDuplicateConnectionHistory()
    {
        var audit = new NetworkAccessAuditStore(AuditRoot);
        string allocation = Allocate(audit);
        string line = Line(allocation);
        audit.Observe(envoy, [line], false);
        // Simulate restart after history was written, before the cursor was saved.
        var restarted = new NetworkAccessAuditStore(AuditRoot);
        restarted.Observe(envoy, [line], false);
        Assert.Single(restarted.Read(alice, 0, 50, null).Events);
        Assert.Null(restarted.Checkpoint.Cursor);
    }

    [Fact]
    public void ConnectedAndCompletionRecordsCoalesceWhileUpstreamFailureIsNotAnApproval()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        string allocation = Allocate(store);
        store.Observe(envoy, [
            Line(allocation, phase: "proxy", chain: "upstream-allowed", type: "TcpUpstreamConnected"),
            Line(allocation, phase: "proxy", chain: "upstream-allowed", type: "TcpConnectionEnd", second: 3),
            Line(allocation, chain: "upstream-allowed", second: 4),
            Line(allocation, 1, phase: "proxy", chain: "upstream-failed", type: "TcpConnectionEnd", second: 5),
        ], false);
        NetworkAuditEvent connected = Assert.Single(store.Read(alice, 0, 50, null).Events, item => item.Connected);
        Assert.True(connected.Closed);
        Assert.Equal("closed", connected.Outcome);
        Assert.Equal(42, connected.BytesSent);
        Assert.Equal(2, store.Read(alice, 0, 50, null).Events.Count);
    }

    [Fact]
    public void CorruptOrUnknownAllocationIsUnattributedAndDoesNotPromptAnotherOwner()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        string allocation = Allocate(store);
        File.WriteAllText(Path.Combine(AuditRoot, "allocations", allocation + ".json"), "[]");
        var restarted = new NetworkAccessAuditStore(AuditRoot);
        restarted.Observe(envoy, [Line(allocation)], false);
        Assert.Empty(restarted.Read(alice, 0, 50, null).Events);
        Assert.Null(Assert.Single(restarted.Read(admin, 0, 50, null).Events).Identity);
        Assert.Equal(1, restarted.Read(admin, 0, 50, null).UnattributedConnections);
    }

    [Fact]
    public void PagingFiltersOwnerBeforeSelectingRowsAndCoverageGapSurvivesRestart()
    {
        var store = new NetworkAccessAuditStore(AuditRoot);
        string owner = Allocate(store);
        string other = Allocate(store, identity with { UserId = "bob" });
        store.Observe(envoy, [Line(owner, 1), Line(other, 2), Line(owner, 3), Line(other, 4), Line(owner, 5)], true);
        NetworkAuditPage first = store.Read(alice, 0, 2, null);
        Assert.Equal(2, first.Events.Count);
        Assert.All(first.Events, item => Assert.Equal("alice", item.Identity!.UserId));
        Assert.True(first.NextBefore > 0);
        Assert.Single(store.Read(alice, first.NextBefore, 2, null).Events);
        DateTimeOffset until = DateTimeOffset.Parse("2026-11-07T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        store.CommitCheckpoint(envoy, until);
        var restarted = new NetworkAccessAuditStore(AuditRoot);
        Assert.Equal(until, restarted.Checkpoint.Cursor);
        Assert.Equal(1, restarted.Read(alice, 0, 50, null).CoverageGaps);
    }
}
