using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Abstractions;

public interface IUpdateCoordinator
{
    ValueTask<UpdateStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusSnapshot> ApplyUpdatePolicyAsync(string releaseChannel, CancellationToken cancellationToken = default);

    Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusSnapshot> ApplyPreparedReleaseAsync(CancellationToken cancellationToken = default);

    ValueTask<UpdateStatusSnapshot> RevertToLastKnownGoodAsync(CancellationToken cancellationToken = default);

    ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken = default);
}
