namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task<IReadOnlyList<string>> CreateChannelInboundAssetsAsync(
        string registrationId,
        string userId,
        string sessionId,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        List<string> assetIds = [];

        foreach (Kautuka.ChannelContracts.Channels.ChannelInboundMediaDescriptor media in envelope.InboundMedia)
        {
            ChannelInboundMediaContent content = await ReadInboundMediaContentAsync(registrationId, sessionId, media, envelope.UpdateId, userId, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(content.Content, writable: false);
            var asset = await assetStore.CreateUploadAsync(
                sessionId,
                userId,
                new Kautuka.Gateway.Domain.Assets.AssetUploadCommand(
                    content.FileName,
                    content.ContentType,
                    content.SizeBytes,
                    "userUpload",
                    "inlinePreview",
                    stream),
                cancellationToken).ConfigureAwait(false);
            assetIds.Add(asset.Id);
        }

        return assetIds;
    }

    private async Task<ChannelInboundMediaContent> ReadInboundMediaContentAsync(
        string registrationId,
        string sessionId,
        Kautuka.ChannelContracts.Channels.ChannelInboundMediaDescriptor media,
        string eventId,
        string userId,
        CancellationToken cancellationToken)
    {
        var request = new Kautuka.Protos.ReadInboundMediaRequest
        {
            Context = new Kautuka.Protos.ScopedRequestContext
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ActorUserId = userId,
                SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                GatewaySessionId = sessionId,
            },
            RegistrationId = registrationId,
            EventId = eventId,
            MediaId = media.MediaId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"{registrationId}:{eventId}:{media.MediaId}",
        };
        request.Context.ActorCapabilities.Add("channels.runtime");

        using var content = new MemoryStream();
        string? fileName = media.FileName;
        string? contentType = media.ContentType;
        long? sizeBytes = media.SizeBytes;

        await foreach (Kautuka.Protos.ReadInboundMediaChunk chunk in channelHost.ReadInboundMediaAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(chunk.FileName))
            {
                fileName = chunk.FileName;
            }

            if (!string.IsNullOrWhiteSpace(chunk.ContentType))
            {
                contentType = chunk.ContentType;
            }

            if (chunk.SizeBytes > 0)
            {
                sizeBytes = chunk.SizeBytes;
            }

            if (chunk.Content.Length > 0)
            {
                await content.WriteAsync(chunk.Content.Memory, cancellationToken).ConfigureAwait(false);
            }
        }

        if (content.Length == 0)
        {
            throw new InvalidOperationException("Inbound media fetch returned no content.");
        }

        string resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? $"{media.MediaKind}-{media.MediaId}.bin" : fileName;
        string resolvedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        return new ChannelInboundMediaContent(
            resolvedFileName,
            resolvedContentType,
            sizeBytes ?? content.Length,
            content.ToArray());
    }

    private sealed record ChannelInboundMediaContent(
        string FileName,
        string ContentType,
        long SizeBytes,
        byte[] Content);
}
