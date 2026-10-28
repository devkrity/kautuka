namespace Kautuka.Gateway.Domain.Autopilot;

internal interface IAutopilotRunExecutionService
{
    Task StartAndExecuteAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken);
}
