using System.Reflection;

using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessTopologyGateTests
{
    [Fact]
    public async Task ProductionManagerWiring_UsesLiveTopologyGateForContainerAuthorization()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-topology-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.11", "user-a", "agent-a", "container-a", "agent-sandbox", null,
                "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            string policyPath = Path.Combine(root, "policy.json");
            services.AddSingleton<IWorkloadRegistry>(registry);
            services.AddSingleton(new NetworkAccessPolicyStore(policyPath));
            services.AddSingleton(new NetworkAccessAuditRing());
            services.AddSingleton(new PodmanCommandRunner());
            services.AddSingleton(sp => new WorkloadAddressAllocator(Path.Combine(root, "ip.json")));
            services.AddSingleton<WorkloadPostStartVerifier>();
            services.AddSingleton(sp => new EgressTopologyManager(
                sp.GetRequiredService<PodmanCommandRunner>(),
                sp.GetRequiredService<WorkloadAddressAllocator>(),
                sp.GetRequiredService<WorkloadPostStartVerifier>(),
                sp.GetRequiredService<IWorkloadRegistry>(),
                root));
            services.AddManagerNetworkAccessController();

            await using ServiceProvider provider = services.BuildServiceProvider();
            EgressTopologyManager topology = provider.GetRequiredService<EgressTopologyManager>();
            Kautuka.NetworkAccessController.NetworkAccessController controller =
                provider.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessController>();
            var actor = new NetworkAccessActor("admin-a", IsAdmin: true);
            provider.GetRequiredService<NetworkAccessPolicyStore>().SetRule(
                actor,
                new RuleWriteSpec("agent", "user-a", "agent-a", null, "allowed.test", "host", 443, Allowed: true),
                provider.GetRequiredService<NetworkAccessPolicyStore>().Revision);
            SetTopologyGate(topology, isOpen: true);

            Assert.True(controller.EvaluateContainerConnection(
                "10.247.1.11", "198.18.250.250", 443, isTls: true, sni: "allowed.test").Allowed);

            SetTopologyGate(topology, isOpen: false);
            NetworkAccessDecisionResult denied = controller.EvaluateContainerConnection(
                "10.247.1.11", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
            Assert.False(denied.Allowed);
            Assert.Equal("topology_closed", denied.Reason);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void EvaluateContainerConnection_DeniesNewAuthorizationWhenTopologyGateIsClosed_WithNegativeControl()
    {
        string root = Path.Combine(Path.GetTempPath(), $"nac-topology-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            bool gateOpen = true;
            Func<bool> readGate = () => gateOpen;
            var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.10", "user-a", "agent-a", "container-a", "agent-sandbox", null,
                "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(
                policy,
                registry,
                new NetworkAccessAuditRing(),
                isContainerAuthorizationGateOpen: () => readGate());
            var actor = new NetworkAccessActor("admin-a", IsAdmin: true);
            policy.SetRule(
                actor,
                new RuleWriteSpec("agent", "user-a", "agent-a", null, "allowed.test", "host", 443, Allowed: true),
                policy.Revision);

            NetworkAccessDecisionResult allowedBeforeIncident = controller.EvaluateContainerConnection(
                "10.247.1.10", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
            Assert.True(allowedBeforeIncident.Allowed);

            gateOpen = false;
            NetworkAccessDecisionResult deniedDuringIncident = controller.EvaluateContainerConnection(
                "10.247.1.10", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
            Assert.False(deniedDuringIncident.Allowed);
            Assert.Equal("topology_closed", deniedDuringIncident.Reason);

            // Negative control (.NC): reopening the gate restores the existing policy decision;
            // the incident gate did not persist or rewrite a system brake or the user's rule.
            gateOpen = true;
            NetworkAccessDecisionResult allowedAfterRecovery = controller.EvaluateContainerConnection(
                "10.247.1.10", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
            Assert.True(allowedAfterRecovery.Allowed);
            Assert.True(policy.ListRules(new NetworkAccessActor("admin-a", IsAdmin: true))
                .Rules.Single(r => r.Rule.Destination == "allowed.test").Rule.Allowed);

            // Failure to read topology health also denies without changing policy state.
            readGate = static () => throw new InvalidOperationException("topology state unavailable");
            NetworkAccessDecisionResult deniedWhenGateUnreadable = controller.EvaluateContainerConnection(
                "10.247.1.10", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
            Assert.False(deniedWhenGateUnreadable.Allowed);
            Assert.Equal("topology_closed", deniedWhenGateUnreadable.Reason);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void SetTopologyGate(EgressTopologyManager manager, bool isOpen)
    {
        FieldInfo field = typeof(EgressTopologyManager).GetField("isStartupGateOpen", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected the topology gate state field.");
        field.SetValue(manager, isOpen);
    }
}
