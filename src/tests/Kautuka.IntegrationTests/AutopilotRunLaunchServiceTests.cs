using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotRunLaunchServiceTests
{
    [Fact]
    public async Task LaunchRequestsPreRunApprovalInsteadOfStartingWhenScheduleNeedsApproval()
    {
        GatewayAutopilotSchedule schedule = CreateSchedule(AutopilotScheduleModes.NeedsApproval);
        GatewayScheduledRun run = CreateRun(schedule, AutopilotRunStatuses.Scheduled);
        var autopilotStore = new RecordingAutopilotStore(run);
        var approvalStore = new RecordingApprovalStore();
        var execution = new RecordingRunExecutionService();
        var mediator = new RecordingMediator();
        var service = new AutopilotRunLaunchService(
            autopilotStore,
            approvalStore,
            execution,
            mediator,
            NullLogger<AutopilotRunLaunchService>.Instance,
            new TestGatewayWorkflowStore());

        await service.LaunchAsync(schedule, run, CancellationToken.None);

        GatewayToolApprovalRequest approval = Assert.Single(approvalStore.Created);
        Assert.Equal(AutopilotLaunchPolicy.PreRunToolType, approval.ToolType);
        Assert.Equal(run.Id, approval.RunId);
        Assert.Equal(schedule.OwnerUserId, approval.UserId);
        Assert.Null(approval.SessionId);
        Assert.Equal(AutopilotRunStatuses.AwaitingPreRunApproval, autopilotStore.Current.Status);
        Assert.Equal(approval.Id, autopilotStore.Current.PreRunApprovalRequestId);
        HitlApprovalRequested requested = Assert.Single(mediator.Requested);
        Assert.Equal(approval.Id, requested.RequestId);
        Assert.Equal(AutopilotLaunchPolicy.PreRunToolType, requested.ToolType);
        Assert.Equal(string.Empty, requested.SessionId);
        Assert.Empty(execution.StartedRunIds);
    }

    [Fact]
    public async Task LaunchStartsExecutionWhenPreRunApprovalIsAlreadyApproved()
    {
        GatewayAutopilotSchedule schedule = CreateSchedule(AutopilotScheduleModes.NeedsApproval);
        GatewayScheduledRun run = CreateRun(schedule, AutopilotRunStatuses.AwaitingPreRunApproval, "apr-1");
        var autopilotStore = new RecordingAutopilotStore(run);
        var approvalStore = new RecordingApprovalStore();
        approvalStore.Created.Add(new GatewayToolApprovalRequest(
            Id: "apr-1",
            SessionId: null,
            RunId: run.Id,
            CheckpointId: null,
            AgentId: schedule.TargetId,
            UserId: schedule.OwnerUserId,
            ToolCallId: AutopilotLaunchPolicy.PreRunToolCallId,
            ToolName: AutopilotLaunchPolicy.PreRunToolName,
            ToolType: AutopilotLaunchPolicy.PreRunToolType,
            McpServerId: null,
            ArgumentsJson: run.InputJson,
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: schedule.OwnerUserId,
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: null));
        var execution = new RecordingRunExecutionService();
        var service = new AutopilotRunLaunchService(
            autopilotStore,
            approvalStore,
            execution,
            new RecordingMediator(),
            NullLogger<AutopilotRunLaunchService>.Instance,
            new TestGatewayWorkflowStore());

        await service.LaunchAsync(schedule, run, CancellationToken.None);

        Assert.Equal(run.Id, Assert.Single(execution.StartedRunIds));
    }

    [Fact]
    public async Task RemindPendingRepublishesOutstandingInRunApproval()
    {
        GatewayAutopilotSchedule schedule = CreateSchedule(AutopilotScheduleModes.NeedsApproval);
        GatewayScheduledRun run = CreateRun(schedule, AutopilotRunStatuses.WaitingForInRunApproval);
        run = run with { SessionId = "session-autopilot" };
        var approvalStore = new RecordingApprovalStore();
        approvalStore.Created.Add(new GatewayToolApprovalRequest(
            Id: "apr-inrun",
            SessionId: "session-autopilot",
            RunId: "runtime-1",
            CheckpointId: null,
            AgentId: schedule.TargetId,
            UserId: schedule.OwnerUserId,
            ToolCallId: "call-1",
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
        var mediator = new RecordingMediator();
        var service = new AutopilotRunLaunchService(
            new RecordingAutopilotStore(run),
            approvalStore,
            new RecordingRunExecutionService(),
            mediator,
            NullLogger<AutopilotRunLaunchService>.Instance,
            new TestGatewayWorkflowStore());

        Assert.True(await service.RemindPendingAsync(schedule, run, CancellationToken.None));
        HitlApprovalRequested requested = Assert.Single(mediator.Requested);
        Assert.Equal("apr-inrun", requested.RequestId);
        Assert.Equal("session-autopilot", requested.SessionId);
        Assert.Equal("mcp", requested.ToolType);
    }

    [Fact]
    public async Task RemindPendingMatchesApprovalsOnlyToTheLinkedWorkflowInstanceAndItsAttempts()
    {
        GatewayAutopilotSchedule schedule = CreateSchedule(AutopilotScheduleModes.On) with { TargetType = AutopilotTargetTypes.Workflow };
        GatewayScheduledRun run = CreateRun(schedule, AutopilotRunStatuses.WaitingForInRunApproval) with { RuntimeRunId = "instance-current" };
        using var testDatabase = new TestGatewayDatabase();
        var workflows = new GatewayWorkflowStore(testDatabase.Database);
        var approvals = new GatewayApprovalStore(testDatabase.Database);
        GatewayWorkflowDefinition definition = CreateWorkflowDefinition();
        workflows.CreateDefinition(definition);
        GatewayWorkflowInstance instance = CreateWorkflowInstance("instance-current", definition, "run-step-1", "session-step-1");
        GatewayWorkflowInstance foreignInstance = CreateWorkflowInstance("instance-other", definition, "foreign-run", "foreign-session");
        workflows.CreateInstance(instance);
        workflows.CreateInstance(foreignInstance);
        approvals.CreateRequest(Approval("linked-instance", "unrelated-run", null, "instance-current", "workflow_review", null));
        approvals.CreateRequest(Approval("linked-attempt", "run-step-1", "another-session", null, "mcp", null));
        approvals.CreateRequest(Approval("linked-session", "unrelated-run", "session-step-1", null, "mcp", null));
        approvals.CreateRequest(Approval("linked-child", "child-run", "child-session", null, "mcp", "session-step-1"));
        approvals.CreateRequest(Approval("foreign-prerun", "schedule-other", null, null, AutopilotLaunchPolicy.PreRunToolType, null));
        approvals.CreateRequest(Approval("unlinked-no-session", null, null, null, "workflow_review", null));
        approvals.CreateRequest(Approval("foreign-review", "foreign-run", null, "instance-other", "workflow_review", null));
        var mediator = new RecordingMediator();
        var service = new AutopilotRunLaunchService(
            new RecordingAutopilotStore(run), approvals, new RecordingRunExecutionService(), mediator,
            NullLogger<AutopilotRunLaunchService>.Instance, workflows);

        Assert.True(await service.RemindPendingAsync(schedule, run, CancellationToken.None));

        Assert.Equal(["linked-instance", "linked-attempt", "linked-session", "linked-child"], mediator.Requested.Select(item => item.RequestId));
    }

    private static GatewayToolApprovalRequest Approval(string id, string? runId, string? sessionId, string? instanceId, string type, string? outerSessionId) => new(
        Id: id, SessionId: sessionId, RunId: runId, CheckpointId: null, AgentId: null,
        UserId: "user-1", ToolCallId: id, ToolName: type, ToolType: type, McpServerId: null,
        ArgumentsJson: "{}", RequestedAt: DateTimeOffset.UtcNow.ToString("O"), Decision: null,
        DecidedBy: null, DecidedAt: null, Reason: null, ExpiresAt: null, InstanceId: instanceId, OuterSessionId: outerSessionId);

    private static GatewayWorkflowDefinition CreateWorkflowDefinition()
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayWorkflowDefinition(
            "workflow-1", "user-1", "Workflow", false, 1, new JsonObject(), new JsonObject(), new JsonObject(),
            [
                new GatewayWorkflowNode("step-1", "agent-1", "Prompt", "object", null, [], "read_only", false, false, 0),
                new GatewayWorkflowNode("step-2", "agent-1", "Prompt", "object", null, [], "read_only", false, false, 0),
            ], now, now);
    }

    private static GatewayWorkflowInstance CreateWorkflowInstance(string instanceId, GatewayWorkflowDefinition definition, string runId, string sessionId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayWorkflowInstance(
            instanceId, definition.Id, definition.Revision, "user-1", null, WorkflowInstanceStatuses.Running, false, "step-1",
            new JsonObject(), "hash", 0, definition,
            [
                new GatewayWorkflowStepAttempt($"attempt-{instanceId}", instanceId, "step-1", 1, runId, sessionId, "running", null, now, null, now),
                new GatewayWorkflowStepAttempt($"attempt-{instanceId}-step-2", instanceId, "step-2", 1, "run-step-2", null, "pending", null, now, null, now),
            ],
            null, null, now, now, now, null);
    }

    [Fact]
    public async Task LaunchStartsExecutionWhenScheduleDoesNotNeedApproval()
    {
        GatewayAutopilotSchedule schedule = CreateSchedule(AutopilotScheduleModes.On);
        GatewayScheduledRun run = CreateRun(schedule, AutopilotRunStatuses.Scheduled);
        var execution = new RecordingRunExecutionService();
        var service = new AutopilotRunLaunchService(
            new RecordingAutopilotStore(run),
            new RecordingApprovalStore(),
            execution,
            new RecordingMediator(),
            NullLogger<AutopilotRunLaunchService>.Instance,
            new TestGatewayWorkflowStore());

        await service.LaunchAsync(schedule, run, CancellationToken.None);

        Assert.Equal(run.Id, Assert.Single(execution.StartedRunIds));
    }

    private static GatewayAutopilotSchedule CreateSchedule(string mode) =>
        new(
            Id: "schedule-1",
            OwnerUserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-1",
            CronExpression: "*/5 * * * *",
            TimeZoneId: "UTC",
            Mode: mode,
            RequiresPreRunApproval: string.Equals(mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal),
            SchedulePromptContext: "Run diagnostics",
            WorkspaceFilePaths: [],
            NotifyUiInbox: true,
            NotifyChannels: true,
            PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.Never,
            PreRunApprovalTimeoutSeconds: null,
            OverlapPolicy: AutopilotOverlapPolicies.SkipAndNotify,
            IsPaused: false,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            NextDueAt: DateTimeOffset.UtcNow.ToString("O"),
            LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner: null,
            LeaseExpiresAt: null);

    private static GatewayScheduledRun CreateRun(
        GatewayAutopilotSchedule schedule,
        string status,
        string? preRunApprovalRequestId = null) =>
        new(
            Id: "srun-1",
            ScheduleId: schedule.Id,
            OwnerUserId: schedule.OwnerUserId,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            Status: status,
            TriggerType: "manual",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null,
            RuntimeRunId: null,
            PreRunApprovalRequestId: preRunApprovalRequestId,
            InputJson: "{}",
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStatuses.GetStateCategory(status),
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: null,
            EndedAt: null);

    private sealed class RecordingAutopilotStore(GatewayScheduledRun run) : IGatewayAutopilotStore
    {
        public GatewayScheduledRun Current { get; private set; } = run;

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => null;
        public GatewayScheduledRun? GetRunById(string runId)
            => string.Equals(Current.Id, runId, StringComparison.Ordinal) ? Current : null;
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => null;
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit) => [];
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit) => [];

        public bool TransitionRunState(string runId, string expectedCurrentStatus, string nextStatus, string? error, string? outputJson, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, DateTimeOffset nowUtc, bool markStarted, bool markEnded)
        {
            if (!string.Equals(Current.Id, runId, StringComparison.Ordinal)
                || !string.Equals(Current.Status, expectedCurrentStatus, StringComparison.Ordinal))
            {
                return false;
            }

            Current = Current with
            {
                Status = nextStatus,
                PreRunApprovalRequestId = preRunApprovalRequestId ?? Current.PreRunApprovalRequestId,
                Error = error,
                OutputJson = outputJson,
                SessionId = sessionId ?? Current.SessionId,
                RuntimeRunId = runtimeRunId ?? Current.RuntimeRunId,
                UpdatedAt = nowUtc.ToString("O"),
            };
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

    private sealed class RecordingApprovalStore : IGatewayApprovalStore
    {
        public List<GatewayToolApprovalRequest> Created { get; } = [];

        public void CreateRequest(GatewayToolApprovalRequest request) => Created.Add(request);
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => Created.Where(item => item.Decision is null).ToList();
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId)
            => Created.FirstOrDefault(item => string.Equals(item.Id, requestId, StringComparison.Ordinal));
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    private sealed class RecordingRunExecutionService : IAutopilotRunExecutionService
    {
        public List<string> StartedRunIds { get; } = [];

        public Task StartAndExecuteAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, CancellationToken cancellationToken)
        {
            StartedRunIds.Add(run.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<HitlApprovalRequested> Requested { get; } = [];

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
        {
            if (notification is HitlApprovalRequested requested)
            {
                Requested.Add(requested);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            if (notification is HitlApprovalRequested requested)
            {
                Requested.Add(requested);
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class TestGatewayDatabase : IDisposable
{
    public TestGatewayDatabase()
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"kautuka-autopilot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = RootPath,
            })
            .Build();
        Database = new KautukaDatabase(configuration);
        Database.EnsureReady();
    }

    public string RootPath { get; }
    public KautukaDatabase Database { get; }

    public void Dispose()
    {
        SqliteTestPoolCleanup.ClearPoolsUnderDirectory(RootPath);
        if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
    }
}
