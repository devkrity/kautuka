using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ProviderTestTemporaryAccessTests
{
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
            string token = controller.BeginProviderTest(admin, "provider.example", 443, false);
            OutboundCheckInput Test(string user, string host, string id) =>
                new(user, "gateway", null, true, host, 443, "provider-test", id);
            Assert.True(controller.EvaluateOutboundCheck(Test("admin", "provider.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("other", "provider.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "other.example", token)).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "provider.example", "other")).Allowed);
            controller.EndProviderTest(admin, token);
            Assert.False(controller.EvaluateOutboundCheck(Test("admin", "provider.example", token)).Allowed);
            Assert.DoesNotContain(store.ListRules(admin).Rules, rule => rule.Rule.Destination == "provider.example");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
