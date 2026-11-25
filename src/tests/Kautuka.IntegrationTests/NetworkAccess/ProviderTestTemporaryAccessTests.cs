using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ProviderTestTemporaryAccessTests
{
    [Fact]
    public void TemporaryAccessExpiresWhenCompletionCallFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "provider-test-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var clock = new ManualClock();
            var controller = new NetworkAccessController.NetworkAccessController(store, new WorkloadRegistry(), timeProvider: clock);
            var admin = new NetworkAccessActor("admin", true);
            string token = controller.PrepareProviderTest(admin, "provider.example", 443, false, confirmed: true).Token;
            var request = new OutboundCheckInput("admin", "gateway", null, "provider.example", 443, "provider-test", token);

            Assert.True(controller.EvaluateOutboundCheck(request).Allowed);
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.True(controller.EvaluateOutboundCheck(request).Allowed);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(controller.EvaluateOutboundCheck(request).Allowed);
            Assert.DoesNotContain(store.ListRules(admin).Rules, rule => rule.Rule.Destination == "provider.example");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }

    [Fact]
    public void TemporaryAccessOnlyAppliesToTheApprovedTestAndLeavesNoRule()
    {
        string root = Path.Combine(Path.GetTempPath(), "provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var controller = new NetworkAccessController.NetworkAccessController(store, new WorkloadRegistry());
            var admin = new NetworkAccessActor("admin", true);
            string token = controller.PrepareProviderTest(admin, "provider.example", 443, false, confirmed: true).Token;
            OutboundCheckInput Test(string user, string host, string id) =>
                new(user, "gateway", null, host, 443, "provider-test", id);
            Assert.True(controller.EvaluateOutboundCheck(Test("admin", "provider.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("other", "provider.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "other.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "provider.example", "other")).Allowed);
            OutboundCheckInput original = Test("admin", "provider.example", token);
            Assert.False(controller.EvaluateOutboundCheck(original with { AgentId = "other-agent" }, recordPendingRequest: false).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(original with { AgentId = string.Empty }, recordPendingRequest: false).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(original with { ConnectorId = "some-connector" }, recordPendingRequest: false).Allowed);
            controller.EndProviderTest(admin, token);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "provider.example", token)).Allowed);
            Assert.DoesNotContain(store.ListRules(admin).Rules, rule => rule.Rule.Destination == "provider.example");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
