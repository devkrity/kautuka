using Kautuka.ChannelContracts.Channels;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class HitlEventHandlersTests
{
    [Fact]
    public async Task ChannelApprovalRequestedHandlerKeepsChildApprovalInChildSession()
    {
        var ingress = new CapturingChannelIngressService();
        var handler = new ChannelApprovalRequestedHandler(
            ingress,
            NullLogger<ChannelApprovalRequestedHandler>.Instance);

        await handler.Handle(new HitlApprovalRequested(
            RequestId: "req-1",
            ToolCallId: "tool-1",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            SessionId: "session-inner",
            TargetType: "agent",
            TargetId: "agent-inner",
            UserId: "user-1",
            RunId: "run-1",
            ToolType: "mcp"), CancellationToken.None);

        Assert.Equal("user-1", ingress.LastUserId);
        Assert.Equal("session-inner", ingress.LastSessionId);
        Assert.Equal("agent", ingress.LastTargetType);
        Assert.Equal("agent-inner", ingress.LastTargetId);
    }

    [Fact]
    public async Task ApprovalStreamInvalidationHandlerPublishesDoorbellForSessionlessRequest()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForUserApprovals("user-1");
        var handler = new ApprovalStreamInvalidationHandler(hub);

        await handler.Handle(new HitlApprovalRequested(
            RequestId: "apr-prerun",
            ToolCallId: "autopilot_pre_run",
            ToolName: "autopilot_pre_run",
            ArgumentsJson: "{}",
            SessionId: string.Empty,
            TargetType: "agent",
            TargetId: "agent-1",
            UserId: "user-1",
            RunId: "srun-1",
            ToolType: "autopilot_prerun"), CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ApprovalInvalidation delivered = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal($"hitl-invalidation:apr-prerun:requested", delivered.EventId);
        Assert.Equal(ApprovalStreamInvalidationHandler.WatchEventType, delivered.EventType);
        Assert.Equal("apr-prerun", delivered.RootRunId);
        Assert.Null(delivered.SessionId);
    }

    [Fact]
    public async Task ApprovalStreamInvalidationHandlerPublishesDoorbellForInRunApprovalsWithSession()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForUserApprovals("user-1");
        var handler = new ApprovalStreamInvalidationHandler(hub);

        await handler.Handle(new HitlApprovalRequested(
            RequestId: "apr-inrun",
            ToolCallId: "tool-1",
            ToolName: "run_shell",
            ArgumentsJson: "{}",
            SessionId: "session-1",
            TargetType: "agent",
            TargetId: "agent-1",
            UserId: "user-1",
            RunId: "run-1",
            ToolType: "mcp"), CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ApprovalInvalidation delivered = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal($"hitl-invalidation:apr-inrun:requested", delivered.EventId);
        Assert.Equal(ApprovalStreamInvalidationHandler.WatchEventType, delivered.EventType);
        Assert.Equal("session-1", delivered.SessionId);
        Assert.Equal("apr-inrun", delivered.RootRunId);
    }

    private sealed class CapturingChannelIngressService : IChannelIngressService
    {
        public string? LastUserId { get; private set; }
        public string? LastSessionId { get; private set; }
        public string? LastTargetType { get; private set; }
        public string? LastTargetId { get; private set; }

        public Task<ChannelIngressResult> ProcessInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken)
            => Task.FromResult(new ChannelIngressResult(true, "ignored", "not used"));

        public Task<ChannelInteractionDispatchResult> DispatchChannelInteractionAsync(string registrationId, System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
            => Task.FromResult(new ChannelInteractionDispatchResult(true, "ignored", "not used"));

        public Task NotifyChannelApprovalAsync(string userId, string sessionId, string? targetType, string? targetId, GatewayChatApprovalRequest request, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            LastSessionId = sessionId;
            LastTargetType = targetType;
            LastTargetId = targetId;
            return Task.CompletedTask;
        }

        public Task SyncChannelApprovalResolutionAsync(
            ChannelApprovalResolution resolution,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyChannelContinuationAsync(string requestId, string outputText, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingScanSessionStore(params GatewaySession[] sessions) : IGatewaySessionStore
    {
        private readonly Dictionary<string, GatewaySession> sessionsById = sessions.ToDictionary(session => session.Id, StringComparer.Ordinal);

        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => [];
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => null;
        public GatewaySession? ArchiveSession(string sessionId) => null;
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => [];
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => [];
        public bool TryGetSession(string sessionId, out GatewaySession? session) => sessionsById.TryGetValue(sessionId, out session);
        public bool DeleteSession(string sessionId) => false;
        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => null;
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null) => throw new NotSupportedException();
        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => throw new NotSupportedException();
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteMemoryFact(string factId) => false;
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => false;
        public IReadOnlyList<GatewayRun> ListRuns() => [];
        public bool TryGetRun(string runId, out GatewayRun? run)
        {
            run = null;
            return false;
        }
    }

    private sealed class StaticApprovalStore(GatewayToolApprovalRequest request) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest requestToCreate) => throw new NotSupportedException();
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public GatewayToolApprovalRequest? GetById(string requestId) => string.Equals(requestId, request.Id, StringComparison.Ordinal) ? request : null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) => throw new NotSupportedException();
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
    }

    private sealed class StaticAgentToolInvocationLinkStore(AgentToolInvocationLink? link) : IAgentToolInvocationLinkStore
    {
        public void Upsert(AgentToolInvocationLink linkToPersist) => throw new NotSupportedException();
        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
            => string.Equals(approvalRequestId, link?.ApprovalRequestId, StringComparison.Ordinal) ? link : null;
        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
            => string.Equals(callerSessionId, link?.CallerSessionId, StringComparison.Ordinal) && link is not null ? [link] : [];
        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
            => string.Equals(childSessionId, link?.ChildSessionId, StringComparison.Ordinal) && link is not null ? link : null;
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
            => link is not null && string.Equals(link.RootRunId, rootRunId, StringComparison.Ordinal) ? [link] : [];
        public bool HasLinksForSession(string sessionId)
            => link is not null && string.Equals(link.CallerSessionId, sessionId, StringComparison.Ordinal);
    }
}
