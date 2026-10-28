using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotSchedulerServiceTests
{
    [Fact]
    public async Task FailedSchedulerCycleIsLoggedAndTheNextTickCanRun()
    {
        int completedTicks = 0;
        await AutopilotSchedulerService.RunSafelyAsync(
            NullLogger<AutopilotSchedulerService>.Instance,
            "scheduled cycle",
            () => throw new InvalidOperationException("first tick failed"),
            CancellationToken.None);
        await AutopilotSchedulerService.RunSafelyAsync(
            NullLogger<AutopilotSchedulerService>.Instance,
            "scheduled cycle",
            () => { completedTicks++; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(1, completedTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkflowOutputTreatsMissingOrEmptyStringAsNoOutput(string? output)
    {
        JsonNode? node = output is null ? null : JsonValue.Create(output);

        Assert.Null(AutopilotWorkflowOutput.Serialize(node));
    }

    [Fact]
    public void WorkflowOutputPreservesNonEmptyJson()
    {
        Assert.Equal("\"result\"", AutopilotWorkflowOutput.Serialize(JsonValue.Create("result")));
    }

    [Fact]
    public async Task RunCycleOnceAsyncSuppressesDueScheduleForInactiveOwner()
    {
        GatewayAutopilotSchedule schedule = new(
            Id: "schedule-1",
            OwnerUserId: "user-inactive",
            TargetType: "agent",
            TargetId: "agent-a",
            CronExpression: "*/5 * * * *",
            TimeZoneId: "UTC",
            Mode: AutopilotScheduleModes.On,
            RequiresPreRunApproval: false,
            SchedulePromptContext: "Run diagnostics",
            WorkspaceFilePaths: ["docs/runbook.md"],
            NotifyUiInbox: true,
            NotifyChannels: false,
            PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.NextOccurrence,
            PreRunApprovalTimeoutSeconds: null,
            OverlapPolicy: AutopilotOverlapPolicies.SkipAndNotify,
            IsPaused: false,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            NextDueAt: DateTimeOffset.UtcNow.ToString("O"),
            LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner: null,
            LeaseExpiresAt: null);

        var autopilotStore = new RecordingAutopilotStore(schedule);
        var userStore = new RecordingUserStore(new GatewayUser(
            Id: "user-inactive",
            Username: "inactive-user",
            Password: "pw",
            Role: "user",
            IsActive: false,
            Email: null,
            DisplayName: null));
        var scheduler = new AutopilotSchedulerService(
            autopilotStore,
            new NoOpApprovalStore(),
            userStore,
            new AutopilotRunLaunchService(
                autopilotStore,
                new NoOpApprovalStore(),
                new RecordingRunExecutionService(),
                new NoOpMediator(),
                NullLogger<AutopilotRunLaunchService>.Instance,
                new TestGatewayWorkflowStore()),
            new RecordingNotificationDispatcher(),
            new TestGatewayWorkflowStore(),
            NullLogger<AutopilotSchedulerService>.Instance);

        await scheduler.RunCycleOnceAsync(CancellationToken.None);

        Assert.Empty(autopilotStore.InsertedRuns);
        Assert.Contains(autopilotStore.Suppressions, item => item.ScheduleId == schedule.Id && item.Reason == "inactive_owner");
        Assert.True(autopilotStore.ScheduleUpdated);
    }

    [Fact]
    public async Task RunCycleOnceAsyncStoresWorkspaceFilePathsInRunInput()
    {
        GatewayAutopilotSchedule schedule = new(
            Id: "schedule-1",
            OwnerUserId: "user-active",
            TargetType: "agent",
            TargetId: "agent-a",
            CronExpression: "*/5 * * * *",
            TimeZoneId: "UTC",
            Mode: AutopilotScheduleModes.On,
            RequiresPreRunApproval: false,
            SchedulePromptContext: "Run diagnostics",
            WorkspaceFilePaths: ["docs/runbook.md"],
            NotifyUiInbox: true,
            NotifyChannels: false,
            PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.NextOccurrence,
            PreRunApprovalTimeoutSeconds: null,
            OverlapPolicy: AutopilotOverlapPolicies.SkipAndNotify,
            IsPaused: false,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            NextDueAt: DateTimeOffset.UtcNow.ToString("O"),
            LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner: null,
            LeaseExpiresAt: null);

        var autopilotStore = new RecordingAutopilotStore(schedule);
        var userStore = new RecordingUserStore(new GatewayUser(
            Id: "user-active",
            Username: "active-user",
            Password: "pw",
            Role: "user",
            IsActive: true,
            Email: null,
            DisplayName: null));
        var scheduler = new AutopilotSchedulerService(
            autopilotStore,
            new NoOpApprovalStore(),
            userStore,
            new AutopilotRunLaunchService(
                autopilotStore,
                new NoOpApprovalStore(),
                new RecordingRunExecutionService(),
                new NoOpMediator(),
                NullLogger<AutopilotRunLaunchService>.Instance,
                new TestGatewayWorkflowStore()),
            new RecordingNotificationDispatcher(),
            new TestGatewayWorkflowStore(),
            NullLogger<AutopilotSchedulerService>.Instance);

        await scheduler.RunCycleOnceAsync(CancellationToken.None);

        GatewayScheduledRun run = Assert.Single(autopilotStore.InsertedRuns);
        using JsonDocument input = JsonDocument.Parse(run.InputJson!);
        Assert.Equal("Run diagnostics", input.RootElement.GetProperty("schedulePromptContext").GetString());
        Assert.Equal("docs/runbook.md", input.RootElement.GetProperty("workspaceFilePaths")[0].GetString());
    }

    [Theory]
    [InlineData(WorkflowInstanceStatuses.Paused, AutopilotRunStatuses.WaitingForInRunApproval, true, true)]
    [InlineData(WorkflowInstanceStatuses.Running, AutopilotRunStatuses.WaitingForInRunApproval, true, true)]
    [InlineData(WorkflowInstanceStatuses.Paused, AutopilotRunStatuses.WaitingForInRunApproval, false, true)]
    [InlineData(WorkflowInstanceStatuses.Running, AutopilotRunStatuses.Running, true, false)]
    public async Task RunCycleOnceAsyncRemindsPausedWorkflowAndSuppressesRunningWorkflow(string instanceStatus, string runStatus, bool pendingApprovalFound, bool expectWaitingMessage)
    {
        GatewayAutopilotSchedule schedule = new(
            Id: "schedule-1",
            OwnerUserId: "user-active",
            TargetType: AutopilotTargetTypes.Workflow,
            TargetId: "workflow-1",
            CronExpression: "0 * * * *",
            TimeZoneId: "UTC",
            Mode: AutopilotScheduleModes.NeedsApproval,
            RequiresPreRunApproval: true,
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

        GatewayScheduledRun waiting = new(
            Id: "srun-waiting",
            ScheduleId: schedule.Id,
            OwnerUserId: schedule.OwnerUserId,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            Status: runStatus,
            TriggerType: "cron",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: "session-1",
            RuntimeRunId: "runtime-1",
            PreRunApprovalRequestId: null,
            InputJson: "{}",
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStatuses.GetStateCategory(runStatus),
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            EndedAt: null);

        var autopilotStore = new RecordingAutopilotStore(schedule, waiting);
        var userStore = new RecordingUserStore(new GatewayUser(
            Id: "user-active",
            Username: "active-user",
            Password: "pw",
            Role: "user",
            IsActive: true,
            Email: null,
            DisplayName: null));
        var launch = new RecordingRunLaunchService();
        launch.PendingApprovalFound = pendingApprovalFound;
        var notifications = new RecordingNotificationDispatcher();
        using var testDatabase = new TestGatewayDatabase();
        var workflowStore = new GatewayWorkflowStore(testDatabase.Database);
        GatewayWorkflowDefinition definition = CreateWorkflowDefinition();
        workflowStore.CreateDefinition(definition);
        workflowStore.CreateInstance(CreateWorkflowInstance("runtime-1", instanceStatus, definition));
        var scheduler = new AutopilotSchedulerService(
            autopilotStore,
            new NoOpApprovalStore(),
            userStore,
            launch,
            notifications,
            workflowStore,
            NullLogger<AutopilotSchedulerService>.Instance);

        await scheduler.RunCycleOnceAsync(CancellationToken.None);

        Assert.Empty(autopilotStore.InsertedRuns);
        Assert.Equal(expectWaitingMessage, launch.RemindedRunIds.Contains(waiting.Id));
        Assert.Equal(expectWaitingMessage && !pendingApprovalFound
            ? ["Auto-Pilot workflow is still waiting for a decision."]
            : [], notifications.FinalMessages);
        Assert.Equal(!expectWaitingMessage, autopilotStore.Suppressions.Any(item => item.Reason == "workflow_instance_running"));
        Assert.DoesNotContain(AutopilotRunStatuses.Stale, autopilotStore.TransitionTargets);
    }

    private static GatewayWorkflowDefinition CreateWorkflowDefinition()
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayWorkflowDefinition("workflow-1", "user-active", "Workflow", false, 1,
            new JsonObject(), new JsonObject(), new JsonObject(), [], now, now);
    }

    private static GatewayWorkflowInstance CreateWorkflowInstance(string id, string status, GatewayWorkflowDefinition definition)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayWorkflowInstance(id, definition.Id, definition.Revision, definition.OwnerUserId, null, status,
            false, null, new JsonObject(), "hash", 0, definition, [], null, null, now, now, now, null);
    }

    private sealed class RecordingAutopilotStore(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun? existingNonTerminal = null) : IGatewayAutopilotStore
    {
        public List<GatewayScheduledRun> InsertedRuns { get; } = [];
        public List<GatewayAutopilotSuppression> Suppressions { get; } = [];
        public List<string> TransitionTargets { get; } = [];
        public bool ScheduleUpdated { get; private set; }

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) { ScheduleUpdated = true; return true; }
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => scheduleId == schedule.Id ? schedule : null;
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => [schedule];
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) { InsertedRuns.Add(run); return run; }
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => existingNonTerminal;
        public GatewayScheduledRun? GetLatestNonTerminalRunBySchedule(string scheduleId)
            => existingNonTerminal is not null
                && string.Equals(existingNonTerminal.ScheduleId, scheduleId, StringComparison.Ordinal)
                && !AutopilotRunStatuses.IsTerminal(existingNonTerminal.Status)
                    ? existingNonTerminal
                    : null;
        public GatewayScheduledRun? GetRunById(string runId)
            => existingNonTerminal is not null && string.Equals(existingNonTerminal.Id, runId, StringComparison.Ordinal)
                ? existingNonTerminal
                : InsertedRuns.FirstOrDefault(item => string.Equals(item.Id, runId, StringComparison.Ordinal));
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => null;
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit) => [];
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit)
            => existingNonTerminal is null ? [] : [existingNonTerminal];
        public bool TransitionRunState(string runId, string expectedCurrentStatus, string nextStatus, string? error, string? outputJson, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, DateTimeOffset nowUtc, bool markStarted, bool markEnded) { TransitionTargets.Add(nextStatus); return true; }
        public bool UpdateRunMetadata(string runId, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, string? outputJson, string? error, DateTimeOffset nowUtc) => true;
        public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc) => false;
        public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset) => [];
        public void RecordSuppression(GatewayAutopilotSuppression suppression) => Suppressions.Add(suppression);
        public void RecordNotification(GatewayAutopilotNotification notification) { }
        public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly) => [];
        public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc) => false;
    }

    private sealed class RecordingUserStore(GatewayUser user) : IGatewayUserStore
    {
        public bool TryGetUserById(string userId, out GatewayUser? found)
        {
            found = string.Equals(user.Id, userId, StringComparison.Ordinal) ? user : null;
            return found is not null;
        }

        public bool TryGetUserByUsername(string username, out GatewayUser? found)
        {
            found = string.Equals(user.Username, username, StringComparison.Ordinal) ? user : null;
            return found is not null;
        }

        public IReadOnlyList<GatewayUserListItem> ListUsers() => [];
        public bool CanSetup => false;
        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) => throw new NotSupportedException();
        public GatewayUserListItem? InviteUser(System.Text.Json.JsonElement body, out string? error) => throw new NotSupportedException();
        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) { error = null; throw new NotSupportedException(); }
        public bool UpdateIsActive(string userId, bool isActive, out string? error) { error = null; throw new NotSupportedException(); }
        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => throw new NotSupportedException();
        public bool UpdateProfile(string userId, string? email, string? displayName) => throw new NotSupportedException();
        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) => throw new NotSupportedException();


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    private sealed class NoOpApprovalStore : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) { }
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }

    private sealed class NoOpMediator : IMediator
    {
        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Publish(object notification, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunExecutionService : IAutopilotRunExecutionService
    {
        public List<string> StartedScheduleIds { get; } = [];

        public Task StartAndExecuteAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, CancellationToken cancellationToken)
        {
            StartedScheduleIds.Add(schedule.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotificationDispatcher : IAutopilotNotificationDispatcher
    {
        public List<string> FinalMessages { get; } = [];

        public Task DispatchNotificationsAsync(GatewayScheduledRun run, string scheduleId, string? startedMessage, string? finalMessage, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                FinalMessages.Add(finalMessage);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunLaunchService : IAutopilotRunLaunchService
    {
        public List<string> RemindedRunIds { get; } = [];
        public bool PendingApprovalFound { get; set; } = true;

        public IReadOnlyList<GatewayToolApprovalRequest> ListPendingForRun(GatewayScheduledRun run) => [];

        public Task LaunchAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<bool> RemindPendingAsync(GatewayAutopilotSchedule schedule, GatewayScheduledRun run, CancellationToken cancellationToken)
        {
            RemindedRunIds.Add(run.Id);
            return Task.FromResult(PendingApprovalFound);
        }
    }
}
