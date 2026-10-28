using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Abstractions;

public interface IUpdateServiceIntegration
{
    ValueTask<UpdateServiceContext> GetHostContextAsync(CancellationToken cancellationToken);

    ValueTask ActivatePreparedUpdateAsync(UpdatePreparedArtifacts preparedArtifacts, CancellationToken cancellationToken);

    ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken);

    ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken);

    ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken);
}
