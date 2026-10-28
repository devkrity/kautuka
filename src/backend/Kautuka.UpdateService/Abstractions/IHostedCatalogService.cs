using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Abstractions;

public interface IHostedCatalogService
{
    ValueTask<HostedCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task RefreshSnapshotAsync(CancellationToken cancellationToken = default);
}
