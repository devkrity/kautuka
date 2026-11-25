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

    [Fact]
    public void ExpiredRequestCannotAuthorizeAfterRestartOrFreshRequest()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-request-expiry-").FullName;
        string path = Path.Combine(root, "policy.json");
        try
        {
            DateTimeOffset old = DateTimeOffset.UtcNow.AddHours(-25);
            string expired;
            using (var store = new NetworkAccessPolicyStore(path, []))
            {
                expired = Assert.IsType<string>(Record(store, old));
                Assert.Equal(expired, Record(store, old.AddMinutes(1)));
            }
            using var reopened = new NetworkAccessPolicyStore(path, []);
            var admin = new NetworkAccessActor("admin", IsAdmin: true);
            Assert.Throws<KeyNotFoundException>(() => reopened.DecideRequest(admin, expired, true, "agent", reopened.Revision));
            string fresh = Assert.IsType<string>(Record(reopened, DateTimeOffset.UtcNow));
            Assert.NotEqual(expired, fresh);
            Assert.Equal(fresh, Record(reopened, DateTimeOffset.UtcNow));
            Assert.Equal(2, Assert.Single(reopened.ListRequests(admin).Requests).Request.Attempts);
            Assert.Throws<KeyNotFoundException>(() => reopened.DecideRequest(admin, expired, true, "agent", reopened.Revision));
            reopened.DecideRequest(admin, fresh, true, "agent", reopened.Revision);
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

    private static string? Record(NetworkAccessPolicyStore store, DateTimeOffset at)
        => store.RecordOrUpdatePendingRequest("user-a", "agent-a", null, "user-agent", "host",
            "expiry.example.test", 443, "agent-sandbox", "container", observedAtUtc: at).RequestId;
}
