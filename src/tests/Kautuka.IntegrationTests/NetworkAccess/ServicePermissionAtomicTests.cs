using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ServicePermissionAtomicTests
{
    [Fact]
    public void RevisionConflictAndFailedWriteLeaveAllServiceRulesUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool fail = false;
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), _ => { },
                beforeRenameHook: _ => { if (fail) throw new IOException("injected failure"); });
            var admin = new NetworkAccessActor("admin", true);
            long revision = store.Revision;
            NetworkAccessPolicyState initial = store.GetRawStateSnapshot();
            Assert.Throws<InvalidOperationException>(() => store.SetServicePermission(admin, "updates", true, revision + 1));
            fail = true;
            Assert.Throws<IOException>(() => store.SetServicePermission(admin, "updates", true, revision));
            Assert.Equal(revision, store.Revision);
            Assert.Equal(initial.Rules, store.GetRawStateSnapshot().Rules);
            using (var reopened = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json")))
            {
                Assert.Equal(revision, reopened.Revision);
                Assert.Equal(initial.Rules, reopened.GetRawStateSnapshot().Rules);
            }
            fail = false;
            (long next, string status) = store.SetServicePermission(admin, "updates", true, revision);
            Assert.Equal(revision + 1, next);
            Assert.Equal("allowed", status);
            Assert.All(store.SeedRules.Where(r => r.SeedSetupService == "updates"), seed =>
                Assert.True(store.GetRawStateSnapshot().Rules.Single(rule => rule.Id == seed.Id).Allowed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("system", null)]
    [InlineData("user", "system")]
    public void HigherPriorityBlockAndServiceBrakeRemainAndAreReported(string scope, string? userId)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var admin = new NetworkAccessActor("admin", true);
            (long revision, _) = store.SetRule(admin, new RuleWriteSpec(scope, userId, null, null,
                "api.github.com", "host", 443, false), store.Revision);
            (long next, string status) = store.SetServicePermission(admin, "updates", true, revision);
            Assert.Equal("restricted", status);
            Assert.Equal(revision + 1, next);
            var (_, _, services) = store.ListRulesWithServices(admin, null);
            ServicePermissionView updates = Assert.Single(services, service => service.ServiceId == "updates");
            Assert.True(updates.Granted > updates.Effective);
            Assert.Contains(store.GetRawStateSnapshot().Rules, rule => rule.ScopeKind == scope && rule.Destination == "api.github.com" && !rule.Allowed);

            store.SetBrake(admin, "user", "system", on: true);
            var (_, _, brakedServices) = store.ListRulesWithServices(admin, null);
            ServicePermissionView brakedUpdates = Assert.Single(brakedServices, service => service.ServiceId == "updates");
            Assert.Equal(updates.Granted, brakedUpdates.Granted);
            Assert.Equal(0, brakedUpdates.Effective);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
