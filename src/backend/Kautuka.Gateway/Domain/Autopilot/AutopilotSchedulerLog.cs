namespace Kautuka.Gateway.Domain.Autopilot;

internal static partial class AutopilotSchedulerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Auto-Pilot schedule evaluation failed for {ScheduleId}")]
    public static partial void ScheduleEvaluationFailed(ILogger logger, Exception exception, string scheduleId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Auto-Pilot startup reconciliation marked {Count} non-terminal runs as stale")]
    public static partial void ReconciledZombieRuns(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Auto-Pilot {Operation} failed")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Auto-Pilot {Operation} failed for run or request {Id}")]
    public static partial void RunFailed(ILogger logger, Exception exception, string operation, string id);
}
