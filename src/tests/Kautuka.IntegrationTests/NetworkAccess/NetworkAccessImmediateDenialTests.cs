using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessImmediateDenialTests
{
    [Fact]
    public void RenderedDeadlinesKeepPackageVerificationBoundedWithoutHoldingGatewayConnect()
    {
        string config = BoundaryRulesetRenderer.RenderEnvoyConfig(includeGatewayConnect: true);
        int gateway = config.IndexOf("- name: gateway_connect", StringComparison.Ordinal);
        Assert.True(gateway > 0);
        Assert.Contains("timeout: 35s", config[..gateway], StringComparison.Ordinal);
        Assert.Contains("timeout: 2s", config[gateway..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData(2, 2)]
    public void PendingRequestsExpireFromFirstSightingAcrossRestart(int? configuredHours, int expectedHours)
    {
        string root = Directory.CreateTempSubdirectory("kautuka-request-expiry-").FullName;
        string path = Path.Combine(root, "policy.json");
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        TimeSpan? retention = configuredHours is null ? null : TimeSpan.FromHours(configuredHours.Value);
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        try
        {
            string expired;
            using (var store = new NetworkAccessPolicyStore(path, [], timeProvider: clock, pendingRequestRetention: retention))
            {
                expired = Assert.IsType<string>(Record(store));
                clock.Advance(TimeSpan.FromHours(expectedHours) - TimeSpan.FromSeconds(1));
                Assert.Equal(expired, Record(store));
                Assert.Equal(2, Assert.Single(store.ListRequests(admin).Requests).Request.Attempts);
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            using var reopened = new NetworkAccessPolicyStore(path, [], timeProvider: clock, pendingRequestRetention: retention);
            Assert.Throws<KeyNotFoundException>(() => reopened.DecideRequest(admin, expired, true, "agent", reopened.Revision));
            string fresh = Assert.IsType<string>(Record(reopened));
            Assert.NotEqual(expired, fresh);
            Assert.Equal(fresh, Record(reopened));
            Assert.Equal(2, Assert.Single(reopened.ListRequests(admin).Requests).Request.Attempts);
            Assert.Throws<KeyNotFoundException>(() => reopened.DecideRequest(admin, expired, true, "agent", reopened.Revision));
            reopened.DecideRequest(admin, fresh, true, "agent", reopened.Revision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositivePendingRequestRetentionIsRejected(int seconds)
    {
        string path = Path.Combine(Path.GetTempPath(), "kautuka-invalid-retention-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetworkAccessPolicyStore(path, [], pendingRequestRetention: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TemporaryProbeRequestUsesConfiguredClockAndCannotReuseExpiredId()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-probe-expiry-").FullName;
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), [],
                timeProvider: clock, pendingRequestRetention: TimeSpan.FromMinutes(30));
            string? RecordProbe() => store.RecordOrUpdatePendingProbeRequest("probe-a", "user-a", "agent-a",
                "connector-a", "user-agent", "host", "probe.example.test", 443, "connector", "container");
            string old = Assert.IsType<string>(RecordProbe());
            clock.Advance(TimeSpan.FromMinutes(30));
            var admin = new NetworkAccessActor("admin", IsAdmin: true);
            Assert.Throws<KeyNotFoundException>(() => store.DecideRequest(admin, old, true, null, store.Revision));
            string fresh = Assert.IsType<string>(RecordProbe());
            Assert.NotEqual(old, fresh);
            Assert.Throws<KeyNotFoundException>(() => store.DecideRequest(admin, old, true, null, store.Revision));
            Assert.Equal(fresh, Assert.Single(store.ListRequests(admin).Requests).Request.Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnknownGatewayAndContainerDenyImmediatelyUntilFreshAttemptAfterApproval()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-request-retry-").FullName;
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var registry = new WorkloadRegistry();
            const string ip = "10.247.0.40";
            registry.Register(new RegistryEntry(ip, "user-a", "agent-a", new string('a', 64), "agent-sandbox", null,
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            bool gateOpen = true;
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(store, registry,
                isContainerAuthorizationGateOpen: () => gateOpen, applianceMode: true);
            var gateway = new OutboundCheckInput("user-a", "agent-a", null, "gateway.example.test", 443, "remote-mcp", null);
            NetworkAccessDecisionResult first = controller.EvaluateGatewayConnect(gateway, publicOnly: true);
            Assert.False(first.Allowed);
            Assert.Equal("no_rule", first.Reason);
            Assert.Equal(first.RequestId, controller.EvaluateGatewayConnect(gateway, publicOnly: true).RequestId);
            var admin = new NetworkAccessActor("admin", IsAdmin: true);
            store.DecideRequest(admin, Assert.IsType<string>(first.RequestId), true, "agent", store.Revision);
            Assert.True(controller.EvaluateGatewayConnect(gateway, publicOnly: true).Allowed);
            gateOpen = false;
            Assert.Equal("topology_closed", controller.EvaluateGatewayConnect(gateway, publicOnly: true).Reason);
            gateOpen = true;

            NetworkAccessDecisionResult denied = controller.EvaluateContainerConnection(ip, NetworkAddressClassifier.SinkholeAddress,
                443, true, "container.example.test");
            Assert.False(denied.Allowed);
            Assert.Equal("no_rule", denied.Reason);
            store.DecideRequest(admin, Assert.IsType<string>(denied.RequestId), true, "agent", store.Revision);
            Assert.True(controller.EvaluateContainerConnection(ip, NetworkAddressClassifier.SinkholeAddress,
                443, true, "container.example.test").Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string? Record(NetworkAccessPolicyStore store)
        => store.RecordOrUpdatePendingRequest("user-a", "agent-a", null, "user-agent", "host",
            "expiry.example.test", 443, "agent-sandbox", "container").RequestId;

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
