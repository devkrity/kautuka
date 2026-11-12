using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

/// <summary>
/// Controller &amp; Manager gRPC integration tests C11, C12, C13, C15, C17, C18, C19, C20, and C21 (with negative controls).
/// </summary>
public sealed class C11_To_C21_ControllerIntegrationTests
{
    [Fact]
    public async Task C11_Revocation_DeleteRuleImmediatelyDeniesAndCreatesNewPendingRequest_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.11", "user-a", "a1");
            ServerCallContext ctx = CreateCallContext();

            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };

            // 1. First connection is denied and creates a pending request
            NetworkAccessDecisionResult first = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.11", "93.184.216.34", 443, "example.com"));
            Assert.False(first.Allowed);
            Assert.NotNull(first.RequestId);

            // 2. Admin approves the request
            NetworkAccessDecideResponse decide = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = first.RequestId,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = 0,
            }, ctx);
            Assert.Equal(1, decide.Revision);

            // C11.NC: While the rule exists, connections succeed
            NetworkAccessDecisionResult allowed = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.11", "93.184.216.34", 443, "example.com"));
            Assert.True(allowed.Allowed);

            // 3. Delete the rule -> next connection is immediately denied and records a fresh pending request
            NetworkAccessDeleteRuleResponse del = await svc.DeleteRule(new NetworkAccessDeleteRuleRequest
            {
                Actor = admin,
                RuleId = decide.Rule.Id,
                ExpectedRevision = decide.Revision,
            }, ctx);
            Assert.Equal(2, del.Revision);

            NetworkAccessDecisionResult afterDelete = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.11", "93.184.216.34", 443, "example.com"));
            Assert.False(afterDelete.Allowed);
            Assert.Equal("no_rule", afterDelete.Reason);
            Assert.NotNull(afterDelete.RequestId);

            NetworkAccessListRequestsResponse reqs = await svc.ListRequests(new NetworkAccessListRequestsRequest
            {
                Actor = admin,
            }, ctx);
            Assert.Contains(reqs.Requests, r => r.Id == afterDelete.RequestId && r.Status == "pending");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void C12_CorruptStateFile_FailsClosedOnStart_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string statePath = Path.Combine(tempDir, "policy", "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, "{truncated-corrupt-json");

            // Corrupt state.json must refuse to start the controller (fail closed)
            Assert.Throws<InvalidDataException>(() => CreateGrpcService(tempDir, out _, out _));

            // C12.NC: Replacing with valid state.json allows the controller to start and serve
            string validDir = Path.Combine(tempDir, "valid");
            WorkspaceGrpcService svc = CreateGrpcService(validDir, out NacController validController, out _);
            Assert.NotNull(svc);
            Assert.Equal(0, validController.GetStatus().Revision);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C13_RequestCap500_501stOverflowsStillDenies_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.13", "user-a", "a1");
            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };

            string? firstRequestId = null;
            for (int i = 0; i < NetworkAccessPolicyStore.MaxPendingRequests; i++)
            {
                NetworkAccessDecisionResult res = controller.EvaluateContainerConnection(
                    new ContainerConnectionInput("10.247.0.13", "93.184.216.34", 443, $"host-{i}.example.com"));
                Assert.False(res.Allowed);
                Assert.Equal("no_rule", res.Reason);
                firstRequestId ??= res.RequestId;
            }

            // 501st distinct destination overflows the pending request cap, increments RequestOverflow, and still denies
            NetworkAccessDecisionResult overflow = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.13", "93.184.216.34", 443, "host-500.example.com"));
            Assert.False(overflow.Allowed);
            Assert.Equal("request_overflow", overflow.Reason);
            Assert.Null(overflow.RequestId);

            NetworkAccessStatusResponse status = await svc.GetNetworkAccessStatus(new WorkspacePingRequest(), ctx);
            Assert.Equal(1, status.RequestOverflow);

            // C13.NC: Resolving one pending request frees a slot so the 501st destination is recorded normally
            _ = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = firstRequestId!,
                Allow = false,
                Scope = "agent",
                ExpectedRevision = 0,
            }, ctx);

            NetworkAccessDecisionResult afterFree = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.13", "93.184.216.34", 443, "host-500.example.com"));
            Assert.False(afterFree.Allowed);
            Assert.Equal("no_rule", afterFree.Reason);
            Assert.NotNull(afterFree.RequestId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C15_ScopeEnforcementOnDecideAndSetRule_WithNegativeControls()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.21", "user-a", "a1");
            RegisterAgent(registry, "10.247.0.22", "user-b", "b1");
            registry.Register(new RegistryEntry(
                "10.247.0.23",
                null,
                null,
                "c-sys",
                "connector",
                "conn-sys",
                "isolated",
                true,
                "normal",
                "none",
                null,
                DateTimeOffset.UtcNow));

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            // Grant user-a canApproveOwn so we isolate cross-user / admin-only scope checks
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            NetworkAccessDecisionResult reqA = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.21", "93.184.216.34", 443, "a.example.com"));
            NetworkAccessDecisionResult reqB = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.22", "93.184.216.34", 443, "b.example.com"));
            NetworkAccessDecisionResult reqSys = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.23", "93.184.216.34", 443, "sys.example.com"));

            // 1. User A calling DecideRequest on User B's request -> PermissionDenied
            RpcException exCrossUser = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userA,
                    RequestId = reqB.RequestId!,
                    Allow = true,
                    Scope = "agent",
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exCrossUser.StatusCode);

            // 2. Non-admin calling DecideRequest on connector-system request -> PermissionDenied
            RpcException exSysReq = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userA,
                    RequestId = reqSys.RequestId!,
                    Allow = true,
                    Scope = "connector-system",
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exSysReq.StatusCode);

            // 3. Non-admin calling SetRule with scopeKind = connector-system -> PermissionDenied
            RpcException exSetSys = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = userA,
                    ScopeKind = "connector-system",
                    ConnectorId = "conn-sys",
                    Destination = "sys.example.com",
                    DestKind = "host",
                    Port = 443,
                    Allowed = true,
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exSetSys.StatusCode);

            // 4. Non-admin calling SetRule with allowLan = true -> PermissionDenied
            RpcException exSetLan = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = userA,
                    ScopeKind = "agent",
                    UserId = "user-a",
                    AgentId = "a1",
                    Destination = "192.168.1.50",
                    DestKind = "ip",
                    Port = 443,
                    Allowed = true,
                    AllowLan = true,
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exSetLan.StatusCode);

            // 5. Non-admin calling SetRule with destKind = any-public -> PermissionDenied
            RpcException exSetAnyPublic = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = userA,
                    ScopeKind = "user",
                    UserId = "user-a",
                    ConnectorId = "conn-iso",
                    Destination = "*",
                    DestKind = "any-public",
                    Port = 0,
                    Allowed = true,
                    ExecContext = "isolated",
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exSetAnyPublic.StatusCode);

            // 6. Admin calling SetRule with destKind = any-public for agent-sandbox -> InvalidArgument
            RpcException exSandboxAnyPublic = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = admin,
                    ScopeKind = "user",
                    UserId = "user-a",
                    ConnectorId = "conn-in-sandbox",
                    Destination = "*",
                    DestKind = "any-public",
                    Port = 0,
                    Allowed = true,
                    ExecContext = "agent-sandbox",
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.InvalidArgument, exSandboxAnyPublic.StatusCode);

            // C15.NC: Each rejected call's permitted twin succeeds!
            NetworkAccessDecideResponse twinOwn = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = reqA.RequestId!,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = 0,
            }, ctx);
            Assert.Equal(1, twinOwn.Revision);

            NetworkAccessDecideResponse twinSys = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = reqSys.RequestId!,
                Allow = true,
                Scope = "connector-system",
                ExpectedRevision = twinOwn.Revision,
            }, ctx);
            Assert.Equal(2, twinSys.Revision);

            NetworkAccessSetRuleResponse twinLan = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a1",
                Destination = "192.168.1.50",
                DestKind = "ip",
                Port = 443,
                Allowed = true,
                AllowLan = true,
                ExpectedRevision = twinSys.Revision,
            }, ctx);
            Assert.Equal(3, twinLan.Revision);

            NetworkAccessSetRuleResponse twinAnyPublic = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-iso",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExecContext = "isolated",
                ExpectedRevision = twinLan.Revision,
            }, ctx);
            Assert.Equal(4, twinAnyPublic.Revision);
            Assert.True(twinAnyPublic.Rule.OpenInternetBadge);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C17_PerAgentAttributionAndUserScopeOverride_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.31", "user-a", "a1");
            RegisterAgent(registry, "10.247.0.32", "user-a", "a2");
            RegisterAgent(registry, "10.247.0.33", "user-a", "a3");
            RegisterAgent(registry, "10.247.0.34", "user-b", "b1");

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            // Grant User A both privileges
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            // 1. Allow example.com:443 for a1 only (scope = agent)
            NetworkAccessSetRuleResponse rA1 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = userA,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a1",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = 0,
            }, ctx);

            // a1 succeeds; a2 is denied and records request for (user-a, a2); b1 is denied and records request for (user-b, b1)
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.31", "93.184.216.34", 443, "example.com")).Allowed);

            NetworkAccessDecisionResult dA2 = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.32", "93.184.216.34", 443, "example.com"));
            Assert.False(dA2.Allowed);
            Assert.NotNull(dA2.RequestId);

            NetworkAccessDecisionResult dB1 = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.34", "93.184.216.34", 443, "example.com"));
            Assert.False(dB1.Allowed);
            Assert.NotNull(dB1.RequestId);
            Assert.NotEqual(dA2.RequestId, dB1.RequestId);

            // 2. User A approves a2's request with scope = "user" (Allow for all my agents)
            NetworkAccessDecideResponse decideUserScope = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = dA2.RequestId,
                Allow = true,
                Scope = "user",
                ExpectedRevision = rA1.Revision,
            }, ctx);

            // Now a1, a2, and a3 all succeed; b1 is still denied!
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.31", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.32", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.33", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.34", "93.184.216.34", 443, "example.com")).Allowed);

            // 3. Block example.com:443 specifically for a3 (agent block beats user allow!)
            NetworkAccessSetRuleResponse blockA3 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = userA,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a3",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = false,
                ExpectedRevision = decideUserScope.Revision,
            }, ctx);

            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.31", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.32", "93.184.216.34", 443, "example.com")).Allowed);
            NetworkAccessDecisionResult a3Blocked = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.33", "93.184.216.34", 443, "example.com"));
            Assert.False(a3Blocked.Allowed);
            Assert.Equal("blocked", a3Blocked.Reason);

            // C17.NC: Deleting a3's agent block rule restores a3's access via User A's user-scope allow rule
            _ = await svc.DeleteRule(new NetworkAccessDeleteRuleRequest
            {
                Actor = userA,
                RuleId = blockA3.Rule.Id,
                ExpectedRevision = blockA3.Revision,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.33", "93.184.216.34", 443, "example.com")).Allowed);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C18_UserPrivileges_AllSixSubCases_WithNegativeControls()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.41", "user-a", "a1");
            RegisterAgent(registry, "10.247.0.42", "user-a", "a2");
            RegisterAgent(registry, "10.247.0.43", "user-b", "b1");

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            // Trigger pending requests for (user-a, a1) and (user-b, b1)
            NetworkAccessDecisionResult reqA1 = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "subcase-a.example.com"));
            NetworkAccessDecisionResult reqB1 = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.43", "93.184.216.34", 443, "subcase-b.example.com"));

            // (a) Default (both false): User A sees own request with WaitingForAdmin = true, cannot see User B's request,
            // and DecideRequest / SetRule by User A fail with PermissionDenied (privilege_required).
            NetworkAccessListRequestsResponse listDefault = await svc.ListRequests(new NetworkAccessListRequestsRequest
            {
                Actor = userA,
            }, ctx);
            Assert.False(listDefault.Privileges.CanApproveOwn);
            Assert.False(listDefault.Privileges.CanAllowAllAgents);
            NetworkAccessRequestMessage visibleA1 = Assert.Single(listDefault.Requests);
            Assert.Equal(reqA1.RequestId, visibleA1.Id);
            Assert.True(visibleA1.WaitingForAdmin);

            RpcException exDefaultDecide = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userA,
                    RequestId = reqA1.RequestId!,
                    Allow = true,
                    Scope = "agent",
                    ExpectedRevision = 0,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exDefaultDecide.StatusCode);
            Assert.Contains("privilege_required", exDefaultDecide.Status.Detail, StringComparison.Ordinal);

            // Admin approves reqA1 for a1 -> a1 allowed, a2 denied
            NetworkAccessDecideResponse adminApproveA1 = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = reqA1.RequestId!,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = 0,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "subcase-a.example.com")).Allowed);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.42", "93.184.216.34", 443, "subcase-a.example.com")).Allowed);

            // (b) canApproveOwn = true, canAllowAllAgents = false
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = false,
            }, ctx);

            NetworkAccessDecisionResult reqA1StepB = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "step-b.example.com"));
            NetworkAccessDecideResponse userApproveAgent = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = reqA1StepB.RequestId!,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = adminApproveA1.Revision,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "step-b.example.com")).Allowed);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.42", "93.184.216.34", 443, "step-b.example.com")).Allowed);

            // User A tries scope = "user" while canAllowAllAgents == false -> PermissionDenied (privilege_required)
            NetworkAccessDecisionResult reqA1UserTry = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "step-b-user.example.com"));
            RpcException exUserScopeDenied = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userA,
                    RequestId = reqA1UserTry.RequestId!,
                    Allow = true,
                    Scope = "user",
                    ExpectedRevision = userApproveAgent.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exUserScopeDenied.StatusCode);
            Assert.Contains("privilege_required", exUserScopeDenied.Status.Detail, StringComparison.Ordinal);

            // User A tries DecideRequest on User B's request -> PermissionDenied
            RpcException exCrossUserB = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userA,
                    RequestId = reqB1.RequestId!,
                    Allow = true,
                    Scope = "agent",
                    ExpectedRevision = userApproveAgent.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exCrossUserB.StatusCode);

            // (c) canApproveOwn = true, canAllowAllAgents = true (C18.NC: granting both allows scope = "user")
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            NetworkAccessDecideResponse userApproveAllAgents = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = reqA1UserTry.RequestId!,
                Allow = true,
                Scope = "user",
                ExpectedRevision = userApproveAgent.Revision,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.41", "93.184.216.34", 443, "step-b-user.example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.42", "93.184.216.34", 443, "step-b-user.example.com")).Allowed);

            // (d) canApproveOwn = false, canAllowAllAgents = true -> stored as (false, true), and User A cannot approve or write user rules
            NetworkAccessUserPrivilegesMessage privD = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = false,
                CanAllowAllAgents = true,
            }, ctx);
            Assert.False(privD.CanApproveOwn);
            Assert.True(privD.CanAllowAllAgents);

            RpcException exStepDSetRule = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = userA,
                    ScopeKind = "user",
                    UserId = "user-a",
                    Destination = "step-d.example.com",
                    DestKind = "host",
                    Port = 443,
                    Allowed = true,
                    ExpectedRevision = userApproveAllAgents.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exStepDSetRule.StatusCode);
            Assert.Contains("privilege_required", exStepDSetRule.Status.Detail, StringComparison.Ordinal);

            // (e) Self-escalation check: User A calling SetUserPrivileges for User A is rejected with PermissionDenied
            RpcException exSelfEscalate = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
                {
                    Actor = userA,
                    UserId = "user-a",
                    CanApproveOwn = true,
                    CanAllowAllAgents = true,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exSelfEscalate.StatusCode);

            // (f) Admin defaults are applied for decisions without writing from the read path, and demotion resets ON flags cleanly (v7-4).
            RegisterAgent(registry, "10.247.0.48", "admin-new", "admin-agent-1");
            RegisterAgent(registry, "10.247.0.49", "admin-new", "admin-agent-2");
            var newAdmin = new NetworkAccessActorMessage { UserId = "admin-new", IsAdmin = true };
            controller.PolicyStore.SetUserPrivileges(
                new NetworkAccessActor("admin-new", IsAdmin: true),
                "admin-new",
                canApproveOwn: true,
                canAllowAllAgents: true,
                targetIsAdmin: true);

            // Without any prior GetUserPrivileges call, admin-new lists/decides at 'user' scope:
            _ = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = newAdmin }, ctx);
            NetworkAccessSetRuleResponse adminRule = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = newAdmin,
                ScopeKind = "user",
                UserId = "admin-new",
                Destination = "admin-default.example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = userApproveAllAgents.Revision,
            }, ctx);

            Assert.False(adminRule.Rule.Paused);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.49", "93.184.216.34", 443, "admin-default.example.com")).Allowed);

            // The role-sync path owns persistence; a decision must not write an implicit read default.
            using (var reopened = new NetworkAccessPolicyStore(Path.Combine(tempDir, "policy", "state.json")))
            {
                Assert.True(reopened.HasBothPrivileges("admin-new"));
                Assert.False(reopened.ListRules(new NetworkAccessActor("admin-new", IsAdmin: true)).Rules.Single(r => r.Rule.Destination == "admin-default.example.com").Paused);
            }

            // Negative control (.NC): demoting admin-new clears their ON flags, pauses user-scope rules, and denies agent-2.
            NetworkAccessUserPrivilegesMessage demoted = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "admin-new",
                CanApproveOwn = false,
                CanAllowAllAgents = false,
                TargetIsAdmin = false,
            }, ctx);
            Assert.False(demoted.CanApproveOwn);
            Assert.False(demoted.CanAllowAllAgents);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.49", "93.184.216.34", 443, "admin-default.example.com")).Allowed);
            NetworkAccessListRulesResponse afterDemoteRules = await svc.ListRules(new NetworkAccessListRulesRequest { Actor = admin }, ctx);
            Assert.True(afterDemoteRules.Rules.Single(r => r.Destination == "admin-default.example.com").Paused);

            // Also verify an admin can read its default without creating an implicit persisted record.
            NetworkAccessUserPrivilegesMessage adminDefaultPrivs = await svc.GetUserPrivileges(new NetworkAccessGetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "legacy-admin-read-only",
                TargetIsAdmin = true,
            }, ctx);
            Assert.True(adminDefaultPrivs.CanApproveOwn);
            Assert.True(adminDefaultPrivs.CanAllowAllAgents);
            using (var reopened = new NetworkAccessPolicyStore(Path.Combine(tempDir, "policy", "state.json")))
            {
                Assert.False(reopened.HasBothPrivileges("legacy-admin-read-only"));
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C19_RevokingUserPrivileges_PausesExistingUserScopeRules_AndRegrantRestores_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.51", "user-a", "a1");
            RegisterAgent(registry, "10.247.0.52", "user-a", "a2");
            registry.Register(new RegistryEntry(
                "10.247.0.53",
                "user-a",
                "a1",
                "c-iso",
                "connector",
                "conn-1",
                "isolated",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            // Grant User A both flags
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            // Create user-scope rule R (example.com:443), agent-scope rule Ra (agent.example.com:443 for a1), and admin any-public rule Rc
            NetworkAccessSetRuleResponse ruleR = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = userA,
                ScopeKind = "user",
                UserId = "user-a",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = 0,
            }, ctx);

            NetworkAccessSetRuleResponse ruleRa = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = userA,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a1",
                Destination = "agent.example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = ruleR.Revision,
            }, ctx);

            NetworkAccessSetRuleResponse ruleRc = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-1",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExecContext = "isolated",
                ExpectedRevision = ruleRa.Revision,
            }, ctx);

            // Verify a2 reaches example.com:443 via R before privilege revocation
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.52", "93.184.216.34", 443, "example.com")).Allowed);

            // Admin unticks canAllowAllAgents for User A
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = false,
            }, ctx);

            // 1. Immediately, a2 is denied on example.com:443 (no_rule) and records a pending request,
            // while a1's agent-scope rule Ra and connector c-iso's admin any-public rule Rc remain active!
            NetworkAccessDecisionResult a2AfterRevoke = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.52", "93.184.216.34", 443, "example.com"));
            Assert.False(a2AfterRevoke.Allowed);
            Assert.Equal("no_rule", a2AfterRevoke.Reason);
            Assert.NotNull(a2AfterRevoke.RequestId);

            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.51", "93.184.216.34", 443, "agent.example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.53", "93.184.216.34", 443, null)).Allowed);

            // 2. ListRules returns R with Paused = true, while Ra and Rc have Paused = false
            NetworkAccessListRulesResponse rulesUserA = await svc.ListRules(new NetworkAccessListRulesRequest
            {
                Actor = userA,
            }, ctx);
            Assert.Contains(rulesUserA.Rules, r => r.Id == ruleR.Rule.Id && r.Paused);
            Assert.Contains(rulesUserA.Rules, r => r.Id == ruleRa.Rule.Id && !r.Paused);
            Assert.Contains(rulesUserA.Rules, r => r.Id == ruleRc.Rule.Id && !r.Paused);

            // 3. User A calling SetRule(scopeKind = user) is rejected with privilege_required
            RpcException exWritePaused = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = userA,
                    ScopeKind = "user",
                    UserId = "user-a",
                    Destination = "another.example.com",
                    DestKind = "host",
                    Port = 443,
                    Allowed = true,
                    ExpectedRevision = ruleRc.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exWritePaused.StatusCode);
            Assert.Contains("privilege_required", exWritePaused.Status.Detail, StringComparison.Ordinal);

            // 4. Admin can filter rules by FilterUserId = "user-a" and see R (Paused = true)
            NetworkAccessListRulesResponse adminFiltered = await svc.ListRules(new NetworkAccessListRulesRequest
            {
                Actor = admin,
                FilterUserId = "user-a",
            }, ctx);
            Assert.All(adminFiltered.Rules, r => Assert.Equal("user-a", r.UserId));
            Assert.Contains(adminFiltered.Rules, r => r.Id == ruleR.Rule.Id && r.Paused);

            // Also verify unticking canApproveOwn (with canAllowAllAgents = true) keeps R paused
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = false,
                CanAllowAllAgents = true,
            }, ctx);
            Assert.False(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.52", "93.184.216.34", 443, "example.com")).Allowed);

            // C19.NC: Admin re-ticks both flags for User A without recreating R; R becomes Paused = false and a2 succeeds again!
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            NetworkAccessDecisionResult a2Restored = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.52", "93.184.216.34", 443, "example.com"));
            Assert.True(a2Restored.Allowed);

            NetworkAccessListRulesResponse rulesRestored = await svc.ListRules(new NetworkAccessListRulesRequest
            {
                Actor = userA,
            }, ctx);
            Assert.Contains(rulesRestored.Rules, r => r.Id == ruleR.Rule.Id && !r.Paused);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C20a_OpenInternetConnectorRuleAndNonPublicV4_RejectsEveryPrivateRange_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.61", "user-a", "a1");
            registry.Register(new RegistryEntry(
                "10.247.0.62",
                "user-a",
                "a1",
                "c-iso",
                "connector",
                "conn-open",
                "isolated",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };

            NetworkAccessSetRuleResponse grantOpen = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-open",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExecContext = "isolated",
                ExpectedRevision = 0,
            }, ctx);
            Assert.True(grantOpen.Rule.OpenInternetBadge);

            // 1. Public IP and TLS SNI from the isolated connector succeed with any_public and OpenInternetBadge = true
            NetworkAccessDecisionResult pubIp = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.62", "93.184.216.34", 443, null));
            Assert.True(pubIp.Allowed);
            Assert.Equal("any_public", pubIp.Reason);
            Assert.True(pubIp.OpenInternetBadge);

            NetworkAccessDecisionResult pubSni = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.62", "93.184.216.34", 443, "any.public.example.com"));
            Assert.True(pubSni.Allowed);
            Assert.Equal("any_public", pubSni.Reason);
            Assert.True(pubSni.OpenInternetBadge);

            // 2. Every NonPublicV4 representative address is denied by the controller (not_public / sinkhole_plain)
            // This loop checks the controller decision layer. Rendered Envoy and nft artifacts are asserted separately in U8.
            string[] nonPublicSamples =
            [
                "10.0.0.1",
                "10.247.0.2",
                "172.16.0.1",
                "192.168.1.1",
                "100.64.0.1",
                "127.0.0.1",
                "169.254.169.254",
                "168.63.129.16",
                "198.18.250.250",
                "224.0.0.1",
            ];

            foreach (string ip in nonPublicSamples)
            {
                NetworkAccessDecisionResult res = controller.EvaluateContainerConnection(
                    new ContainerConnectionInput("10.247.0.62", ip, 443, null));
                Assert.False(res.Allowed);
                Assert.True(res.Reason is "not_public" or "sinkhole_plain", $"Expected not_public or sinkhole_plain for {ip}, got {res.Reason}");
            }

            // 3. Agent sandbox (user-a, a1) is NOT covered by the connector's any-public rule
            NetworkAccessDecisionResult sandboxTry = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.61", "93.184.216.34", 443, null));
            Assert.False(sandboxTry.Allowed);
            Assert.Equal("no_rule", sandboxTry.Reason);

            // C20.NC: Deleting the any-public rule causes the isolated connector's public IP connection to be denied
            _ = await svc.DeleteRule(new NetworkAccessDeleteRuleRequest
            {
                Actor = admin,
                RuleId = grantOpen.Rule.Id,
                ExpectedRevision = grantOpen.Revision,
            }, ctx);

            NetworkAccessDecisionResult afterRevoke = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.0.62", "93.184.216.34", 443, null));
            Assert.False(afterRevoke.Allowed);
            Assert.Equal("no_rule", afterRevoke.Reason);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C21_EmergencyBrake_UserAndSystemBrake_PreservesRulesAndRevision_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            RegisterAgent(registry, "10.247.0.71", "user-a", "a1");
            RegisterAgent(registry, "10.247.0.72", "user-a", "a2");
            registry.Register(new RegistryEntry(
                "10.247.0.73",
                "user-a",
                "a1",
                "c-a",
                "connector",
                "conn-a",
                "isolated",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));
            RegisterAgent(registry, "10.247.0.74", "user-b", "b1");

            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            // Grant User A and User B working rules
            NetworkAccessSetRuleResponse r1 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a1",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = 0,
            }, ctx);
            NetworkAccessSetRuleResponse r2 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "agent",
                UserId = "user-a",
                AgentId = "a2",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = r1.Revision,
            }, ctx);
            NetworkAccessSetRuleResponse r3 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-a",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExecContext = "isolated",
                ExpectedRevision = r2.Revision,
            }, ctx);
            NetworkAccessSetRuleResponse r4 = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "agent",
                UserId = "user-b",
                AgentId = "b1",
                Destination = "example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = r3.Revision,
            }, ctx);

            NetworkAccessListRulesResponse rulesBefore = await svc.ListRules(new NetworkAccessListRulesRequest { Actor = admin }, ctx);
            long revisionBefore = rulesBefore.Revision;
            int requestsBeforeCount = (await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx)).Requests.Count;

            // (a) User A engages own brake
            NetworkAccessBrakeMessage brakeAOn = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = userA,
                Scope = "user",
                UserId = "user-a",
                On = true,
            }, ctx);
            Assert.Contains(brakeAOn.Users, u => u.UserId == "user-a" && !u.LockedByAdmin);

            // All of User A's workloads (a1, a2, conn-a) are immediately denied with brake_user and no requests recorded
            NetworkAccessDecisionResult dA1 = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.71", "93.184.216.34", 443, "example.com"));
            NetworkAccessDecisionResult dA2 = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.72", "93.184.216.34", 443, "example.com"));
            NetworkAccessDecisionResult dCA = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.73", "93.184.216.34", 443, null));
            Assert.False(dA1.Allowed);
            Assert.Equal("brake_user", dA1.Reason);
            Assert.False(dA2.Allowed);
            Assert.Equal("brake_user", dA2.Reason);
            Assert.False(dCA.Allowed);
            Assert.Equal("brake_user", dCA.Reason);

            int requestsDuringBrake = (await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx)).Requests.Count;
            Assert.Equal(requestsBeforeCount, requestsDuringBrake);

            // C21.NC: User B's agent b1 and system gateway CheckOutbound continue to succeed while User A is braked
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.74", "93.184.216.34", 443, "example.com")).Allowed);
            NetworkAccessCheckOutboundResponse gwOut = await svc.CheckOutbound(new NetworkAccessCheckOutboundRequest
            {
                UserId = "system",
                AgentId = "gateway",
                Host = "api.openai.com",
                Port = 443,
            }, ctx);
            Assert.True(gwOut.Allowed);

            // User A cannot engage User B's brake or system brake
            await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
                {
                    Actor = userA,
                    Scope = "user",
                    UserId = "user-b",
                    On = true,
                }, ctx);
            });
            await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
                {
                    Actor = userA,
                    Scope = "system",
                    On = true,
                }, ctx);
            });

            // (b) User A releases own brake -> a1, a2, conn-a immediately work again without touching rules
            _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = userA,
                Scope = "user",
                UserId = "user-a",
                On = false,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.71", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.72", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.73", "93.184.216.34", 443, null)).Allowed);

            // (c) Rule preservation: rule list, count, and Revision are unchanged across engage & release
            NetworkAccessListRulesResponse rulesAfter = await svc.ListRules(new NetworkAccessListRulesRequest { Actor = admin }, ctx);
            Assert.Equal(revisionBefore, rulesAfter.Revision);
            Assert.Equal(rulesBefore.Rules.Count, rulesAfter.Rules.Count);
            Assert.Equal(rulesBefore.Rules.Select(r => r.Id), rulesAfter.Rules.Select(r => r.Id));

            // (d) Admin lock: Admin sets User A's brake -> LockedByAdmin = true; User A cannot release (brake_locked); Admin releases
            NetworkAccessBrakeMessage adminLockA = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = admin,
                Scope = "user",
                UserId = "user-a",
                On = true,
            }, ctx);
            Assert.Contains(adminLockA.Users, u => u.UserId == "user-a" && u.LockedByAdmin);

            // v7-5 NC: Locked user attempts bypass by toggling On then Off -> both are rejected with brake_locked!
            RpcException exLockedOn = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
                {
                    Actor = userA,
                    Scope = "user",
                    UserId = "user-a",
                    On = true,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exLockedOn.StatusCode);
            Assert.Contains("brake_locked", exLockedOn.Status.Detail, StringComparison.Ordinal);

            RpcException exLockedOff = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
                {
                    Actor = userA,
                    Scope = "user",
                    UserId = "user-a",
                    On = false,
                }, ctx);
            });
            Assert.Equal(StatusCode.PermissionDenied, exLockedOff.StatusCode);
            Assert.Contains("brake_locked", exLockedOff.Status.Detail, StringComparison.Ordinal);

            // User A is still locked by admin
            NetworkAccessBrakeMessage checkStillLocked = await svc.GetBrake(new NetworkAccessGetBrakeRequest { Actor = admin }, ctx);
            Assert.Contains(checkStillLocked.Users, u => u.UserId == "user-a" && u.LockedByAdmin);

            _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = admin,
                Scope = "user",
                UserId = "user-a",
                On = false,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.71", "93.184.216.34", 443, "example.com")).Allowed);

            // (e) System brake: Admin sets system brake -> all users and system CheckOutbound are denied with brake_system,
            // and state persists across PolicyStore reload
            _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = admin,
                Scope = "system",
                On = true,
            }, ctx);

            Assert.Equal("brake_system", controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.71", "93.184.216.34", 443, "example.com")).Reason);
            Assert.Equal("brake_system", controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.74", "93.184.216.34", 443, "example.com")).Reason);
            NetworkAccessCheckOutboundResponse gwBraked = await svc.CheckOutbound(new NetworkAccessCheckOutboundRequest
            {
                UserId = "system",
                AgentId = "gateway",
                Host = "api.openai.com",
                Port = 443,
            }, ctx);
            Assert.False(gwBraked.Allowed);
            Assert.Equal("brake_system", gwBraked.Reason);

            // Reload policy store from disk -> system brake is still active
            var reloadedStore = new NetworkAccessPolicyStore(Path.Combine(tempDir, "policy", "state.json"));
            Assert.True(reloadedStore.GetBrake(new NetworkAccessActor("admin", true)).System);
            Assert.Equal(revisionBefore, reloadedStore.Revision);

            // Release system brake -> all traffic works again
            _ = await svc.SetBrake(new NetworkAccessSetBrakeRequest
            {
                Actor = admin,
                Scope = "system",
                On = false,
            }, ctx);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.71", "93.184.216.34", 443, "example.com")).Allowed);
            Assert.True(controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.0.74", "93.184.216.34", 443, "example.com")).Allowed);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }



    [Fact]
    public void C22_WorkloadRegistry_ContainerWithoutAgentOrProbeLabel_GetsDenyAll_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "policy", "state.json"));
            var registry = new WorkloadRegistry();
            var controller = new NacController(store, registry);

            // Container with user, no agentId, and no purpose="probe" (when not systemwide/system::) gets deny-all (v7-26)
            registry.Register(new RegistryEntry(
                "10.247.1.100",
                "user-x",
                null,
                "orphan-ctr",
                "connector",
                "conn-orphan",
                "isolated",
                Systemwide: false,
                Egress: "normal",
                Attribution: "user",
                CompatProfile: null,
                DateTimeOffset.UtcNow,
                Purpose: null));

            RegistryEntry? reg = registry.FindByIp("10.247.1.100");
            Assert.NotNull(reg);
            Assert.Equal("deny-all", reg.Egress);

            NetworkAccessDecisionResult decision = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.100", "93.184.216.34", 443, "example.com"));
            Assert.False(decision.Allowed);
            Assert.Equal("deny_all", decision.Reason);

            // v7-26.NC: Container WITH Purpose="probe" receives normal egress and is not forced to deny-all
            registry.Register(new RegistryEntry(
                "10.247.1.101",
                "user-x",
                null,
                "probe-ctr",
                "connector",
                "conn-probe",
                "isolated",
                Systemwide: false,
                Egress: "normal",
                Attribution: "user",
                CompatProfile: null,
                DateTimeOffset.UtcNow,
                Purpose: "probe"));

            RegistryEntry? probeReg = registry.FindByIp("10.247.1.101");
            Assert.NotNull(probeReg);
            Assert.Equal("normal", probeReg.Egress);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }



    [Fact]
    public async Task C23_ConnectorProbes_UserProbeVsSystemwideProbe_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            ServerCallContext ctx = CreateCallContext();

            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userX = new NetworkAccessActorMessage { UserId = "user-x", IsAdmin = false };

            // 1. User's own connector probe container (user-x, conn-user, purpose="probe")
            registry.Register(new RegistryEntry(
                "10.247.1.110",
                "user-x",
                null,
                "user-probe-ctr",
                "connector",
                "conn-user",
                "isolated",
                Systemwide: false,
                Egress: "normal",
                Attribution: "user",
                CompatProfile: null,
                DateTimeOffset.UtcNow,
                Purpose: "probe"));

            // Probe connection attempt -> creates ephemeral in-memory pending request with Purpose="probe"
            NetworkAccessDecisionResult userProbeTry = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.110", "93.184.216.34", 443, "user-probe.example.com"));
            Assert.False(userProbeTry.Allowed);
            Assert.Equal("no_rule", userProbeTry.Reason);

            // Without canApproveOwn, the owner sees the probe as waiting for an administrator.
            NetworkAccessListRequestsResponse userList = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = userX }, ctx);
            NetworkAccessRequestMessage? userReq = userList.Requests.FirstOrDefault(r => r.Destination == "user-probe.example.com");
            Assert.NotNull(userReq);
            Assert.True(userReq.WaitingForAdmin);
            Assert.Equal("probe", userReq.Purpose);

            RpcException deniedWithoutPrivilege = await Assert.ThrowsAsync<RpcException>(() => svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userX,
                RequestId = userReq.Id,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = userList.Revision,
            }, ctx));
            Assert.Equal(StatusCode.PermissionDenied, deniedWithoutPrivilege.StatusCode);
            Assert.False(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.110", "93.184.216.34", 443, "user-probe.example.com")).Allowed);

            // Grant the owner permission, then refresh the request view and approve temporarily.
            _ = await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-x",
                CanApproveOwn = true,
                CanAllowAllAgents = false,
            }, ctx);
            userList = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = userX }, ctx);
            userReq = userList.Requests.FirstOrDefault(r => r.Destination == "user-probe.example.com");
            Assert.NotNull(userReq);
            Assert.False(userReq.WaitingForAdmin);

            // User-x approves probe temporarily: writes NO rule and does NOT increment persistent Revision!
            NetworkAccessDecideResponse decideUser = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userX,
                RequestId = userReq.Id,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = userList.Revision,
            }, ctx);
            Assert.True(decideUser.Rule is null || string.IsNullOrEmpty(decideUser.Rule.Id));
            NetworkAccessListRulesResponse rulesAfterProbeApprove = await svc.ListRules(new NetworkAccessListRulesRequest { Actor = admin }, ctx);
            Assert.DoesNotContain(rulesAfterProbeApprove.Rules, r => r.Destination == "user-probe.example.com");

            // While probe container is active: (probe container, user-x, conn-user, host, 443) is temporarily allowed!
            NetworkAccessDecisionResult probeAllowed = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.110", "93.184.216.34", 443, "user-probe.example.com"));
            Assert.True(probeAllowed.Allowed);
            Assert.Equal("probe_temporary_allow", probeAllowed.Reason);

            // Negative control 1: Different port or another probe container for same user+connector is NOT covered.
            Assert.False(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.110", "93.184.216.34", 8443, "user-probe.example.com")).Allowed);
            registry.Register(new RegistryEntry(
                "10.247.1.111", "user-x", null, "user-probe-ctr-other", "connector", "conn-user", "isolated",
                Systemwide: false, Egress: "normal", Attribution: "user", CompatProfile: null, DateTimeOffset.UtcNow, Purpose: "probe"));
            Assert.False(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.111", "93.184.216.34", 443, "user-probe.example.com")).Allowed);
            registry.Deregister("10.247.1.111", "user-probe-ctr-other");

            // Probe finishes -> deregister clears temporary probe grant and ephemeral request card.
            registry.Deregister("10.247.1.110", "user-probe-ctr");
            NetworkAccessListRequestsResponse afterProbeExitList = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = userX }, ctx);
            Assert.DoesNotContain(afterProbeExitList.Requests, r => r.Destination == "user-probe.example.com");

            // Negative control 2 (.NC): Runtime container starting afterwards for the SAME user+connector+destination is STILL DENIED with no rule written!
            registry.Register(new RegistryEntry(
                "10.247.1.112", "user-x", "a1", "runtime-ctr", "connector", "conn-user", "isolated",
                Systemwide: false, Egress: "normal", Attribution: "user-agent", CompatProfile: null, DateTimeOffset.UtcNow, Purpose: null));
            NetworkAccessDecisionResult runtimeTry = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.112", "93.184.216.34", 443, "user-probe.example.com"));
            Assert.False(runtimeTry.Allowed);
            Assert.Equal("no_rule", runtimeTry.Reason);
            Assert.NotNull(runtimeTry.RequestId);

            // Runtime approval writes the normal persistent user+agent rule!
            NetworkAccessDecideResponse decideRuntime = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userX,
                RequestId = runtimeTry.RequestId!,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = controller.PolicyStore.Revision,
            }, ctx);
            Assert.NotNull(decideRuntime.Rule);
            Assert.Equal("agent", decideRuntime.Rule.ScopeKind);
            Assert.True(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.112", "93.184.216.34", 443, "user-probe.example.com")).Allowed);

            // 2. Systemwide connector probe container (even if UserId is set on a user with canApproveOwn, systemwide probe ALWAYS goes to admin!)
            registry.Register(new RegistryEntry(
                "10.247.1.120",
                "user-x",
                null,
                "sys-probe-ctr",
                "connector",
                "conn-sys",
                "isolated",
                Systemwide: true,
                Egress: "normal",
                Attribution: "user",
                CompatProfile: null,
                DateTimeOffset.UtcNow,
                Purpose: "probe"));

            NetworkAccessDecisionResult sysProbeTry = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.120", "93.184.216.34", 443, "sys-probe.example.com"));
            Assert.False(sysProbeTry.Allowed);

            // User-x CANNOT see or approve the systemwide probe request even with canApproveOwn = true
            NetworkAccessListRequestsResponse userListAfterSys = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = userX }, ctx);
            Assert.DoesNotContain(userListAfterSys.Requests, r => r.Destination == "sys-probe.example.com");

            // Admin sees the systemwide probe request and temporarily allows it without creating a persistent rule
            NetworkAccessListRequestsResponse adminList = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            NetworkAccessRequestMessage? sysReq = adminList.Requests.FirstOrDefault(r => r.Destination == "sys-probe.example.com");
            Assert.NotNull(sysReq);

            // User-x attempting to decide systemwide probe request throws PermissionDenied
            await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = userX,
                    RequestId = sysReq.Id,
                    Allow = true,
                    Scope = "connector-system",
                    ExpectedRevision = adminList.Revision,
                }, ctx);
            });

            // Admin temporarily approves systemwide probe -> NO persistent rule written; works during probe; gone on exit!
            NetworkAccessDecideResponse decideSysProbe = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = sysReq.Id,
                Allow = true,
                Scope = "connector-system",
                ExpectedRevision = adminList.Revision,
            }, ctx);
            Assert.True(decideSysProbe.Rule is null || string.IsNullOrEmpty(decideSysProbe.Rule.Id));
            Assert.True(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.120", "93.184.216.34", 443, "sys-probe.example.com")).Allowed);
            Assert.DoesNotContain((await svc.ListRules(new NetworkAccessListRulesRequest { Actor = admin }, ctx)).Rules, r => r.Destination == "sys-probe.example.com");

            registry.Deregister("10.247.1.120", "sys-probe-ctr");
            registry.Register(new RegistryEntry(
                "10.247.1.121", "user-x", null, "sys-runtime-ctr", "connector", "conn-sys", "isolated",
                Systemwide: true, Egress: "normal", Attribution: "user", CompatProfile: null, DateTimeOffset.UtcNow, Purpose: null));
            Assert.False(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.121", "93.184.216.34", 443, "sys-probe.example.com")).Allowed);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task V7_7_SetRule_OverridesCallerSpoofedExecContextAndAttribution_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };

            // 1. Registered agent-sandbox connector: even if caller spoofs ExecContext="isolated", Attribution="user",
            //    Manager resolves authoritative registry ExecContext ("agent-sandbox") and rejects any-public.
            registry.Register(new RegistryEntry(
                "10.247.1.70",
                "user-a",
                "a1",
                "ctr-sandbox-spoof",
                "connector",
                "conn-sandbox-bound",
                "agent-sandbox",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            RpcException exSpoofSandbox = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = admin,
                    ScopeKind = "user",
                    UserId = "user-a",
                    ConnectorId = "conn-sandbox-bound",
                    Destination = "*",
                    DestKind = "any-public",
                    Port = 0,
                    Allowed = true,
                    ExecContext = "isolated",
                    Attribution = "user",
                    ExpectedRevision = controller.PolicyStore.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.InvalidArgument, exSpoofSandbox.StatusCode);
            Assert.Contains("scope_not_enforceable", exSpoofSandbox.Status.Detail, StringComparison.Ordinal);

            // 2. Connector with Attribution="none" recorded request history: caller spoofing Attribution="user" is rejected.
            registry.Register(new RegistryEntry(
                "10.247.1.71",
                null,
                null,
                "ctr-unattr",
                "connector",
                "conn-unattributed",
                "isolated",
                true,
                "normal",
                "none",
                null,
                DateTimeOffset.UtcNow));
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.71", "93.184.216.34", 443, "unattr.example.com"));
            registry.Deregister("10.247.1.71", "ctr-unattr");

            RpcException exSpoofUnattr = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.SetRule(new NetworkAccessSetRuleRequest
                {
                    Actor = admin,
                    ScopeKind = "user",
                    UserId = "user-a",
                    ConnectorId = "conn-unattributed",
                    Destination = "unattr.example.com",
                    DestKind = "host",
                    Port = 443,
                    Allowed = true,
                    ExecContext = "isolated",
                    Attribution = "user",
                    ExpectedRevision = controller.PolicyStore.Revision,
                }, ctx);
            });
            Assert.Equal(StatusCode.InvalidArgument, exSpoofUnattr.StatusCode);
            Assert.Contains("scope_not_enforceable", exSpoofUnattr.Status.Detail, StringComparison.Ordinal);

            // Negative control (.NC): genuine per-user isolated connector in registry succeeds even if caller omits ExecContext/Attribution.
            registry.Register(new RegistryEntry(
                "10.247.1.72",
                "user-a",
                "a1",
                "ctr-real-iso",
                "connector",
                "conn-real-isolated",
                "isolated",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            NetworkAccessSetRuleResponse allowedIso = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-real-isolated",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExpectedRevision = controller.PolicyStore.Revision,
            }, ctx);
            Assert.True(allowedIso.Rule.Allowed);
            Assert.True(allowedIso.Rule.OpenInternetBadge);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task V7_8_DecideRequest_DefaultAndForcedScopeResolution_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            RegisterAgent(registry, "10.247.1.80", "user-a", "a1");
            registry.Register(new RegistryEntry(
                "10.247.1.81", "user-a", null, "ctr-sys-user", "connector", "conn-sys-user", "isolated", true, "normal", "user", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry(
                "10.247.1.82", null, null, "ctr-sys-none", "connector", "conn-sys-none", "isolated", true, "normal", "none", null, DateTimeOffset.UtcNow));

            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.80", "93.184.216.34", 443, "ord-block.example.com"));
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.80", "93.184.216.34", 443, "ord-allow.example.com"));
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.81", "93.184.216.34", 443, "sys-user-deny.example.com"));
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.81", "93.184.216.34", 443, "sys-allow-empty.example.com"));
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.82", "93.184.216.34", 443, "sys-none-deny.example.com"));

            NetworkAccessListRequestsResponse list = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            string idOrdBlock = list.Requests.Single(r => r.Destination == "ord-block.example.com").Id;
            string idOrdAllow = list.Requests.Single(r => r.Destination == "ord-allow.example.com").Id;
            string idSysUserDeny = list.Requests.Single(r => r.Destination == "sys-user-deny.example.com").Id;
            string idSysAllowEmpty = list.Requests.Single(r => r.Destination == "sys-allow-empty.example.com").Id;
            string idSysNoneDeny = list.Requests.Single(r => r.Destination == "sys-none-deny.example.com").Id;
            long rev = list.Revision;

            // 1. Ordinary agent "Keep blocked" (Allow=false) ALWAYS forces scope="agent" even when caller passes Scope="user".
            NetworkAccessDecideResponse ordBlockRes = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = idOrdBlock,
                Allow = false,
                Scope = "user",
                ExpectedRevision = rev,
            }, ctx);
            Assert.Equal("agent", ordBlockRes.Rule.ScopeKind);
            rev = ordBlockRes.Revision;

            // 2. Ordinary agent Allow with empty Scope defaults to "agent".
            NetworkAccessDecideResponse ordAllowRes = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = idOrdAllow,
                Allow = true,
                Scope = string.Empty,
                ExpectedRevision = rev,
            }, ctx);
            Assert.Equal("agent", ordAllowRes.Rule.ScopeKind);
            rev = ordAllowRes.Revision;

            // 3. Systemwide user-attributed deny with empty Scope defaults to "user".
            NetworkAccessDecideResponse sysUserDenyRes = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = idSysUserDeny,
                Allow = false,
                Scope = string.Empty,
                ExpectedRevision = rev,
            }, ctx);
            Assert.Equal("user", sysUserDenyRes.Rule.ScopeKind);
            rev = sysUserDenyRes.Revision;

            // 4. Systemwide non-attributed deny with empty Scope defaults to "connector-system".
            NetworkAccessDecideResponse sysNoneDenyRes = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = idSysNoneDeny,
                Allow = false,
                Scope = string.Empty,
                ExpectedRevision = rev,
            }, ctx);
            Assert.Equal("connector-system", sysNoneDenyRes.Rule.ScopeKind);
            rev = sysNoneDenyRes.Revision;

            // 5. Negative control (.NC): Systemwide allow with empty Scope rejects (scope_required), whereas explicit Scope succeeds.
            RpcException exScopeReq = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await svc.DecideRequest(new NetworkAccessDecideRequest
                {
                    Actor = admin,
                    RequestId = idSysAllowEmpty,
                    Allow = true,
                    Scope = string.Empty,
                    ExpectedRevision = rev,
                }, ctx);
            });
            Assert.Equal(StatusCode.InvalidArgument, exScopeReq.StatusCode);
            Assert.Contains("scope_required", exScopeReq.Status.Detail, StringComparison.Ordinal);

            NetworkAccessDecideResponse sysAllowExplicit = await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = admin,
                RequestId = idSysAllowEmpty,
                Allow = true,
                Scope = "connector-system",
                ExpectedRevision = rev,
            }, ctx);
            Assert.Equal("connector-system", sysAllowExplicit.Rule.ScopeKind);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task V7_9_ClearingCards_ResolvesOnlyRequestsEvaluatingToAllow_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            WorkspaceGrpcService svc = CreateGrpcService(tempDir, out NacController controller, out IWorkloadRegistry registry);
            ServerCallContext ctx = CreateCallContext();
            var admin = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true };
            var userA = new NetworkAccessActorMessage { UserId = "user-a", IsAdmin = false };

            await svc.SetUserPrivileges(new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = admin,
                UserId = "user-a",
                CanApproveOwn = true,
                CanAllowAllAgents = true,
            }, ctx);

            RegisterAgent(registry, "10.247.1.90", "user-a", "a1");
            RegisterAgent(registry, "10.247.1.91", "user-a", "a2");
            registry.Register(new RegistryEntry(
                "10.247.1.92", "user-a", "a1", "ctr-iso-a", "connector", "conn-a", "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

            // Create pending requests across agents, ports, destinations, and LAN vs public.
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.90", "93.184.216.34", 443, "shared.example.com")); // a1 shared:443
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.91", "93.184.216.34", 443, "shared.example.com")); // a2 shared:443
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.91", "93.184.216.34", 8443, "shared.example.com")); // a2 shared:8443
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.90", "93.184.216.34", 443, "deny-test.example.com")); // a1 deny-test
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.91", "93.184.216.34", 443, "deny-test.example.com")); // a2 deny-test
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.92", "93.184.216.34", 443, "public-conn.example.com")); // conn-a public host
            _ = controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.92", "192.168.1.50", 443, null)); // conn-a RFC1918 IP

            NetworkAccessListRequestsResponse list0 = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            long rev = list0.Revision;

            // 1. Agent-scope block of a1's deny-test does NOT touch a2's deny-test (stays pending).
            string a1DenyId = list0.Requests.Single(r => r.AgentId == "a1" && r.Destination == "deny-test.example.com").Id;
            rev = (await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = a1DenyId,
                Allow = false,
                Scope = "agent",
                ExpectedRevision = rev,
            }, ctx)).Revision;

            NetworkAccessListRequestsResponse afterDeny = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            Assert.Equal("pending", afterDeny.Requests.Single(r => r.AgentId == "a2" && r.Destination == "deny-test.example.com").Status);

            // 2. Agent-scope allow of a1's shared:443 keeps a2's shared:443 and shared:8443 pending.
            string a1SharedId = afterDeny.Requests.Single(r => r.AgentId == "a1" && r.Destination == "shared.example.com").Id;
            rev = (await svc.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = userA,
                RequestId = a1SharedId,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = rev,
            }, ctx)).Revision;

            NetworkAccessListRequestsResponse afterAgentAllow = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            Assert.Equal("pending", afterAgentAllow.Requests.Single(r => r.AgentId == "a2" && r.Destination == "shared.example.com" && r.Port == 443).Status);
            Assert.Equal("pending", afterAgentAllow.Requests.Single(r => r.AgentId == "a2" && r.Destination == "shared.example.com" && r.Port == 8443).Status);

            // 3. Negative control (.NC): user-scope allow of shared:443 resolves a2's matching shared:443 card,
            //    while keeping shared:8443 pending; and any-public on conn-a resolves public card while keeping RFC1918 pending!
            rev = (await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = userA,
                ScopeKind = "user",
                UserId = "user-a",
                Destination = "shared.example.com",
                DestKind = "host",
                Port = 443,
                Allowed = true,
                ExpectedRevision = rev,
            }, ctx)).Revision;

            NetworkAccessListRequestsResponse afterUserAllow = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            Assert.Equal("allowed", afterUserAllow.Requests.Single(r => r.AgentId == "a2" && r.Destination == "shared.example.com" && r.Port == 443).Status);
            Assert.Equal("pending", afterUserAllow.Requests.Single(r => r.AgentId == "a2" && r.Destination == "shared.example.com" && r.Port == 8443).Status);

            _ = await svc.SetRule(new NetworkAccessSetRuleRequest
            {
                Actor = admin,
                ScopeKind = "user",
                UserId = "user-a",
                ConnectorId = "conn-a",
                Destination = "*",
                DestKind = "any-public",
                Port = 0,
                Allowed = true,
                ExpectedRevision = rev,
            }, ctx);

            NetworkAccessListRequestsResponse afterOpenInternet = await svc.ListRequests(new NetworkAccessListRequestsRequest { Actor = admin }, ctx);
            Assert.Equal("allowed", afterOpenInternet.Requests.Single(r => r.ConnectorId == "conn-a" && r.Destination == "public-conn.example.com").Status);
            Assert.Equal("pending", afterOpenInternet.Requests.Single(r => r.ConnectorId == "conn-a" && r.Destination == "192.168.1.50").Status);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static void RegisterAgent(IWorkloadRegistry registry, string ip, string userId, string agentId)
    {
        registry.Register(new RegistryEntry(
            ip,
            userId,
            agentId,
            $"ctr-{userId}-{agentId}",
            "agent-sandbox",
            null,
            "agent-sandbox",
            false,
            "normal",
            "user-agent",
            null,
            DateTimeOffset.UtcNow));
    }

    private static WorkspaceGrpcService CreateGrpcService(
        string stateDir,
        out NacController controller,
        out IWorkloadRegistry registry)
    {
        var store = new NetworkAccessPolicyStore(Path.Combine(stateDir, "policy", "state.json"));
        registry = new WorkloadRegistry();
        var audit = new NetworkAccessAuditRing(Path.Combine(stateDir, "audit", "decisions.jsonl"));
        var compat = new ImageCompatibilityStore(Path.Combine(stateDir, "compat", "image-compatibility.json"));
        controller = new NacController(store, registry, audit);

        return new WorkspaceGrpcService(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance,
            controller,
            compat);
    }

    private static ServerCallContext CreateCallContext()
        => new TestServerCallContext();

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nac-c-ctrl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata requestHeaders = [];
        private readonly Metadata responseTrailers = [];
        private readonly Dictionary<object, object> userState = [];

        protected override string MethodCore => "/kautuka.manager.v1.ManagerService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "unix:test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override IDictionary<object, object> UserStateCore => userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }
}
