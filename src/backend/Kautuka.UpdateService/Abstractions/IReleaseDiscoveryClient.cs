using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Abstractions;

public interface IReleaseDiscoveryClient
{
    Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken);
}
