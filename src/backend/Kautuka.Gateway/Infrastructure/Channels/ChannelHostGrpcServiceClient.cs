using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Kautuka.ChannelHost.Protos;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

/// <summary>
/// AOT-compatible gRPC client for the ChannelHost service.
/// Wraps the proto-first generated <see cref="ChannelHostService.ChannelHostServiceClient"/>.
/// </summary>
internal sealed class ChannelHostGrpcServiceClient : IChannelHostGrpcService
{
    private readonly ChannelHostService.ChannelHostServiceClient client;
    private readonly string channelHostAddress;
    private readonly Func<X509Certificate2?> delegationSigningCertificate;
    private readonly TimeProvider timeProvider;

    public ChannelHostGrpcServiceClient(
        CallInvoker callInvoker,
        string channelHostAddress,
        X509Certificate2? delegationSigningCertificate = null,
        TimeProvider? timeProvider = null)
        : this(callInvoker, channelHostAddress, () => delegationSigningCertificate, timeProvider)
    {
    }

    /// <summary>
    /// Creates a client whose delegation certificate is resolved only when an authenticated
    /// ChannelHost operation is made. This preserves the mTLS/delegation boundary while
    /// allowing Gateway to start before its sidecar has materialised local certificate files.
    /// </summary>
    public ChannelHostGrpcServiceClient(
        CallInvoker callInvoker,
        string channelHostAddress,
        Func<X509Certificate2?> delegationSigningCertificate,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(callInvoker);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelHostAddress);
        ArgumentNullException.ThrowIfNull(delegationSigningCertificate);

        client = new ChannelHostService.ChannelHostServiceClient(callInvoker);
        this.channelHostAddress = channelHostAddress;
        this.delegationSigningCertificate = delegationSigningCertificate;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default)
    {
        request.Context ??= new ScopedRequestContext
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = "gateway-system",
            TenantId = "default",
            SubmittedAtUtc = timeProvider.GetUtcNow().ToString("O"),
            GatewaySessionId = "gateway-channel-health",
        };
        Sign(request, request.Context, "channels.read");
        return await InvokeUnaryAsync(
            "Ping",
            ct => client.PingAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.read");
        return await InvokeUnaryAsync(
            "ListProviders",
            ct => client.ListProvidersAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.read");
        return await InvokeUnaryAsync(
            "GetAttachments",
            ct => client.GetAttachmentsAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.read");
        return await InvokeUnaryAsync(
            "GetAttachment",
            ct => client.GetAttachmentAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.read");
        return await InvokeUnaryAsync(
            "GetAttachmentSchema",
            ct => client.GetAttachmentSchemaAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.write");
        return await InvokeUnaryAsync(
            "UpsertAttachment",
            ct => client.UpsertAttachmentAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.write");
        return await InvokeUnaryAsync(
            "SyncOwnerTargets",
            ct => client.SyncOwnerTargetsAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.write");
        return await InvokeUnaryAsync(
            "ProvisionAttachment",
            ct => client.ProvisionAttachmentAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.write");
        return await InvokeUnaryAsync(
            "DeactivateAttachment",
            ct => client.DeactivateAttachmentAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TransitionAttachmentLifecycleResponse> TransitionAttachmentLifecycleAsync(
        TransitionAttachmentLifecycleRequest request,
        CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.write");
        return await InvokeUnaryAsync(
            "TransitionAttachmentLifecycle",
            ct => client.TransitionAttachmentLifecycleAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.runtime");
        return await InvokeUnaryAsync(
            "SendOutboundMessage",
            ct => client.SendOutboundMessageAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(
        ReadInboundMediaRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.runtime");
        using AsyncServerStreamingCall<ReadInboundMediaChunk> call = client.ReadInboundMedia(
            request,
            headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(),
            cancellationToken: cancellationToken);

        await foreach (ReadInboundMediaChunk chunk in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(
        ChannelIngressSubscribeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Sign(request, request.Context, "channels.ingress");
        // DateTime.MaxValue signals "no deadline" — DeadlineCallInvoker must not inject
        // its 30-second default on a long-lived server-streaming subscription.
        using AsyncServerStreamingCall<ChannelInboundEventMessage> call = client.StreamIngress(
            request,
            headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(),
            deadline: DateTime.MaxValue,
            cancellationToken: cancellationToken);

        await foreach (ChannelInboundEventMessage msg in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    public async ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.ingress");
        return await InvokeUnaryAsync(
            "AcknowledgeIngress",
            ct => client.AcknowledgeIngressAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        request.IdempotencyKey = DefaultIdempotencyKey(request.IdempotencyKey, request.Context);
        Sign(request, request.Context, "channels.runtime");
        return await InvokeUnaryAsync(
            "SetRuntimeCredentials",
            ct => client.SetRuntimeCredentialsAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: ct).ResponseAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private static string DefaultIdempotencyKey(string current, ScopedRequestContext? context)
        => string.IsNullOrWhiteSpace(current) ? context?.RequestId ?? Guid.NewGuid().ToString("N") : current;

    public async ValueTask<UploadOutboundAssetResponse> UploadOutboundAssetAsync(
        UploadOutboundAssetMetadata metadata,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);
        var first = new UploadOutboundAssetRequest { Metadata = metadata };
        Sign(first, metadata.Context, "channels.runtime");
        using AsyncClientStreamingCall<UploadOutboundAssetRequest, UploadOutboundAssetResponse> call = client.UploadOutboundAsset(
            headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(first, cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await call.RequestStream.WriteAsync(new UploadOutboundAssetRequest
            {
                Content = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
            }, cancellationToken).ConfigureAwait(false);
        }
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        return await call.ResponseAsync.ConfigureAwait(false);
    }

    private void Sign(Google.Protobuf.IMessage request, ScopedRequestContext? context, string operation)
    {
        if (context is null) return;

        X509Certificate2? signingCertificate = delegationSigningCertificate();
        if (signingCertificate is null) return;

        ChannelDelegationEnvelope.Sign(request, context, operation, signingCertificate, timeProvider.GetUtcNow());
    }

    private async ValueTask<TResponse> InvokeUnaryAsync<TResponse>(
        string methodName,
        Func<CancellationToken, Task<TResponse>> operation,
        CancellationToken cancellationToken)
    {
        using Activity? activity = AgentRunHostCallTracer.StartGrpcClientActivity(
            KautukaActivitySources.Component.ChannelHost,
            methodName,
            channelHostAddress);

        try
        {
            TResponse response = await operation(cancellationToken).ConfigureAwait(false);
            AgentRunHostCallTracer.MarkSuccess(activity);
            return response;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            AgentRunHostCallTracer.MarkFailure(activity, ex);
            throw new InvalidOperationException(
                $"ChannelHost gRPC endpoint '{channelHostAddress}' is unavailable. Ensure Kautuka.ChannelHost is running.", ex);
        }
        catch (Exception ex)
        {
            AgentRunHostCallTracer.MarkFailure(activity, ex);
            throw;
        }
    }
}
