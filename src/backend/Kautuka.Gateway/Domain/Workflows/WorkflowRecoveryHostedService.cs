using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed class WorkflowRecoveryHostedService(
    IGatewayWorkflowStore workflowStore,
    IWorkflowExecutionService executionService,
    ILogger<WorkflowRecoveryHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<GatewayWorkflowInstance> instances;
        try
        {
            instances = workflowStore.ListRecoverableInstances();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkflowRecoveryLog.ListFailed(logger, exception);
            return Task.CompletedTask;
        }

        foreach (GatewayWorkflowInstance instance in instances)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                WorkflowRecoveryLog.Recovering(logger, instance.Id, instance.Status);
                if (string.Equals(instance.Status, WorkflowInstanceStatuses.Paused, StringComparison.Ordinal))
                {
                    continue;
                }

                executionService.Enqueue(instance.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                WorkflowRecoveryLog.RecoveryFailed(logger, instance.Id, exception);
            }
        }

        return Task.CompletedTask;
    }
}

internal static partial class WorkflowRecoveryLog
{
    [LoggerMessage(EventId = 7204, Level = LogLevel.Error, Message = "Could not list recoverable workflow instances; startup recovery will be skipped.")]
    public static partial void ListFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Information, Message = "Recovering workflow instance {InstanceId} in status {Status}.")]
    public static partial void Recovering(ILogger logger, string instanceId, string status);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Error, Message = "Could not recover workflow instance {InstanceId}; continuing with the next instance.")]
    public static partial void RecoveryFailed(ILogger logger, string instanceId, Exception exception);
}
