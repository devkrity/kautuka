namespace Kautuka.Gateway.Domain.Autopilot;

internal interface IAutopilotNotificationDispatcher
{
    Task DispatchNotificationsAsync(
        GatewayScheduledRun run,
        string scheduleId,
        string? startedMessage,
        string? finalMessage,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
