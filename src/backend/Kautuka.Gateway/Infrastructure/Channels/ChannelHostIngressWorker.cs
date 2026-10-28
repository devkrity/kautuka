using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

/// <summary>
/// Subscribes to the ChannelHost ingress stream (<see cref="IChannelHostGrpcService.StreamIngressAsync"/>)
/// and feeds received events into <see cref="IChannelIngressService"/>.
/// </summary>
internal sealed partial class ChannelHostIngressWorker(
    IChannelHostGrpcService channelHost,
    IChannelIngressService ingress,
    IChannelAttachmentRegistry attachments,
    GatewayState state,
    ILogger<ChannelHostIngressWorker> logger) : BackgroundService
{
    private static readonly TimeSpan errorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan unavailableDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogIngressWorkerStarted(logger);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunIngressStreamAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    // gRPC stream internally canceled (connection reset, ChannelHost unavailable) — not a shutdown signal.
                    LogIngressWorkerRpcError(logger, StatusCode.Unknown, ex.Message);
                    await Task.Delay(errorDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    LogIngressWorkerChannelHostUnavailable(logger, ex);
                    await Task.Delay(unavailableDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (RpcException ex)
                {
                    LogIngressWorkerRpcError(logger, ex.Status.StatusCode, ex.Status.Detail);
                    await Task.Delay(errorDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        LogIngressWorkerStopped(logger);
    }

    private async Task RunIngressStreamAsync(CancellationToken ct)
    {
        await attachments.RefreshUsersAsync(state.Users.Values, ct).ConfigureAwait(false);
        ChannelIngressSubscribeRequest request = new()
        {
            SubscriberId = "gateway-ingress",
            Context = BuildContext("channels.ingress"),
        };
        LogIngressStreamConnecting(logger);
        await foreach (ChannelInboundEventMessage msg in channelHost.StreamIngressAsync(request, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            // Prompts are admitted durably and approval decisions enqueue their own
            // continuations. Keep these short state changes ordered on ingress.
            await ProcessAndAcknowledgeAsync(msg, request.SubscriberId, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessAndAcknowledgeAsync(ChannelInboundEventMessage msg, string subscriberId, CancellationToken ct)
    {
        if (!await ProcessInboundMessageAsync(msg, ct).ConfigureAwait(false)) return;
        _ = await channelHost.AcknowledgeIngressAsync(new AcknowledgeIngressRequest
        {
            SubscriberId = subscriberId,
            EventId = msg.EventId,
            DeliveryLeaseId = msg.DeliveryLeaseId,
            IdempotencyKey = $"ingress-ack:{msg.EventId}:{msg.DeliveryLeaseId}",
            Context = BuildContext("channels.ingress"),
        }, ct).ConfigureAwait(false);
    }

    private async Task<bool> ProcessInboundMessageAsync(ChannelInboundEventMessage msg, CancellationToken ct)
    {

        using IDisposable scope = logger.BeginKautukaCorrelationScope(
            ("EventId", msg.EventId),
            ("RegistrationId", msg.RegistrationId),
            ("EventType", msg.EventType),
            ("ConversationId", msg.ConversationId));

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(
                string.IsNullOrEmpty(msg.PayloadJson) ? "{}" : msg.PayloadJson).RootElement;
        }
        catch (JsonException)
        {
            payload = JsonDocument.Parse("{}").RootElement;
        }

        ChannelInboundEvent evt = new(
            msg.EventId,
            msg.ChannelType,
            msg.RegistrationId,
            NullIfEmpty(msg.OwnerUserId),
            msg.ExternalUserId,
            msg.ConversationId,
            NullIfEmpty(msg.ConversationThreadId),
            msg.EventType,
            NullIfEmpty(msg.Text),
            NullIfEmpty(msg.CallbackData),
            NullIfEmpty(msg.AcknowledgeToken),
            msg.SourceMessageId == 0 ? null : msg.SourceMessageId,
            payload,
            msg.InboundMedia.Select(static media => new ChannelInboundMediaDescriptor(
                media.MediaId,
                media.MediaKind,
                NullIfEmpty(media.FileName),
                NullIfEmpty(media.ContentType),
                media.SizeBytes == 0 ? null : media.SizeBytes,
                NullIfEmpty(media.ProviderUniqueId)))
                .ToArray(),
            NullIfEmpty(msg.SourceTransportMessageId),
            NullIfEmpty(msg.ReplySourceTransportMessageId));

        ChannelIngressResult result;
        try
        {
            result = await ingress.AdmitInboundEventAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let a single inbound message tear down the gateway Host (StopHost behavior).
            LogIngressEventFailed(logger, ex, msg.EventId, msg.RegistrationId);
            result = new ChannelIngressResult(false, "ingress_exception", ex.Message);
        }

        LogIngressEventProcessed(logger, msg.EventId, result.Success, result.Status);
        // An exception may mean the durable queue admission failed. Keep the host
        // delivery lease recoverable rather than acknowledging a lost prompt.
        return result.Status != "ingress_exception";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static ScopedRequestContext BuildContext(string capability)
    {
        ScopedRequestContext context = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = "gateway-system",
            SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            GatewaySessionId = "gateway-channel-ingress",
        };
        context.ActorCapabilities.Add(capability);
        return context;
    }

    [LoggerMessage(10, LogLevel.Information, "ChannelHostIngressWorker started")]
    private static partial void LogIngressWorkerStarted(ILogger logger);

    [LoggerMessage(11, LogLevel.Information, "ChannelHostIngressWorker stopped")]
    private static partial void LogIngressWorkerStopped(ILogger logger);

    [LoggerMessage(12, LogLevel.Warning, "ChannelHost unavailable; ingress worker backing off")]
    private static partial void LogIngressWorkerChannelHostUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(13, LogLevel.Warning, "ChannelHostIngressWorker RPC error. status={StatusCode} detail={Detail}")]
    private static partial void LogIngressWorkerRpcError(ILogger logger, StatusCode statusCode, string detail);

    [LoggerMessage(14, LogLevel.Information, "Connecting to ChannelHost ingress stream")]
    private static partial void LogIngressStreamConnecting(ILogger logger);

    [LoggerMessage(15, LogLevel.Debug, "Ingress event processed. eventId={EventId} success={Success} status={Status}")]
    private static partial void LogIngressEventProcessed(ILogger logger, string eventId, bool success, string status);

    [LoggerMessage(16, LogLevel.Error, "Ingress event failed without stopping host. eventId={EventId} registrationId={RegistrationId}")]
    private static partial void LogIngressEventFailed(ILogger logger, Exception exception, string eventId, string registrationId);
}
