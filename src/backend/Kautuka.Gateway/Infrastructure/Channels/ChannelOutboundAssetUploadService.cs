using System.Security.Cryptography;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

internal sealed class ChannelOutboundAssetUploadService(
    IConversationAssetStore assets,
    IChannelHostGrpcService channelHost) : IChannelOutboundAssetUploadService
{
    public async Task<UploadOutboundAssetResponse> UploadAsync(
        string sessionId,
        string ownerUserId,
        string assetId,
        string attachmentId,
        string conversationId,
        string? conversationThreadId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        IReadOnlyList<StoredConversationAsset> authorized = assets.GetAssetsByIds(sessionId, [assetId], ownerUserId);
        if (authorized.Count != 1
            || !string.Equals(authorized[0].SessionId, sessionId, StringComparison.Ordinal)
            || !string.Equals(authorized[0].OwnerUserId, ownerUserId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The outbound asset is outside the authenticated session or owner scope.");
        if (!assets.TryOpenAsset(sessionId, assetId, ownerUserId, out AssetDownloadResult? download) || download is null)
            throw new FileNotFoundException("The authorized outbound asset is unavailable.");

        using Stream content = download.Content;
        string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        if (!content.CanSeek) throw new InvalidOperationException("Outbound asset content must be replayable for verified upload.");
        content.Position = 0;
        var metadata = new UploadOutboundAssetMetadata
        {
            Context = new ScopedRequestContext
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ActorUserId = ownerUserId,
                TenantId = "default",
                GatewaySessionId = sessionId,
                SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            },
            AssetId = assetId,
            AttachmentId = attachmentId,
            ConversationId = conversationId,
            ConversationThreadId = conversationThreadId ?? string.Empty,
            SafeFileName = download.FileName,
            ContentType = download.ContentType,
            DeclaredSizeBytes = authorized[0].Public.SizeBytes,
            Sha256Hex = digest,
            IdempotencyKey = $"{attachmentId}:{sessionId}:{assetId}:{conversationId}",
        };
        return await channelHost.UploadOutboundAssetAsync(metadata, content, cancellationToken).ConfigureAwait(false);
    }
}
