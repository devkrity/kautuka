using Kautuka.Protos;

namespace Kautuka.ChannelHost.Hosting;

internal interface IChannelAdapter
{
    string ProviderKey { get; }
    string DisplayName { get; }
    string SchemaVersion { get; }
    string CapabilityVersion { get; }
    ChannelProviderCapabilities Capabilities { get; }

    IReadOnlyList<AttachmentSchemaField> GetSchema();
    Task<ChannelProviderHealth> GetHealthAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(CancellationToken cancellationToken);
    Task<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken);
    Task<ProvisionAttachmentResponse> ProvisionAttachmentAsync(string attachmentId, ProvisionAttachmentRequest request, CancellationToken cancellationToken);
    Task<SendOutboundMessageResponse> SendOutboundMessageAsync(string attachmentId, SendOutboundMessageRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(string attachmentId, ReadInboundMediaRequest request, CancellationToken cancellationToken);
    Task RunAsync(CancellationToken cancellationToken);
}
