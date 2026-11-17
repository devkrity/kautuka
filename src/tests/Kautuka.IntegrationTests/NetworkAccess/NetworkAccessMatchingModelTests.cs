using Kautuka.NetworkAccessController;

using System.Text.Json;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessMatchingModelTests
{
    [Fact]
    public void DocumentedScopeExamplesMatchContainerAndDiscoveryDecisions()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-examples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "alice-secretary", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry("10.247.1.11", "alice", "researcher", "alice-researcher", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry("10.247.1.12", "bob", "secretary", "bob-secretary", "connector", "bob::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            void Add(string scope, string? user, string? agent, string? connector, string host, bool allowed)
                => store.SetRule(admin, new RuleWriteSpec(scope, user, agent, connector, host, "host", 443, allowed), store.Revision);
            bool Allowed(string ip, string host) => controller.EvaluateContainerConnection(new ContainerConnectionInput(ip, "93.184.216.34", 443, host, true)).Allowed;

            Add("system", null, null, null, "example1.test", true);
            Assert.True(Allowed("10.247.1.10", "example1.test"));
            Assert.True(Allowed("10.247.1.12", "example1.test"));
            Add("user", "alice", null, null, "example2.test", true);
            Assert.True(Allowed("10.247.1.11", "example2.test"));
            Assert.False(Allowed("10.247.1.12", "example2.test"));
            Add("agent", "alice", "secretary", null, "example3.test", true);
            Assert.True(Allowed("10.247.1.10", "example3.test"));
            Assert.False(Allowed("10.247.1.11", "example3.test"));
            Add("user", "alice", null, "web", "example4.test", true);
            Assert.True(Allowed("10.247.1.10", "example4.test"));
            Assert.False(Allowed("10.247.1.12", "example4.test"));
            Assert.True(controller.EvaluateOutboundCheck(new OutboundCheckInput("alice", "", "alice::web", "example4.test", 443, "save-discovery", null)).Allowed);
            Add("system", null, null, null, "example5.test", true);
            Add("user", "alice", null, null, "example5.test", false);
            Assert.False(Allowed("10.247.1.10", "example5.test"));
            Add("user", "alice", null, "web", "example6.test", true);
            Add("agent", "alice", "secretary", null, "example6.test", false);
            Assert.False(Allowed("10.247.1.10", "example6.test"));
            Assert.True(Allowed("10.247.1.11", "example6.test"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RevokingManagementPausesOwnRulesAcrossScopesAndRegrantRestoresThem()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var admin = new NetworkAccessActor("admin", true);
            var alice = new NetworkAccessActor("alice", false);
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            store.SetUserPrivileges(admin, "alice", true, false, false);
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, null, "user.example", "host", 443, true), store.Revision);
            store.SetRule(alice, new RuleWriteSpec("agent", "alice", "secretary", null, "agent.example", "host", 443, true), store.Revision);
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, "web", "connector.example", "host", 443, true), store.Revision);
            store.SetRule(new NetworkAccessActor("alice", true), new RuleWriteSpec("user", "alice", null, null, "admin.example", "host", 443, true), store.Revision);
            store.SetRule(admin, new RuleWriteSpec("system", null, null, null, "admin-block.example", "host", 443, true), store.Revision);
            store.SetRule(admin, new RuleWriteSpec("user", "alice", null, null, "admin-block.example", "host", 443, false), store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            string[] destinations = ["user.example", "agent.example", "connector.example"];
            bool Allowed(string host) => controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, host, true)).Allowed;
            Assert.All(destinations, host => Assert.True(Allowed(host)));

            store.SetUserPrivileges(admin, "alice", false, false, false);
            Assert.All(destinations, host => Assert.False(Allowed(host)));
            Assert.True(Allowed("admin.example"));
            Assert.False(Allowed("admin-block.example"));
            Assert.All(store.ListRules(admin).Rules.Where(view => view.Rule.UserId == "alice" && !view.Rule.Destination.StartsWith("admin", StringComparison.Ordinal)), view => Assert.True(view.Paused));
            Assert.False(store.ListRules(admin).Rules.Single(view => view.Rule.Destination == "admin.example").Paused);
            using (var reopened = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json")))
            {
                Assert.True(reopened.ListRules(admin).Rules.Single(view => view.Rule.Destination == "user.example").Paused);
                Assert.False(reopened.ListRules(admin).Rules.Single(view => view.Rule.Destination == "admin.example").Paused);
            }

            store.SetUserPrivileges(admin, "alice", true, false, false);
            Assert.All(destinations, host => Assert.True(Allowed(host)));
            Assert.All(store.ListRules(admin).Rules.Where(view => view.Rule.UserId == "alice"), view => Assert.False(view.Paused));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UserConnectorRuleAppliesAcrossAgentsButAgentBlockWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-matching-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            store.SetRule(admin, new RuleWriteSpec("user", "alice", null, "web", "example.com", "host", 443, true), store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));

            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true)).Allowed);
            registry.Register(new RegistryEntry("10.247.1.11", "bob", "secretary", "bob-container", "connector", "bob::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.11", "93.184.216.34", 443, "example.com", true)).Allowed);

            store.SetRule(admin, new RuleWriteSpec("agent", "alice", "secretary", null, "example.com", "host", 443, false), store.Revision);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true)).Allowed);
            registry.Register(new RegistryEntry("10.247.1.12", "alice", "researcher", "researcher-container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.12", "93.184.216.34", 443, "example.com", true)).Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OwnerCanCreateOwnConnectorRuleWithoutAdministrator()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var alice = new NetworkAccessActor("alice", false);
            Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, new RuleWriteSpec("user", "alice", null, "alice::web", "example.com", "host", 443, true), store.Revision));
            store.SetUserPrivileges(new NetworkAccessActor("admin", true), "alice", canApproveOwn: true, canAllowAllAgents: false, targetIsAdmin: false);
            (_, NetworkAccessRuleView connectorRule) = store.SetRule(alice, new RuleWriteSpec("user", "alice", null, "alice::web", "example.com", "host", 443, true), store.Revision);
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, null, "other.example", "host", 443, true), store.Revision);
            store.SetRule(alice, new RuleWriteSpec("agent", "alice", "secretary", null, "third.example", "host", 443, true), store.Revision);
            Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, new RuleWriteSpec("user", "bob", null, "bob::web", "example.com", "host", 443, true), store.Revision));
            Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, new RuleWriteSpec("system", null, null, null, "example.com", "host", 443, true), store.Revision));
            store.DeleteRule(alice, connectorRule.Rule.Id, store.Revision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExplicitBlockWinsAcrossPlatformAndAgent(bool platformAllow, bool agentAllow)
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-block-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            store.SetRule(admin, new RuleWriteSpec("system", null, null, null, "example.com", "host", 443, platformAllow), store.Revision);
            store.SetRule(admin, new RuleWriteSpec("agent", "alice", "secretary", null, "example.com", "host", 443, agentAllow), store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true)).Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DiscoveryWithoutAgentRequiresAndUsesUserConnectorRule()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", null, "container", "connector", "alice::web", "isolated", "normal", "user", null, DateTimeOffset.UtcNow, "save-discovery"));
            NetworkAccessDecisionResult Check() => controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true));
            NetworkAccessDecisionResult denied = Check();
            Assert.False(denied.Allowed);
            Assert.Equal("no_rule", denied.Reason);
            Assert.NotNull(denied.RequestId);
            store.SetUserPrivileges(new NetworkAccessActor("admin", true), "alice", canApproveOwn: true, canAllowAllAgents: false, targetIsAdmin: false);
            Assert.True(registry.Deregister("10.247.1.10", "container"));
            Assert.Contains(store.ListRequests(new NetworkAccessActor("alice", false)).Requests, item => item.Request.Id == denied.RequestId);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", null, "container", "connector", "alice::web", "isolated", "normal", "user", null, DateTimeOffset.UtcNow, "save-discovery"));
            DecideRequestResult approval = store.DecideRequest(new NetworkAccessActor("alice", false), denied.RequestId!, true, "connector", store.Revision);
            Assert.Equal("alice::web", approval.Rule?.ConnectorId);
            Assert.True(Check().Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UserAllowAppliesWithoutLegacyAllAgentsPrivilegeButConnectorBlockWins()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            store.SetUserPrivileges(admin, "alice", canApproveOwn: true, canAllowAllAgents: false, targetIsAdmin: false);
            var alice = new NetworkAccessActor("alice", false);
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, null, "example.com", "host", 443, true), store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            NetworkAccessDecisionResult Check() => controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true));
            Assert.True(Check().Allowed);
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, "web", "example.com", "host", 443, false), store.Revision);
            Assert.False(Check().Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ApprovalDoesNotResolveRequestWhileAnotherApplicableRuleBlocks()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            NetworkAccessDecisionResult pending = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true));
            store.SetRule(admin, new RuleWriteSpec("system", null, null, null, "example.com", "host", 443, false), store.Revision);
            DecideRequestResult decision = store.DecideRequest(admin, pending.RequestId!, true, "agent", store.Revision);
            Assert.Equal("pending", decision.Request.Status);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com", true)).Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PlatformAllowCannotOverrideSystemServiceBlock()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-system-block-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var controller = new NetworkAccessController.NetworkAccessController(store, new WorkloadRegistry());
            var admin = new NetworkAccessActor("admin", true);
            store.SetRule(admin, new RuleWriteSpec("system", null, null, null, "example.com", "host", 443, true), store.Revision);
            store.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "example.com", "host", 443, false), store.Revision);
            Assert.False(controller.EvaluateOutboundCheck(new OutboundCheckInput("system", "channelhost", null, "example.com", 443, null, null)).Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExplicitPlatformPackageProxyAllowStillUsesPackageProxyCluster()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            store.SetRule(admin, new RuleWriteSpec("system", null, null, null, "198.18.250.249", "ip", 20101, true), store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "connector", "alice::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            NetworkAccessDecisionResult result = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "198.18.250.249", 20101, null, false));
            Assert.True(result.Allowed, result.Reason);
            Assert.Equal("pkgs_proxy", result.Cluster);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EditingRuleScopeReplacesOldRuleAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var admin = new NetworkAccessActor("admin", true);
            store.SetUserPrivileges(admin, "alice", canApproveOwn: true, canAllowAllAgents: false, targetIsAdmin: false);
            var alice = new NetworkAccessActor("alice", false);
            (_, NetworkAccessRuleView oldRule) = store.SetRule(alice, new RuleWriteSpec("user", "alice", null, "web", "example.com", "host", 443, true), store.Revision);
            long before = store.Revision;
            store.SetRule(alice, new RuleWriteSpec("user", "alice", null, null, "example.com", "host", 443, true), before, oldRule.Rule.Id);
            NetworkAccessPolicyState state = store.GetRawStateSnapshot();
            Assert.Equal(before + 1, state.Revision);
            NetworkAccessRule rule = Assert.Single(state.Rules);
            Assert.Null(rule.ConnectorId);
            store.SetUserPrivileges(admin, "bob", canApproveOwn: true, canAllowAllAgents: false, targetIsAdmin: false);
            Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(new NetworkAccessActor("bob", false), new RuleWriteSpec("user", "bob", null, null, "example.com", "host", 443, true), store.Revision, rule.Id));
            Assert.Equal(rule.Id, Assert.Single(store.GetRawStateSnapshot().Rules).Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OldRulesAndPrivilegesAreResetOnceWithoutResettingBrakes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "policy.json");
            var oldRule = new NetworkAccessRule("old", "connector-system", null, null, "web", "example.com", "host", 443, true, false, "admin", DateTimeOffset.UtcNow);
            var privilege = new UserPrivileges("alice", true, false, "admin", DateTimeOffset.UtcNow);
            var brake = new BrakeState(true, "admin", DateTimeOffset.UtcNow, []);
            var oldState = new NetworkAccessPolicyState(1, 5, [oldRule], [], [privilege], brake);
            File.WriteAllText(path, JsonSerializer.Serialize(oldState, JsonSerializerOptions.Web));
            using (var store = new NetworkAccessPolicyStore(path))
            {
                NetworkAccessPolicyState state = store.GetRawStateSnapshot();
                Assert.Empty(state.Rules);
                Assert.Empty(state.UserPrivileges);
                Assert.True(state.Brake.System);
                store.SetRule(new NetworkAccessActor("admin", true), new RuleWriteSpec("system", null, null, null, "new.example", "host", 443, true), store.Revision);
            }
            using var reopened = new NetworkAccessPolicyStore(path);
            Assert.Single(reopened.GetRawStateSnapshot().Rules);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
