using Kautuka.Protos;

namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelOutboundAssetUploadService
{
    Task<UploadOutboundAssetResponse> UploadAsync(
        string sessionId,
        string ownerUserId,
        string assetId,
        string attachmentId,
        string conversationId,
        string? conversationThreadId,
        CancellationToken cancellationToken);
}
