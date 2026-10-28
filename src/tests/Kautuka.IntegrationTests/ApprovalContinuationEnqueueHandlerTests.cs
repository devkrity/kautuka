using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ApprovalContinuationEnqueueHandlerTests
{
    [Fact]
    public async Task HandleEnqueuesAgentToolExecutionUsingApprovalTargetContext()
    {
        var queue = new CapturingQueue();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-agent-tool-1",
            RunId: "run-agent-tool-1",
            CheckpointId: null,
            AgentId: "agent-agent-tool-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null));

        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            approvalStore,
            new NoOpAgentToolApprovalWaiter(),
            new NoOpAgentToolInvocationLinkStore(),
            new ThreadCommandTestSupport.NoOpRunWaitStateStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(new HitlApprovalResolved(
            RequestId: "req-1",
            Approved: true,
            Reason: "telegram",
            DecidedByUserId: "user-1",
            Source: HitlResolutionSource.ChannelCallback,
            SessionId: "session-caller-1",
            TargetType: "agent",
            TargetId: "agent-caller-1",
            UserId: "user-1"),
            CancellationToken.None).ConfigureAwait(true);

        ApprovalContinuationWork work = await queue.WaitForWorkAsync().ConfigureAwait(true);
        Assert.Equal("session-agent-tool-1", work.SessionId);
        Assert.Equal("agent", work.TargetType);
        Assert.Equal("agent-agent-tool-1", work.TargetId);
        Assert.Equal("user-1", work.UserId);
        Assert.True(work.Approved);
        Assert.Equal(HitlResolutionSource.ChannelCallback, work.Source);
    }

    [Fact]
    public async Task HandleEnqueuesNestedBrowserChildApprovalContinuation()
    {
        var queue = new CapturingQueue();
        var approvalStore = new FakeApprovalStore(CreateApprovalRequest() with
        {
            OuterSessionId = "session-parent-1",
            OuterAgentId = "agent-parent-1",
        });
        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            approvalStore,
            new NoOpAgentToolApprovalWaiter(),
            new FakeAgentToolInvocationLinkStore(CreateInvocationLink()),
            CreateParentChildPauseWaitStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(CreateResolvedNotification(HitlResolutionSource.Browser), CancellationToken.None).ConfigureAwait(true);

        ApprovalContinuationWork work = await queue.WaitForWorkAsync().ConfigureAwait(true);
        Assert.Equal("req-linked-1", work.RequestId);
        Assert.Equal("session-child-1", work.SessionId);
        Assert.Equal("agent-child-1", work.TargetId);
        Assert.Equal(HitlResolutionSource.Browser, work.Source);
    }

    [Fact]
    public async Task HandleDoesNotEnqueueBrowserOuterResolutions()
    {
        var queue = new CapturingQueue();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-1",
            RunId: null,
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "invoke_agent_roller",
            ToolType: "agent",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null));

        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            approvalStore,
            new NoOpAgentToolApprovalWaiter(),
            new NoOpAgentToolInvocationLinkStore(),
            new ThreadCommandTestSupport.NoOpRunWaitStateStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(new HitlApprovalResolved(
            RequestId: "req-1",
            Approved: true,
            Reason: "chat-ui",
            DecidedByUserId: "user-1",
            Source: HitlResolutionSource.Browser,
            SessionId: "session-1",
            TargetType: "agent",
            TargetId: "agent-1",
            UserId: "user-1"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, queue.EnqueueCount);
    }

    [Fact]
    public async Task HandleEnqueuesLinkedAgentToolResolutionFromApprovalApi()
    {
        var queue = new CapturingQueue();
        var approvalStore = new FakeApprovalStore(CreateApprovalRequest());
        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            approvalStore,
            new FakeAgentToolApprovalWaiter(isWaiting: false),
            new FakeAgentToolInvocationLinkStore(CreateInvocationLink()),
            CreateParentChildPauseWaitStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(CreateResolvedNotification(HitlResolutionSource.ApprovalApi), CancellationToken.None).ConfigureAwait(true);

        ApprovalContinuationWork work = await queue.WaitForWorkAsync().ConfigureAwait(true);
        Assert.Equal(1, queue.EnqueueCount);
        Assert.Equal("req-linked-1", work.RequestId);
        Assert.Equal("session-child-1", work.SessionId);
        Assert.Equal("agent", work.TargetType);
        Assert.Equal("agent-child-1", work.TargetId);
        Assert.Equal(HitlResolutionSource.ApprovalApi, work.Source);
    }

    [Fact]
    public async Task HandleDoesNotEnqueueLinkedResolutionWhenAgentToolWaiterIsActive()
    {
        var queue = new CapturingQueue();
        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            new FakeApprovalStore(CreateApprovalRequest()),
            new FakeAgentToolApprovalWaiter(isWaiting: true),
            new FakeAgentToolInvocationLinkStore(CreateInvocationLink()),
            CreateParentChildPauseWaitStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(CreateResolvedNotification(HitlResolutionSource.ChannelCallback), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, queue.EnqueueCount);
    }

    [Fact]
    public async Task HandleDoesNotEnqueueLinkedResolutionWithoutParentChildPause()
    {
        var queue = new CapturingQueue();
        var handler = new ApprovalContinuationEnqueueHandler(
            queue,
            new FakeApprovalStore(CreateApprovalRequest() with
            {
                OuterSessionId = "session-parent-1",
                OuterAgentId = "agent-parent-1",
            }),
            new NoOpAgentToolApprovalWaiter(),
            new FakeAgentToolInvocationLinkStore(CreateInvocationLink()),
            new ThreadCommandTestSupport.NoOpRunWaitStateStore(),
            NullLogger<ApprovalContinuationEnqueueHandler>.Instance);

        await handler.Handle(CreateResolvedNotification(HitlResolutionSource.Browser), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(0, queue.EnqueueCount);
    }

    private static ThreadCommandTestSupport.NoOpRunWaitStateStore CreateParentChildPauseWaitStore()
    {
        var store = new ThreadCommandTestSupport.NoOpRunWaitStateStore();
        store.Seed(new RunWaitState(
            "wait-parent-child-1",
            "continuation-parent-1",
            "run-parent-1",
            "agent",
            "agent-parent-1",
            "session-parent-1",
            "agent-parent-1",
            "user-1",
            RunWaitReasons.Child,
            "req-linked-1",
            "{}",
            "{}",
            "[]",
            "tool-call-parent-1",
            "invoke_agent_roller",
            "req-linked-1",
            "session-child-1",
            "run-parent-1",
            "run-child-1",
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-parent-child-1",
            "run-parent-1"));
        return store;
    }

    private static GatewayToolApprovalRequest CreateApprovalRequest()
        => new(
            Id: "req-linked-1",
            SessionId: "session-child-1",
            RunId: "run-child-1",
            CheckpointId: "checkpoint-child-1",
            AgentId: "agent-child-1",
            UserId: "user-1",
            ToolCallId: "tool-call-child-1",
            ToolName: "run_skill_script",
            ToolType: "skill",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null);

    private static AgentToolInvocationLink CreateInvocationLink()
        => new(
            ApprovalRequestId: "req-linked-1",
            CallerSessionId: "session-parent-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CanonicalEventId: "event-link-1",
            CallerToolCallId: "tool-call-parent-1",
            ChildSessionId: "session-child-1",
            CallerRunId: "run-parent-1",
            ChildRunId: "run-child-1",
            ChildAgentId: "agent-child-1",
            RootRunId: "run-parent-1");

    private static HitlApprovalResolved CreateResolvedNotification(HitlResolutionSource source)
        => new(
            RequestId: "req-linked-1",
            Approved: true,
            Reason: source.ToString(),
            DecidedByUserId: "user-1",
            Source: source,
            SessionId: "session-parent-1",
            TargetType: "agent",
            TargetId: "agent-parent-1",
            UserId: "user-1");

    private sealed class CapturingQueue : IApprovalContinuationQueue
    {
        public int EnqueueCount { get; private set; }

        private readonly TaskCompletionSource<ApprovalContinuationWork> workTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ApprovalContinuationEnqueueResult> EnqueueAsync(ApprovalContinuationWork work, CancellationToken cancellationToken)
        {
            EnqueueCount++;
            workTcs.TrySetResult(work);
            return ValueTask.FromResult(ApprovalContinuationEnqueueResult.Inserted);
        }

        public async IAsyncEnumerable<ApprovalContinuationLease> LeaseAllAsync(
            string leaseOwner,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask CompleteAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask RenewAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ApprovalContinuationFailureDisposition> FailAsync(ApprovalContinuationLease lease, string safeError, CancellationToken cancellationToken)
            => ValueTask.FromResult(ApprovalContinuationFailureDisposition.RetryScheduled);

        public Task<ApprovalContinuationWork> WaitForWorkAsync()
            => workTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class NoOpAgentToolApprovalWaiter : IAgentToolApprovalWaiter
    {
        public bool IsWaiting(string requestId) => false;

        public ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeAgentToolApprovalWaiter(bool isWaiting) : IAgentToolApprovalWaiter
    {
        public bool IsWaiting(string requestId) => isWaiting;

        public ValueTask<HitlApprovalResolved> WaitAsync(string requestId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NoOpAgentToolInvocationLinkStore : IAgentToolInvocationLinkStore
    {
        public void Upsert(AgentToolInvocationLink link) { }

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId) => null;

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId) => [];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId) => null;
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId) => [];
        public bool HasLinksForSession(string sessionId) => false;
    }

    private sealed class FakeAgentToolInvocationLinkStore(AgentToolInvocationLink link) : IAgentToolInvocationLinkStore
    {
        public void Upsert(AgentToolInvocationLink value) => throw new NotSupportedException();

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
            => string.Equals(approvalRequestId, link.ApprovalRequestId, StringComparison.Ordinal) ? link : null;

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId) => [];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId) => null;

        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId) => [];

        public bool HasLinksForSession(string sessionId) => false;
    }

    private sealed class FakeApprovalStore(GatewayToolApprovalRequest request) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request)
            => throw new NotSupportedException();

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll)
            => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName)
            => null;

        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson)
            => null;

        public GatewayToolApprovalRequest? GetById(string requestId)
            => string.Equals(requestId, request.Id, StringComparison.Ordinal) ? request : null;

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
            => [];
    }
}
