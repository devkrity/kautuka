using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.State;

internal interface IHostedCatalogStateStore
{
    ValueTask<HostedCatalogSnapshot> GetAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(HostedCatalogSnapshot snapshot, CancellationToken cancellationToken = default);
}
