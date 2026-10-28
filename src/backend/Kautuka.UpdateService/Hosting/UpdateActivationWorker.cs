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
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            bool resumed = await hostIntegration.TryContinuePendingActivationAsync(stoppingToken).ConfigureAwait(false);
            if (resumed)
            {
                logger.LogInformation("Resumed pending update activation after UpdateService restart.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await PersistPendingActivationFailureAsync(ex).ConfigureAwait(false);
            logger.LogWarning(ex, "Pending update activation startup attempt failed.");
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
