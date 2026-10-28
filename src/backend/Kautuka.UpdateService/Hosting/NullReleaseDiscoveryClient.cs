using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Hosting;

internal sealed class NullReleaseDiscoveryClient(
    ILogger<NullReleaseDiscoveryClient> logger) : IReleaseDiscoveryClient
{
    private readonly ILogger<NullReleaseDiscoveryClient> logger = logger;

    public Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogDebug(
            "Update discovery is scaffolded but no release client is active yet. owner={Owner} repo={Repository} channel={Channel}",
            request.RepositoryOwner,
            request.RepositoryName,
            request.ReleaseChannel);

        return Task.FromResult(ReleaseDiscoveryResult.NoRelease());
    }
}
