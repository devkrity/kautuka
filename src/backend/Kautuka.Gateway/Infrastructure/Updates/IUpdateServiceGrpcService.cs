using Kautuka.UpdateService.Protos;

namespace Kautuka.Gateway.Infrastructure.Updates;

internal interface IUpdateServiceGrpcService
{
    Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
    Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
    Task<UpdateStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
    Task<UpdateStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default);
    Task<UpdateStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
    Task<UpdateStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
    Task<UpdateStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default);
}
