namespace Kautuka.Gateway.Domain.Autopilot;

internal interface IAutopilotRunLaunchService
{
    IReadOnlyList<GatewayToolApprovalRequest> ListPendingForRun(GatewayScheduledRun run);

    Task LaunchAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken);

    Task<bool> RemindPendingAsync(
        GatewayAutopilotSchedule schedule,
        GatewayScheduledRun run,
        CancellationToken cancellationToken);
}
