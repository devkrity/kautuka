using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ThreadCommandConcurrencyTests
{
    [Fact]
    public async Task ConcurrentResolveSameApprovalOnlyOneAcceptedOthersAlreadyApplied()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        Task<ThreadCommandResult>[] tasks =
        [
            service.ResolveInterruptAsync(Command(requestId: request.Id), TestContext.Current.CancellationToken).AsTask(),
            service.ResolveInterruptAsync(Command(requestId: request.Id), TestContext.Current.CancellationToken).AsTask(),
            service.ResolveInterruptAsync(Command(requestId: request.Id), TestContext.Current.CancellationToken).AsTask(),
            service.ResolveInterruptAsync(Command(requestId: request.Id), TestContext.Current.CancellationToken).AsTask(),
        ];

        ThreadCommandResult[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.Single(results, result => result.Status == ThreadCommandStatus.Accepted);
        Assert.Equal(3, results.Count(result => result.Status == ThreadCommandStatus.AlreadyApplied));
        Assert.Equal("approved", approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task ConcurrentResolveAllPartialFailureDoesNotRegisterBypass()
    {
        GatewayToolApprovalRequest first = CreateRequest("request-1");
        GatewayToolApprovalRequest expired = CreateRequest("request-2") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        };
        var approvalStore = new ThreadCommandApprovalStore(first, expired);
        ThreadCommandService service = CreateService(approvalStore);

        Task<ThreadCommandResult>[] tasks =
        [
            service.ResolveAllInterruptsAsync(
                ResolveAllCommand(trustThisRun: true),
                TestContext.Current.CancellationToken).AsTask(),
            service.ResolveAllInterruptsAsync(
                ResolveAllCommand(trustThisRun: true),
                TestContext.Current.CancellationToken).AsTask(),
        ];

        ThreadCommandResult[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.All(results, result => Assert.True(
            result.Status is ThreadCommandStatus.Expired or ThreadCommandStatus.Accepted or ThreadCommandStatus.AlreadyApplied,
            result.Status.ToString()));
        Assert.Equal(1, results.Max(static result => result.ResolvedCount));
    }

    [Fact]
    public async Task ConcurrentStopActiveRootIsIdempotentWithoutDoubleSettle()
    {
        GatewayToolApprovalRequest first = CreateRequest("request-1");
        GatewayToolApprovalRequest second = CreateRequest("request-2");
        var approvalStore = new ThreadCommandApprovalStore(first, second);
        ThreadCommandService service = CreateStopCapableService(approvalStore, out _, out _);

        Task<ThreadCommandResult>[] tasks =
        [
            service.StopActiveRootAsync(StopCommand(), TestContext.Current.CancellationToken).AsTask(),
            service.StopActiveRootAsync(StopCommand(), TestContext.Current.CancellationToken).AsTask(),
            service.StopActiveRootAsync(StopCommand(), TestContext.Current.CancellationToken).AsTask(),
        ];

        ThreadCommandResult[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.Equal(1, results.Count(result => result.Status == ThreadCommandStatus.Accepted));
        Assert.Equal(2, results.Count(result => result.Status == ThreadCommandStatus.AlreadyApplied));
        Assert.Equal("stopped", approvalStore.GetById("request-1")?.Decision);
        Assert.Equal("stopped", approvalStore.GetById("request-2")?.Decision);
    }

    [Fact]
    public async Task ConcurrentStaleRevisionRequestsAllConflictWithoutMutation()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        var agentStore = new ThreadCommandAgentStore(RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }, rootRuntimeSequence: 7));
        ThreadCommandService service = CreateService(approvalStore, agentStore: agentStore);

        Task<ThreadCommandResult>[] tasks = Enumerable.Range(0, 8)
            .Select(_ => service.ResolveInterruptAsync(
                Command(requestId: request.Id, expectedRevision: 3),
                TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        ThreadCommandResult[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.All(results, result => Assert.Equal(ThreadCommandStatus.Conflict, result.Status));
        Assert.Null(approvalStore.GetById(request.Id)?.Decision);
    }

    [Fact]
    public async Task ConcurrentLateApprovalCreationAfterStopIsRejected()
    {
        GatewayToolApprovalRequest first = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(first);
        ThreadCommandService service = CreateStopCapableService(approvalStore, out ThreadCommandAgentStore agentStore, out _);
        var admissionGate = new RootRunAdmissionGate(agentStore);
        var guardedStore = new GatewayApprovalStoreWrapper(
            approvalStore,
            admissionGate,
            new ApprovalResolutionTestInvocationLinkStore(),
            agentStore);

        ThreadCommandResult stopResult = await service.StopActiveRootAsync(
            StopCommand(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, stopResult.Status);

        Task<bool>[] attempts = Enumerable.Range(0, 6)
            .Select(index => Task.Run(() =>
            {
                try
                {
                    guardedStore.CreateRequest(CreateRequest($"request-late-{index}"));
                    return true;
                }
                catch (RootRunAdmissionClosedException)
                {
                    return false;
                }
            }, TestContext.Current.CancellationToken))
            .ToArray();

        bool[] created = await Task.WhenAll(attempts).ConfigureAwait(true);

        Assert.All(created, Assert.False);
        Assert.All(Enumerable.Range(0, 6), index => Assert.Null(guardedStore.GetById($"request-late-{index}")));
    }

    [Fact]
    public async Task ConcurrentLateNestedChildApprovalAfterRootStopIsRejected()
    {
        var links = new ApprovalResolutionTestInvocationLinkStore(new AgentToolInvocationLink(
            "nested-late",
            "session-1",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            "event-nested-late",
            ChildSessionId: "child-session",
            ChildRunId: "child-run",
            ChildAgentId: "child-agent",
            RootRunId: "root-run"));
        GatewayToolApprovalRequest pending = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(pending);
        ThreadCommandService service = CreateStopCapableService(approvalStore, out ThreadCommandAgentStore agentStore, out _);
        agentStore.AppendEvent(RuntimeEvent(
            1,
            NaradEventTypes.RunStarted,
            new { trigger = "chat" },
            runId: "root-run",
            rootRunId: "root-run"));
        var admissionGate = new RootRunAdmissionGate(agentStore);
        var guardedStore = new GatewayApprovalStoreWrapper(
            approvalStore,
            admissionGate,
            links,
            agentStore);

        ThreadCommandResult stopResult = await service.StopActiveRootAsync(
            new StopActiveRootCommand("agent-1", "session-1", "root-run", "stopped", "user-1", "Admin User", null),
            TestContext.Current.CancellationToken);
        Assert.Equal(ThreadCommandStatus.Accepted, stopResult.Status);

        GatewayToolApprovalRequest nestedLate = CreateRequest("nested-late") with
        {
            SessionId = "child-session",
            RunId = "child-run",
            OuterSessionId = "session-1",
            AgentId = "child-agent",
        };

        Assert.Throws<RootRunAdmissionClosedException>(() => guardedStore.CreateRequest(nestedLate));
        Assert.Null(guardedStore.GetById("nested-late"));
    }

    [Fact]
    public async Task CommandPathsShareSingleResolutionOwner()
    {
        GatewayToolApprovalRequest request = CreateRequest("request-1");
        var approvalStore = new ThreadCommandApprovalStore(request);
        ThreadCommandService service = CreateService(approvalStore);

        ThreadCommandResult commandPath = await service.ResolveInterruptAsync(
            new ResolveThreadInterruptCommand(
                request.Id,
                ApprovalActionCatalog.Approve,
                Reason: null,
                "user-1",
                "Admin User",
                ExpectedRevision: null,
                HitlResolutionSource.ApprovalApi),
            TestContext.Current.CancellationToken);

        Assert.Equal(ThreadCommandStatus.Accepted, commandPath.Status);
        Assert.Equal("approved", approvalStore.GetById(request.Id)?.Decision);
        Assert.Equal("approvals", approvalStore.GetById(request.Id)?.ResolutionSource);
    }

    private static ResolveThreadInterruptCommand Command(
        string requestId = "request-1",
        bool approved = true,
        bool stopRun = false,
        long? expectedRevision = null)
        => new(
            requestId,
            stopRun
                ? ApprovalActionCatalog.StopRun
                : approved
                    ? ApprovalActionCatalog.Approve
                    : ApprovalActionCatalog.Deny,
            null,
            "user-1",
            "Admin User",
            expectedRevision);

    private static ResolveAllThreadInterruptsCommand ResolveAllCommand(bool trustThisRun = false)
        => new(
            "agent-1",
            "session-1",
            "run-1",
            Approved: true,
            trustThisRun,
            StopRun: false,
            Reason: null,
            "user-1",
            "Admin User",
            ExpectedRevision: null);

    private static StopActiveRootCommand StopCommand()
        => new("agent-1", "session-1", "run-1", "stopped", "user-1", "Admin User", null);

    private sealed class GatewayApprovalStoreWrapper(
        ThreadCommandApprovalStore inner,
        IRootRunAdmissionGate admissionGate,
        IAgentToolInvocationLinkStore invocationLinks,
        IAgentDefinitionStore agentDefinitionStore) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.AgentId)
                && !string.IsNullOrWhiteSpace(request.RunId)
                && string.IsNullOrWhiteSpace(request.Decision))
            {
                string? rootRunId = ApprovalRootRunIdResolver.Resolve(
                    request,
                    invocationLinks,
                    agentDefinitionStore);
                if (!string.IsNullOrWhiteSpace(rootRunId)
                    && admissionGate.IsAdmissionClosed(request.AgentId, rootRunId))
                {
                    throw new RootRunAdmissionClosedException(rootRunId);
                }
            }

            inner.CreateRequest(request);
        }

        public bool Resolve(
            string requestId,
            string decision,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
            => inner.Resolve(requestId, decision, reason, decidedBy, resolutionSource, continuation, resolutionEvent);

        public bool Resolve(
            string requestId,
            bool approved,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation)
            => inner.Resolve(requestId, approved, reason, decidedBy, resolutionSource, continuation);

        public GatewayToolApprovalRequest? GetById(string requestId) => inner.GetById(requestId);

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => inner.ListBySession(sessionId);

        public IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string outerSessionId) => inner.ListByOuterSession(outerSessionId);

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => inner.ListPending(userId, adminAll);

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
            => inner.ListHistory(userId, adminAll, limit, offset);

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;

        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }
}
