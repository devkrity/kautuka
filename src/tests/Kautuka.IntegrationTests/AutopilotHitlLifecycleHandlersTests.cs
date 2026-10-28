using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotHitlLifecycleHandlersTests
{
    [Fact]
    public async Task ApprovalRequestedIgnoresAutopilotPreRunSoRunStaysAwaitingApproval()
    {
        GatewayScheduledRun run = new(
            Id: "srun-prerun",
            ScheduleId: "schedule-1",
            OwnerUserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-1",
            Status: AutopilotRunStatuses.AwaitingPreRunApproval,
            TriggerType: "manual",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null,
            RuntimeRunId: null,
            PreRunApprovalRequestId: "apr-prerun",
            InputJson: "{}",
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStateCategories.PendingApproval,
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: null,
            EndedAt: null);

        var autopilotStore = new RecordingAutopilotStore(run);
        var approvalStore = new StaticApprovalStore(new GatewayToolApprovalRequest(
            Id: "apr-prerun",
            SessionId: null,
            RunId: "srun-prerun",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: AutopilotLaunchPolicy.PreRunToolCallId,
            ToolName: AutopilotLaunchPolicy.PreRunToolName,
            ToolType: AutopilotLaunchPolicy.PreRunToolType,
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null));

        var handler = new AutopilotApprovalRequestedHandler(autopilotStore, approvalStore, new RecordingQuarantineStore());
        await handler.Handle(new HitlApprovalRequested(
            RequestId: "apr-prerun",
            ToolCallId: AutopilotLaunchPolicy.PreRunToolCallId,
            ToolName: AutopilotLaunchPolicy.PreRunToolName,
            ArgumentsJson: "{}",
            SessionId: string.Empty,
            TargetType: "agent",
            TargetId: "agent-1",
            UserId: "user-1",
            RunId: "srun-prerun",
            ToolType: AutopilotLaunchPolicy.PreRunToolType), CancellationToken.None);

        Assert.Empty(autopilotStore.Transitions);
    }

    [Fact]
    public async Task ContinuationCompletedResolvesRunByApprovalRequestRunIdWhenSessionDoesNotMatch()
    {
        GatewayScheduledRun run = new(
            Id: "srun-1",
            ScheduleId: "schedule-1",
            OwnerUserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-1",
            Status: AutopilotRunStatuses.WaitingForInRunApproval,
            TriggerType: "manual",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: "session-caller",
            RuntimeRunId: null,
            PreRunApprovalRequestId: null,
            InputJson: null,
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStateCategories.PendingApproval,
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            EndedAt: null);

        var autopilotStore = new RecordingAutopilotStore(run);
        var approvalStore = new StaticApprovalStore(new GatewayToolApprovalRequest(
            Id: "apr-1",
            SessionId: "session-agent-tool",
            RunId: "srun-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: null));

        var handler = new AutopilotContinuationCompletedHandler(autopilotStore, approvalStore, new RecordingQuarantineStore());

        await handler.Handle(new HitlContinuationCompleted(
            RequestId: "apr-1",
            SessionId: "session-agent-tool",
            TargetType: "agent",
            TargetId: "agent-1",
            UserId: "user-1",
            Approved: true,
            Source: HitlResolutionSource.ChannelCallback,
            HasNestedApprovalRequest: false,
            OutputText: "done"), CancellationToken.None);

        Assert.Contains(autopilotStore.Transitions, t =>
            t.RunId == "srun-1"
            && t.ExpectedCurrentStatus == AutopilotRunStatuses.WaitingForInRunApproval
            && t.NextStatus == AutopilotRunStatuses.Completed);
    }

    [Fact]
    public async Task ContinuationCompletedQuarantinesMissingExactRunCorrelation()
    {
        GatewayScheduledRun run = new(
            Id: "srun-2",
            ScheduleId: "schedule-2",
            OwnerUserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-root",
            Status: AutopilotRunStatuses.WaitingForInRunApproval,
            TriggerType: "manual",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: "session-root",
            RuntimeRunId: null,
            PreRunApprovalRequestId: null,
            InputJson: null,
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStateCategories.PendingApproval,
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            EndedAt: null);

        var autopilotStore = new RecordingAutopilotStore(run);
        var approvalStore = new StaticApprovalStore(new GatewayToolApprovalRequest(
            Id: "apr-nested",
            SessionId: "session-agent-tool",
            RunId: "runtime-run-id-not-scheduled-id",
            CheckpointId: null,
            AgentId: "agent-agent-tool",
            UserId: "user-1",
            ToolCallId: "tool-nested-1",
            ToolName: "run_skill_script",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: null));

        var quarantine = new RecordingQuarantineStore();
        var handler = new AutopilotContinuationCompletedHandler(autopilotStore, approvalStore, quarantine);

        await handler.Handle(new HitlContinuationCompleted(
            RequestId: "apr-nested",
            SessionId: "session-agent-tool",
            TargetType: "agent",
            TargetId: "agent-agent-tool",
            UserId: "user-1",
            Approved: true,
            Source: HitlResolutionSource.ChannelCallback,
            HasNestedApprovalRequest: false,
            OutputText: "nested done"), CancellationToken.None);

        Assert.Empty(autopilotStore.Transitions);
        Assert.Contains(quarantine.Items, item =>
            item.EntityId == "apr-nested" && item.ReasonCode == "scheduled_run_not_found");
    }

    private sealed class RecordingAutopilotStore(GatewayScheduledRun run) : IGatewayAutopilotStore
    {
        public List<RunTransition> Transitions { get; } = [];

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => null;
        public GatewayScheduledRun? GetRunById(string runId) => runId == run.Id ? run : null;
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => null;
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit)
            => string.Equals(status, AutopilotRunStatuses.WaitingForInRunApproval, StringComparison.Ordinal)
                ? [run]
                : [];
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit) => [];

        public bool TransitionRunState(string runId, string expectedCurrentStatus, string nextStatus, string? error, string? outputJson, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, DateTimeOffset nowUtc, bool markStarted, bool markEnded)
        {
            Transitions.Add(new RunTransition(runId, expectedCurrentStatus, nextStatus));
            return true;
        }

        public bool UpdateRunMetadata(string runId, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, string? outputJson, string? error, DateTimeOffset nowUtc) => false;
        public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc) => false;
        public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset) => [];
        public void RecordSuppression(GatewayAutopilotSuppression suppression) { }
        public void RecordNotification(GatewayAutopilotNotification notification) { }
        public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly) => [];
        public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc) => false;
    }

    private sealed record RunTransition(string RunId, string ExpectedCurrentStatus, string NextStatus);

    private sealed class RecordingQuarantineStore : IExecutionQuarantineStore
    {
        public List<ExecutionQuarantineItem> Items { get; } = [];

        public ExecutionQuarantineItem Quarantine(string entityType, string entityId, string reasonCode, string safeDiagnostic)
        {
            var item = new ExecutionQuarantineItem(
                $"q-{Items.Count + 1}", entityType, entityId, reasonCode, safeDiagnostic,
                ExecutionQuarantineStatuses.Open, 0, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
            Items.Add(item);
            return item;
        }

        public IReadOnlyList<ExecutionQuarantineItem> ListActive() => Items;
        public ExecutionQuarantineItem? Get(string quarantineId) => Items.FirstOrDefault(item => item.QuarantineId == quarantineId);
        public bool RequestRetry(string quarantineId) => true;
        public bool Terminate(string quarantineId) => true;
        public bool Resolve(string entityType, string entityId) => true;
        public void MarkAuditPublished(string quarantineId, string publishedAt)
        {
            int index = Items.FindIndex(item => item.QuarantineId == quarantineId);
            if (index >= 0)
            {
                Items[index] = Items[index] with { AuditPublishedAt = publishedAt };
            }
        }
    }

    private sealed class StaticApprovalStore(GatewayToolApprovalRequest approval) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) => throw new NotSupportedException();
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => throw new NotSupportedException();
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) => throw new NotSupportedException();
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public GatewayToolApprovalRequest? GetById(string requestId) => requestId == approval.Id ? approval : null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
    }
}
