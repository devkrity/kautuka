namespace Kautuka.Gateway.Domain.Autopilot;

internal static class AutopilotTargetTypes
{
    public const string Agent = "agent";
    public const string Workflow = "workflow";
}

internal static class AutopilotScheduleModes
{
    public const string Off = "off";
    public const string On = "on";
    public const string NeedsApproval = "needs_approval";
}

internal static class AutopilotRunStatuses
{
    public const string Scheduled = "scheduled";
    public const string AwaitingPreRunApproval = "awaiting_pre_run_approval";
    public const string Running = "running";
    public const string WaitingForInRunApproval = "waiting_for_in_run_approval";
    public const string Completed = "completed";
    public const string NoOp = "no_op";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string TimedOut = "timed_out";
    public const string Skipped = "skipped";
    public const string Stale = "stale";

    public static string GetStateCategory(string status) =>
        status switch
        {
            AwaitingPreRunApproval or WaitingForInRunApproval => AutopilotRunStateCategories.PendingApproval,
            Scheduled or Running => AutopilotRunStateCategories.Executing,
            Completed or NoOp or Failed or Rejected or TimedOut or Skipped or Stale => AutopilotRunStateCategories.Terminal,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown Auto-Pilot run status."),
        };

    public static bool IsTerminal(string status) =>
        string.Equals(GetStateCategory(status), AutopilotRunStateCategories.Terminal, StringComparison.Ordinal);
}

internal static class AutopilotRunStateCategories
{
    public const string PendingApproval = "pending_approval";
    public const string Executing = "executing";
    public const string Terminal = "terminal";
}

internal static class AutopilotPreRunApprovalExpiryPolicies
{
    public const string NextOccurrence = "next_occurrence";
    public const string Duration = "duration";
    public const string Never = "never";
}

internal static class AutopilotOverlapPolicies
{
    public const string SkipAndNotify = "skip_and_notify";
    public const string SkipSilently = "skip_silently";
}

internal static class AutopilotLaunchPolicy
{
    public const string PreRunToolCallId = "autopilot_pre_run";
    public const string PreRunToolName = "autopilot_pre_run";
    public const string PreRunToolType = "autopilot_prerun";

    public static bool RequiresPreRunApproval(GatewayAutopilotSchedule schedule)
        => schedule.RequiresPreRunApproval
            || string.Equals(schedule.Mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal);
}

internal sealed record GatewayAutopilotSchedule(
    string Id,
    string OwnerUserId,
    string TargetType,
    string TargetId,
    string CronExpression,
    string TimeZoneId,
    string Mode,
    bool RequiresPreRunApproval,
    string SchedulePromptContext,
    string[] WorkspaceFilePaths,
    bool NotifyUiInbox,
    bool NotifyChannels,
    string PreRunApprovalExpiryPolicy,
    int? PreRunApprovalTimeoutSeconds,
    string OverlapPolicy,
    bool IsPaused,
    string CreatedAt,
    string UpdatedAt,
    string? NextDueAt,
    string? LastEvaluatedAt,
    string? LeaseOwner,
    string? LeaseExpiresAt);

internal sealed record GatewayScheduledRun(
    string Id,
    string ScheduleId,
    string OwnerUserId,
    string TargetType,
    string TargetId,
    string Status,
    string TriggerType,
    string ScheduledFor,
    string? SessionId,
    string? RuntimeRunId,
    string? PreRunApprovalRequestId,
    string? InputJson,
    string? OutputJson,
    string? Error,
    string StateCategory,
    string? SkipReason,
    string? BlockingRunId,
    string? BlockingRunStatusSnapshot,
    string? BlockingRunStateCategory,
    string? BlockedAt,
    string CreatedAt,
    string UpdatedAt,
    string? StartedAt,
    string? EndedAt);

internal sealed record GatewayAutopilotSuppression(
    string Id,
    string ScheduleId,
    string SuppressedAt,
    string Reason,
    string? ExistingRunId,
    string? Note);

internal sealed record GatewayAutopilotNotification(
    string Id,
    string ScheduledRunId,
    string OwnerUserId,
    string ChannelType,
    string Destination,
    string Status,
    string? PayloadJson,
    string CreatedAt,
    string? SentAt,
    string? Error,
    bool IsRead,
    string? ReadAt);
