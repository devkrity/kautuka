using System.Text.Json;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotRunExecutionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAndExecuteAsyncForwardsOwnerUserIdAndConsumesTheDurableTerminal(bool pauseForApproval)
    {
        var store = new RecordingAutopilotStore();
        var chatGatewayService = new CapturingChatGatewayService { PauseForApproval = pauseForApproval };
        var dispatcher = new RecordingNotificationDispatcher();
        var service = new AutopilotRunExecutionService(
            store,
            chatGatewayService,
            null!,
            null!,
            dispatcher,
            NullLogger<AutopilotRunExecutionService>.Instance);

        GatewayAutopilotSchedule schedule = new(
            Id: "schedule-1",
            OwnerUserId: "user-offline",
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
            NextDueAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner: null,
            LeaseExpiresAt: null);

        GatewayScheduledRun run = new(
            Id: "run-1",
            ScheduleId: schedule.Id,
            OwnerUserId: schedule.OwnerUserId,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            Status: AutopilotRunStatuses.Scheduled,
            TriggerType: "cron",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null,
            RuntimeRunId: null,
            PreRunApprovalRequestId: null,
            InputJson: null,
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStateCategories.Executing,
            SkipReason: null,
            BlockingRunId: null,
            BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null,
            BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: null,
            EndedAt: null);

        await service.StartAndExecuteAsync(schedule, run, CancellationToken.None);

        Assert.True(chatGatewayService.CapturedBody.HasValue);
        Assert.Equal("user-offline", chatGatewayService.CapturedBody.Value.GetProperty("user_id").GetString());
        Assert.Equal("agent", chatGatewayService.CapturedBody.Value.GetProperty("target_type").GetString());
        Assert.Equal("agent-a", chatGatewayService.CapturedBody.Value.GetProperty("target_id").GetString());
        Assert.Equal("Run diagnostics", chatGatewayService.CapturedBody.Value.GetProperty("message").GetString());
        Assert.Equal("docs/runbook.md", chatGatewayService.CapturedBody.Value.GetProperty("workspaceFiles")[0].GetString());
        Assert.Equal("autopilot", chatGatewayService.CapturedBody.Value.GetProperty("source").GetString());

        Assert.Contains(store.StateTransitions, transition =>
            transition.RunId == "run-1"
            && transition.ExpectedCurrentStatus == AutopilotRunStatuses.Scheduled
            && transition.NextStatus == AutopilotRunStatuses.Running);
        Assert.Contains(store.StateTransitions, transition =>
            transition.RunId == "run-1"
            && transition.ExpectedCurrentStatus == AutopilotRunStatuses.Running
            && transition.NextStatus == (pauseForApproval ? AutopilotRunStatuses.WaitingForInRunApproval : AutopilotRunStatuses.Completed));
        Assert.Contains(store.MetadataUpdates, update => update.RunId == "run-1" && update.SessionId == "session-offline");
        Assert.Equal(pauseForApproval ? 1 : 2, dispatcher.Messages.Count);
        if (!pauseForApproval)
        {
            Assert.Equal("session-offline", dispatcher.Runs[^1].SessionId);
            Assert.Equal(store.MetadataUpdates[^1].RuntimeRunId, dispatcher.Runs[^1].RuntimeRunId);
            Assert.False(string.IsNullOrWhiteSpace(dispatcher.Runs[^1].RuntimeRunId));
        }
        Assert.Equal(pauseForApproval, chatGatewayService.PausedTurnPersisted);
    }

    private sealed class CapturingChatGatewayService : IChatGatewayService
    {
        public bool PauseForApproval { get; init; }
        public bool PausedTurnPersisted { get; private set; }
        public JsonElement? CapturedBody { get; private set; }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedBody = body.Clone();

            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatSessionReady("session-offline"));
            if (PauseForApproval)
            {
                yield return NaradTestEventFactory.FromChatEvent(new GatewayChatApprovalRequest("approval-1", "call-1", "run_shell", "{}", RunId: "runtime-1"));
                PausedTurnPersisted = true;
                yield return NaradTestEventFactory.FromBody(NaradEventTypes.RunPaused,
                    JsonSerializer.SerializeToElement(new { reason = "approval_required" }), "session-offline", "runtime-1", "runtime-1", 3, 3);
                yield break;
            }
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-a",
                SessionId: "session-offline",
                Response: "done",
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0))));

            await Task.CompletedTask;
        }

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
            => throw new NotSupportedException();
    }

    private sealed class RecordingNotificationDispatcher : IAutopilotNotificationDispatcher
    {
        public List<string?> Messages { get; } = [];
        public List<GatewayScheduledRun> Runs { get; } = [];

        public Task DispatchNotificationsAsync(
            GatewayScheduledRun run,
            string scheduleId,
            string? startedMessage,
            string? finalMessage,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            Messages.Add(startedMessage ?? finalMessage);
            Runs.Add(run);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAutopilotStore : IGatewayAutopilotStore
    {
        public List<RunTransition> StateTransitions { get; } = [];
        public List<RunMetadataUpdate> MetadataUpdates { get; } = [];

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetRunById(string runId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit) => throw new NotSupportedException();

        public bool TransitionRunState(
            string runId,
            string expectedCurrentStatus,
            string nextStatus,
            string? error,
            string? outputJson,
            string? sessionId,
            string? runtimeRunId,
            string? preRunApprovalRequestId,
            DateTimeOffset nowUtc,
            bool markStarted,
            bool markEnded)
        {
            StateTransitions.Add(new RunTransition(runId, expectedCurrentStatus, nextStatus, sessionId, outputJson));
            return true;
        }

        public bool UpdateRunMetadata(
            string runId,
            string? sessionId,
            string? runtimeRunId,
            string? preRunApprovalRequestId,
            string? outputJson,
            string? error,
            DateTimeOffset nowUtc)
        {
            MetadataUpdates.Add(new RunMetadataUpdate(runId, sessionId, runtimeRunId));
            return true;
        }

        public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc) => false;

        public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset) => throw new NotSupportedException();
        public void RecordSuppression(GatewayAutopilotSuppression suppression) => throw new NotSupportedException();
        public void RecordNotification(GatewayAutopilotNotification notification) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly) => throw new NotSupportedException();
        public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc) => throw new NotSupportedException();
    }

    private sealed record RunTransition(
        string RunId,
        string ExpectedCurrentStatus,
        string NextStatus,
        string? SessionId,
        string? OutputJson);

    private sealed record RunMetadataUpdate(
        string RunId,
        string? SessionId,
        string? RuntimeRunId);
}
