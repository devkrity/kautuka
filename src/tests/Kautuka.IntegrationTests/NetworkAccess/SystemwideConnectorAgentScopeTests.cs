using Kautuka.NetworkAccessController;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Manager.RuntimeContainer.Sandbox;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class SystemwideConnectorAgentScopeTests
{
    [Fact]
    public void ExistingUserConnectorApprovalContinuesToWorkForItsOwner()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-system-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", null, "old", "connector", "alice::mail", "isolated", true, "normal", "user", null, DateTimeOffset.UtcNow));
            NetworkAccessDecisionResult oldRequest = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "mail.example", true));
            Assert.False(oldRequest.Allowed);
            store.DecideRequest(admin, oldRequest.RequestId!, true, "user", store.Revision);
            registry.Register(new RegistryEntry("10.247.1.11", "alice", "agent-a", "new", "connector", "alice::mail", "isolated", true, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.11", "93.184.216.34", 443, "mail.example", true)).Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SystemwideConnectorKeepsInvokingAgentIdentity()
    {
        var connector = new WorkspaceMcpContainerConnectorDefinition("alice::mail", "image", "command", [], true, false, null, null, [], 30, 600) { CatalogScope = "system" };
        var workspace = new ContainerContext("agent", "alice-agent-a", "alice", "/tmp/a", "/workspace", "image", 1, "512m", 30, [], new Dictionary<string, string>()) { AgentId = "agent-a" };
        ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(connector, workspace, null);
        Assert.Equal("alice", identity.UserId);
        Assert.Equal("agent-a", identity.AgentId);
        Assert.True(identity.Systemwide);
        Assert.Equal("agent-a", PodmanContainerTool.BuildSandboxRegistryEntry(workspace, "10.247.1.12", "sandbox").AgentId);
    }

    [Fact]
    public void SystemwideConnectorUsesOrdinaryAgentAndUserApprovalScopes()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-system-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var registry = new WorkloadRegistry();
            var controller = new NetworkAccessController.NetworkAccessController(store, registry);
            var admin = new NetworkAccessActor("admin", true);
            store.SetUserPrivileges(admin, "alice", canApproveOwn: true, canAllowAllAgents: true, targetIsAdmin: false);
            NetworkAccessDecisionResult gatewayPending = controller.EvaluateOutboundCheck(new OutboundCheckInput("alice", "agent-a", "alice::mail", true, "other.example", 443, "agent-run", "test"));
            Assert.False(gatewayPending.Allowed);
            NetworkAccessRequest gatewayRequest = Assert.Single(store.ListRequests(new NetworkAccessActor("alice", false)).Requests).Request;
            Assert.Equal("agent-a", gatewayRequest.AgentId);
            Assert.Equal("connector", gatewayRequest.ActorKind);
            store.DecideRequest(new NetworkAccessActor("alice", false), gatewayRequest.Id, true, "agent", store.Revision);
            registry.Register(new RegistryEntry("10.247.1.10", "alice", "agent-a", "container-a", "connector", "alice::mail", "isolated", true, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry("10.247.1.11", "alice", "agent-b", "container-b", "connector", "alice::mail", "isolated", true, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            NetworkAccessDecisionResult Check(string ip) => controller.EvaluateContainerConnection(new ContainerConnectionInput(ip, "93.184.216.34", 443, "mail.example", true));
            NetworkAccessDecisionResult first = Check("10.247.1.10");
            Assert.False(first.Allowed);
            NetworkAccessRequest request = Assert.Single(store.ListRequests(new NetworkAccessActor("alice", false)).Requests, item => item.Request.Status == "pending").Request;
            Assert.Equal("alice", request.UserId);
            Assert.Equal("agent-a", request.AgentId);
            store.DecideRequest(new NetworkAccessActor("alice", false), request.Id, true, "agent", store.Revision);
            Assert.True(Check("10.247.1.10").Allowed);
            Assert.True(controller.EvaluateOutboundCheck(new OutboundCheckInput("alice", "agent-a", "alice::mail", true, "mail.example", 443, "agent-run", "test")).Allowed);
            Assert.False(Check("10.247.1.11").Allowed);
            NetworkAccessRequest second = Assert.Single(store.ListRequests(new NetworkAccessActor("alice", false)).Requests, item => item.Request.Status == "pending").Request;
            store.DecideRequest(new NetworkAccessActor("alice", false), second.Id, true, "user", store.Revision);
            Assert.True(Check("10.247.1.11").Allowed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
