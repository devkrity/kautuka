using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class UpdateActivationWorker : BackgroundService
{
    private readonly IUpdateServiceIntegration hostIntegration;
    private readonly IUpdateStateStore stateStore;
    private readonly UpdateOperationHistoryStore operationStore;
    private readonly UpdateOptions options;
    private readonly ILogger<UpdateActivationWorker> logger;

    public UpdateActivationWorker(
        IUpdateServiceIntegration hostIntegration,
        IUpdateStateStore stateStore,
        UpdateOperationHistoryStore operationStore,
        IOptions<UpdateOptions> options,
        ILogger<UpdateActivationWorker> logger)
    {
        this.hostIntegration = hostIntegration;
        this.stateStore = stateStore;
        this.operationStore = operationStore;
        this.options = options.Value;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            UpdateServiceContext? context = null;
            try
            {
                context = await hostIntegration.GetHostContextAsync(stoppingToken).ConfigureAwait(false);
                if (!options.Enabled && !context.RecoveryRequired)
                {
                    return;
                }

                bool resumed = await hostIntegration.TryContinuePendingActivationAsync(stoppingToken).ConfigureAwait(false);
                if (resumed)
                {
                    logger.LogInformation("Resumed pending update activation after UpdateService restart.");
                }

                context = await hostIntegration.GetHostContextAsync(stoppingToken).ConfigureAwait(false);
                if (!context.RecoveryRequired)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                try
                {
                    context = await hostIntegration.GetHostContextAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception observeException)
                {
                    logger.LogWarning(observeException, "Could not observe recovery fencing after pending activation failure.");
                }

                if (context?.RecoveryRequired != true)
                {
                    await PersistPendingActivationFailureAsync(ex).ConfigureAwait(false);
                    logger.LogWarning(ex, "Pending update activation startup attempt failed.");
                    return;
                }

                logger.LogWarning(ex, "Safe recovery is still fenced; UpdateService will retry the recovery handoff.");
            }

            if (context?.RecoveryRequired != true)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task PersistPendingActivationFailureAsync(Exception exception)
    {
        UpdateStateRecord state = await stateStore.GetAsync(CancellationToken.None).ConfigureAwait(false);
        if (state.State != UpdateWorkflowState.Applying || state.StagedRelease is null)
        {
            return;
        }

        string errorMessage = $"Update preflight checks failed: {exception.Message}";
        UpdateStateRecord failedState = state with
        {
            State = UpdateWorkflowState.Available,
            AvailableRelease = state.AvailableRelease ?? state.StagedRelease,
            LastError = errorMessage,
            StateChangedAtUtc = DateTimeOffset.UtcNow,
        };

        await stateStore.SaveAsync(failedState, CancellationToken.None).ConfigureAwait(false);
        await operationStore.AppendAsync(
            new UpdateOperationRecord(
                OperationType: "apply",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                FromVersion: state.CurrentRelease?.Version,
                ToVersion: state.StagedRelease.Version,
                Succeeded: false,
                ErrorMessage: errorMessage),
            CancellationToken.None).ConfigureAwait(false);
    }
}
