using Grpc.Core;

using Kautuka.UpdateService.Protos;

using UpdateServiceGrpcContract = Kautuka.UpdateService.Protos.UpdateService;

namespace Kautuka.Gateway.Infrastructure.Updates;

internal sealed class UpdateServiceGrpcServiceClient(CallInvoker callInvoker) : IUpdateServiceGrpcService
{
    private readonly UpdateServiceGrpcContract.UpdateServiceClient client = new(callInvoker);

    public Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.PingAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.GetCatalogSnapshotAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<UpdateStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.GetUpdateStatusAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<UpdateStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.SetUpdatePolicyAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<UpdateStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.RefreshUpdateDiscoveryAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<UpdateStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.ApplyUpdateAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<UpdateStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        => Call(() => client.RevertUpdateAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    private static async Task<T> Call<T>(Func<Task<T>> invoke)
        => await invoke().ConfigureAwait(false);
}
