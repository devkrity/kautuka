using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

/// <summary>
/// Abstraction over the ChannelHost gRPC service used by Gateway workers.
/// Implemented by <see cref="ChannelHostGrpcServiceClient"/>.
/// </summary>
internal interface IChannelHostGrpcService
{
    ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default);
    ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default);
    ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default);
    ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default);
    ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default);
    ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<TransitionAttachmentLifecycleResponse> TransitionAttachmentLifecycleAsync(
        TransitionAttachmentLifecycleRequest request,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<TransitionAttachmentLifecycleResponse>(new NotSupportedException("ChannelHost lifecycle transitions are not implemented by this client."));
    ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, CancellationToken cancellationToken = default);
    ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default);
    ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default);
    ValueTask<UploadOutboundAssetResponse> UploadOutboundAssetAsync(
        UploadOutboundAssetMetadata metadata,
        Stream content,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<UploadOutboundAssetResponse>(new NotSupportedException("ChannelHost asset upload is not implemented by this client."));
}
