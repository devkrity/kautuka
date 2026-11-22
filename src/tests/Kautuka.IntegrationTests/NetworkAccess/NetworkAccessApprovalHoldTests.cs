using Grpc.Core;

using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccess.Client;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessApprovalHoldTests
{
    [Theory]
    [InlineData(600, "602s", "603s")]
    [InlineData(45, "47s", "300s")]
    public void EnvoyKeepsTheConnectionOpenThroughTheConfiguredHold(int holdSeconds, string authorization, string idle)
    {
        string config = BoundaryRulesetRenderer.RenderEnvoyConfig(includeGatewayConnect: true,
            approvalHold: TimeSpan.FromSeconds(holdSeconds));
        Assert.Contains($"timeout: {authorization}", config, StringComparison.Ordinal);
        Assert.Contains($"stream_idle_timeout: {idle}", config, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayCheckerUsesTheLongWaitOnlyAfterManagerAcknowledgesIt(bool acknowledge)
    {
        var client = new ApprovalCheckClient();
        var checker = new GrpcNetworkAccessChecker(() => client, approvalHold: TimeSpan.FromMinutes(10));
        CancellationToken token = TestContext.Current.CancellationToken;
        Task<NetworkAccessCheckResult> check = checker.CheckAsync(new OutboundTarget("approval.example.test", 443,
            Purpose: "remote-mcp", UserId: "user-a", AgentId: "agent-a"), token);
        if (acknowledge)
        {
            client.Headers.SetResult(new Metadata { { "kautuka-approval-pending", "1" } });
            Assert.False(check.IsCompleted);
            client.Response.SetResult(new NetworkAccessCheckOutboundResponse { Allowed = true, Reason = "rule_agent" });
        }

        NetworkAccessCheckResult result = await check.WaitAsync(TimeSpan.FromSeconds(12), token);
        Assert.Equal(acknowledge, result.Allowed);
        if (!acknowledge) Assert.Equal("controller_unavailable", result.Reason);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task GatewayPreflightReturnsPendingRequestWithoutEnteringApprovalHold()
    {
        var client = new ApprovalCheckClient();
        client.Response.SetResult(new NetworkAccessCheckOutboundResponse { Reason = "no_rule", RequestId = "approval-1" });
        var checker = new GrpcNetworkAccessChecker(() => client, approvalHold: TimeSpan.FromMinutes(10));

        NetworkAccessCheckResult result = await checker.CheckAsync(new OutboundTarget("approval.example.test", 443,
            Purpose: "connector-oauth", UserId: "user-a", ConnectorId: "mail", SkipApprovalHold: true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("approval-1", result.RequestId);
        Assert.True(client.Request!.SkipApprovalHold);
        Assert.Equal("connector-oauth", client.Request.Purpose);
        Assert.True(client.Disposed);
    }

    private sealed class ApprovalCheckClient : ManagerService.ManagerServiceClient
    {
        public TaskCompletionSource<Metadata> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NetworkAccessCheckOutboundResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NetworkAccessCheckOutboundRequest? Request { get; private set; }
        public bool Disposed { get; private set; }

        public override AsyncUnaryCall<NetworkAccessCheckOutboundResponse> CheckOutboundAsync(
            NetworkAccessCheckOutboundRequest request, CallOptions options)
        {
            Request = request;
            return new(Response.Task, Headers.Task, () => Status.DefaultSuccess, () => new Metadata(), () => Disposed = true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayApprovalRechecksTheTopologyEvenWithAnEarlyDecision(bool decideBeforeWaiting)
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-gate-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            bool gateOpen = true;
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry(),
                isContainerAuthorizationGateOpen: () => gateOpen, approvalHold: TimeSpan.FromSeconds(5));
            var input = new OutboundCheckInput("user-a", "agent-a", null, "approval.example.test", 443, "remote-mcp", null);
            NetworkAccessDecisionResult initial = controller.EvaluateGatewayConnect(input, publicOnly: true);
            string requestId = Assert.IsType<string>(initial.RequestId);
            void CloseAndApprove()
            {
                gateOpen = false;
                policy.DecideRequest(new NetworkAccessActor("admin", IsAdmin: true), requestId, true, "agent", policy.Revision);
            }
            if (decideBeforeWaiting) CloseAndApprove();
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial, input.UserId,
                () => controller.EvaluateGatewayConnect(input, publicOnly: true, recordPendingRequest: false), TestContext.Current.CancellationToken);
            if (!decideBeforeWaiting)
            {
                Assert.False(held.IsCompleted);
                CloseAndApprove();
            }
            NetworkAccessDecisionResult result = await held;
            Assert.False(result.Allowed);
            Assert.Equal("topology_closed", result.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ApprovalDoesNotTransferAnOpenCallToAReplacementWorkload()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-workload-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var registry = new WorkloadRegistry();
            const string ip = "10.247.0.40";
            const string host = "approval.example.test";
            registry.Register(new RegistryEntry(ip, "user-a", "agent-a", new string('a', 64), "agent-sandbox", null,
                "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, registry,
                approvalHold: TimeSpan.FromSeconds(10));
            NetworkAccessDecisionResult initial = controller.EvaluateContainerConnection(ip, NetworkAddressClassifier.SinkholeAddress, 443, true, host);
            RegistryEntry original = Assert.IsType<RegistryEntry>(initial.PendingSource);
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial, "user-a",
                () => controller.EvaluateContainerConnection(ip, NetworkAddressClassifier.SinkholeAddress, 443, true, host,
                    expectedSource: original, recordPendingRequest: false), TestContext.Current.CancellationToken);

            Assert.False(held.IsCompleted);
            Assert.True(registry.Deregister(ip, original.ContainerId));
            registry.Register(original with { ContainerId = new string('b', 64) });
            policy.DecideRequest(new NetworkAccessActor("admin", IsAdmin: true), Assert.IsType<string>(initial.RequestId), true, "agent", policy.Revision);
            NetworkAccessDecisionResult result = await held;
            Assert.False(result.Allowed);
            Assert.Equal("stale_workload", result.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableDecisionRechecksTheOriginalCallOnlyWhenApproved(bool allow)
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-hold-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry(),
                approvalHold: TimeSpan.FromSeconds(10));
            var actor = new NetworkAccessActor("admin", IsAdmin: true);
            var outbound = new OutboundCheckInput("user-a", "agent-a", null, "approval.example.test", 443, "agent-run", null);
            NetworkAccessDecisionResult initial = controller.EvaluateOutboundCheck(outbound);
            string requestId = Assert.IsType<string>(initial.RequestId);
            Assert.Equal("no_rule", initial.Reason);
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial, outbound.UserId,
                () => controller.EvaluateOutboundCheck(outbound, recordPendingRequest: false), TestContext.Current.CancellationToken);

            Assert.False(held.IsCompleted);
            Assert.Single(policy.ListRequests(actor).Requests);
            policy.DecideRequest(actor, requestId, allow, "agent", policy.Revision);
            NetworkAccessDecisionResult result = await held;
            Assert.Equal(allow, result.Allowed);
            if (!allow) Assert.Equal("blocked", result.Reason);
            Assert.Equal(allow ? 2 : 1, controller.Checks);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpiredWaitLeavesTheRequestAvailableWithoutOpeningTheCall()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-timeout-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry(),
                approvalHold: TimeSpan.FromMilliseconds(25));
            var input = new OutboundCheckInput("user-a", "agent-a", null, "approval.example.test", 443, "agent-run", null);
            NetworkAccessDecisionResult initial = controller.EvaluateOutboundCheck(input);
            NetworkAccessDecisionResult expired = await controller.RecheckAfterApprovalAsync(initial, input.UserId,
                () => throw new InvalidOperationException("An expired wait cannot authorize traffic."), TestContext.Current.CancellationToken);
            Assert.Equal("no_rule", expired.Reason);
            Assert.Equal(initial.RequestId, expired.RequestId);
            Assert.Single(policy.ListRequests(new NetworkAccessActor("admin", IsAdmin: true)).Requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredApprovalLimitsReserveCapacityAcrossUsers()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-capacity-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry(),
                approvalHold: TimeSpan.FromSeconds(10), maxHeldCalls: 2, maxHeldCallsPerUser: 1);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            NetworkAccessDecisionResult Pending(string user, string host) => controller.EvaluateOutboundCheck(
                new OutboundCheckInput(user, "agent-a", null, host, 443, "agent-run", null));
            NetworkAccessDecisionResult Recheck() => throw new InvalidOperationException("No approval was issued.");
            var first = Pending("user-a", "first.example.test");
            var second = Pending("user-a", "second.example.test");
            var otherUser = Pending("user-b", "first.example.test");
            var thirdUser = Pending("user-c", "first.example.test");
            Task<NetworkAccessDecisionResult> heldFirst = controller.RecheckAfterApprovalAsync(first, "user-a", Recheck, cancellation.Token);
            Assert.False(heldFirst.IsCompleted);
            Assert.Equal(first, await controller.RecheckAfterApprovalAsync(first, "user-a", Recheck, cancellation.Token));
            Assert.Equal(second, await controller.RecheckAfterApprovalAsync(second, "user-a", Recheck, cancellation.Token));
            Task<NetworkAccessDecisionResult> heldOther = controller.RecheckAfterApprovalAsync(otherUser, "user-b", Recheck, cancellation.Token);
            Assert.False(heldOther.IsCompleted);
            Assert.Equal(thirdUser, await controller.RecheckAfterApprovalAsync(thirdUser, "user-c", Recheck, cancellation.Token));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(heldFirst, heldOther));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
