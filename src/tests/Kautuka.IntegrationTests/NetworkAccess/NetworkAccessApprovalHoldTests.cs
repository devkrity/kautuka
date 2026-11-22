using Grpc.Core;

using Kautuka.Manager.Protos;
using Kautuka.NetworkAccess.Client;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkAccessApprovalHoldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayCheckerUsesTheLongWaitOnlyAfterManagerAcknowledgesIt(bool acknowledge)
    {
        var client = new ApprovalCheckClient();
        var checker = new GrpcNetworkAccessChecker(() => client, waitForApproval: true);
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

    private sealed class ApprovalCheckClient : ManagerService.ManagerServiceClient
    {
        public TaskCompletionSource<Metadata> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<NetworkAccessCheckOutboundResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override AsyncUnaryCall<NetworkAccessCheckOutboundResponse> CheckOutboundAsync(
            NetworkAccessCheckOutboundRequest request, CallOptions options)
            => new(Response.Task, Headers.Task, () => Status.DefaultSuccess, () => new Metadata(), () => Disposed = true);
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
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial,
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
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial,
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
            Task<NetworkAccessDecisionResult> held = controller.RecheckAfterApprovalAsync(initial,
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
            NetworkAccessDecisionResult expired = await controller.RecheckAfterApprovalAsync(initial,
                () => throw new InvalidOperationException("An expired wait cannot authorize traffic."), TestContext.Current.CancellationToken);
            Assert.Equal("no_rule", expired.Reason);
            Assert.Equal(initial.RequestId, expired.RequestId);
            Assert.Single(policy.ListRequests(new NetworkAccessActor("admin", IsAdmin: true)).Requests);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WaitsReserveFourSlotsAndDuplicateCallsRetainTheOrdinaryDenial()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-approval-capacity-").FullName;
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"), []);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry(),
                approvalHold: TimeSpan.FromSeconds(10));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            NetworkAccessDecisionResult[] pending = Enumerable.Range(1, 5).Select(index => controller.EvaluateOutboundCheck(
                new OutboundCheckInput("user-a", "agent-a", null, $"host{index}.example.test", 443, "agent-run", null))).ToArray();
            NetworkAccessDecisionResult Recheck() => throw new InvalidOperationException("No approval was issued.");
            Task<NetworkAccessDecisionResult>[] held = pending.Take(4).Select(decision =>
                controller.RecheckAfterApprovalAsync(decision, Recheck, cancellation.Token)).ToArray();

            Assert.All(held, task => Assert.False(task.IsCompleted));
            Assert.Equal(pending[0], await controller.RecheckAfterApprovalAsync(pending[0], Recheck, cancellation.Token));
            Assert.Equal(pending[4], await controller.RecheckAfterApprovalAsync(pending[4], Recheck, cancellation.Token));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(held));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
