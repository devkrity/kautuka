using Kautuka.Gateway.Domain.Autopilot;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayAutopilotStore
{
    GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule);

    bool UpdateSchedule(GatewayAutopilotSchedule schedule);

    bool DeleteSchedule(string scheduleId, string ownerUserId);

    GatewayAutopilotSchedule? GetSchedule(string scheduleId);

    IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId);

    IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset);

    IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit);

    GatewayScheduledRun InsertRun(GatewayScheduledRun run);

    bool TryInsertRunUnlessNonTerminal(GatewayScheduledRun run, out GatewayScheduledRun? blockingRun)
    {
        blockingRun = GetLatestNonTerminalRunBySchedule(run.ScheduleId);
        if (blockingRun is not null)
        {
            return false;
        }

        InsertRun(run);
        blockingRun = null;
        return true;
    }

    GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId);

    GatewayScheduledRun? GetLatestNonTerminalRunBySchedule(string scheduleId)
        => ListNonTerminalRuns(1000)
            .Where(run => string.Equals(run.ScheduleId, scheduleId, StringComparison.Ordinal))
            .OrderByDescending(run => run.CreatedAt, StringComparer.Ordinal)
            .FirstOrDefault();

    GatewayScheduledRun? GetRunById(string runId);

    GatewayScheduledRun? GetRunByRuntimeRunId(string runtimeRunId) => null;

    GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId);

    IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit);

    IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit);

    bool TransitionRunState(
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
        bool markEnded);

    bool UpdateRunMetadata(
        string runId,
        string? sessionId,
        string? runtimeRunId,
        string? preRunApprovalRequestId,
        string? outputJson,
        string? error,
        DateTimeOffset nowUtc);

    bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc);

    IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset);

    void RecordSuppression(GatewayAutopilotSuppression suppression);

    void RecordNotification(GatewayAutopilotNotification notification);

    IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly);

    bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc);
}
