using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

/// <summary>
/// Polls the SQLite outbox for pending channel intents and delivers each one
/// to ChannelHost via attachment-based send RPCs and then acknowledges via
/// <see cref="IChannelOutboxStore.Acknowledge"/>.
/// </summary>
internal sealed partial class ChannelHostOutboxWorker(
    IChannelHostGrpcService channelHost,
    IChannelOutboxStore outbox,
    IChannelAttachmentRegistry attachments,
    GatewayState state,
    ILogger<ChannelHostOutboxWorker> logger,
    ChannelIngressService? channelIngress = null) : BackgroundService
{
    private const string workerId = "gateway-outbox";
    private const int batchSize = 16;
    private static readonly TimeSpan leaseDuration = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan idleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan errorDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan rateLimitDelay = TimeSpan.FromMinutes(1);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Outbox worker must continue processing subsequent actions even if one action or ack path throws unexpectedly.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogOutboxWorkerStarted(logger);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunIterationAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    // ChannelHost unavailable
                    LogOutboxWorkerChannelHostUnavailable(logger, ex);
                    await Task.Delay(errorDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (RpcException ex)
                {
                    LogOutboxWorkerRpcError(logger, ex.Status.StatusCode, ex.Status.Detail);
                    await Task.Delay(RetryDelay(ex), stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogOutboxWorkerUnhandledError(logger, ex);
                    await Task.Delay(errorDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        LogOutboxWorkerStopped(logger);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-action failures must be contained so the worker can continue delivering queued channel messages.")]
    private async Task RunIterationAsync(CancellationToken ct)
    {
        IReadOnlyList<ChannelOutboxEntry> batch = outbox.LeasePending(
            workerId, batchSize, leaseDuration, DateTimeOffset.UtcNow);

        if (batch.Count == 0)
        {
            await Task.Delay(idleDelay, ct).ConfigureAwait(false);
            return;
        }

        foreach (ChannelOutboxEntry entry in batch)
        {
            ct.ThrowIfCancellationRequested();

            _ = attachments.TryGetRegistration(entry.RegistrationId, out GatewayChannelRegistration? registration);
            string? ownerUserId = entry.OwnerUserId;
            if (string.IsNullOrWhiteSpace(ownerUserId)
                && registration is not null
                && string.Equals(registration.Id, entry.RegistrationId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(registration.OwnerUserId))
            {
                ownerUserId = registration.OwnerUserId;
            }

            if (string.IsNullOrWhiteSpace(ownerUserId))
            {
                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, false,
                    "missing_owner_binding", DateTimeOffset.UtcNow, null, maxAttempts: 1);
                continue;
            }

            if (registration is null || !registration.IsActive)
            {
                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, false,
                    "attachment_not_active", DateTimeOffset.UtcNow, null, maxAttempts: 1);
                continue;
            }

            using IDisposable scope = logger.BeginKautukaCorrelationScope(
                ("WorkerId", workerId),
                ("ActionId", entry.Id),
                ("AttachmentId", entry.RegistrationId),
                ("ActionType", entry.ActionType));

            (string conversationId, string conversationThreadId) = ReadConversationBinding(entry.PayloadJson);
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                conversationId = ReadConfiguredConversationBinding(registration);
            }
            SendOutboundMessageRequest request = new()
            {
                Context = BuildOwnerContext(ownerUserId),
                AttachmentId = entry.RegistrationId,
                MessageId = entry.Id,
                RequestedAtUtc = entry.CreatedAt,
                // The request digest includes CorrelationId. Keep it stable with the
                // durable action id so a retry after an ambiguous transport failure is
                // recognized by ChannelHost as the same idempotent operation.
                CorrelationId = entry.Id,
                // A lease covers a batch, so it cannot identify an individual side effect.
                // The durable action id is stable across retries and unique per outbound intent.
                IdempotencyKey = entry.Id,
                ConversationId = conversationId,
                ConversationThreadId = conversationThreadId,
                Content = new OutboundContentEnvelope
                {
                    ActionType = entry.ActionType,
                    PayloadJson = entry.PayloadJson,
                },
            };

            LogOutboxIntentDelivering(logger, entry.Id, entry.ActionType);

            if (IsProgressiveAssistantTextDelta(entry))
            {
                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, true,
                    "filtered_progressive_delta", DateTimeOffset.UtcNow, null);
                continue;
            }

            try
            {
                using Activity? dispatchScope = AgentRunHostCallTracer.StartBackgroundDispatchScope(
                    "channelhost outbox dispatch",
                    entry.RunId,
                    entry.TraceParent,
                    entry.TraceState);

                SendOutboundMessageResponse result = entry.ActionType is "session_facts" or "queue_view" or "session_view" or "session_picker_view"
                    ? await channelHost.SendOutboundMessageAsync(request, ct).ConfigureAwait(false)
                    : await DeliverRuntimeEventAsync(entry, registration, request, ct).ConfigureAwait(false);

                LogOutboxIntentDelivered(logger, entry.Id, result.Accepted, result.Detail ?? string.Empty);

                // Apply side effects from successful delivery
                if (result.Accepted)
                {
                    ApplyTransportMessageId(entry.RegistrationId, result.TransportMessageId, entry.PayloadJson);
                    await SyncResolvedApprovalAfterDeliveryAsync(entry, ct).ConfigureAwait(false);
                    RecordSessionActivity(entry, registration, ownerUserId);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                bool permanentFailure = result.DeliveryStatus == DeliveryStatus.Rejected;
                DateTimeOffset? retryAt = result.Accepted || permanentFailure
                    ? null
                    : now.AddSeconds(3);

                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, result.Accepted,
                    string.IsNullOrEmpty(result.Detail) ? null : result.Detail, now, retryAt,
                    maxAttempts: permanentFailure ? 1 : 10);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException ex)
            {
                LogOutboxIntentDeliveryFailed(logger, entry.Id, ex);
                bool permanentAuthorizationFailure = ex.Status.StatusCode is StatusCode.PermissionDenied
                    or StatusCode.Unauthenticated
                    or StatusCode.FailedPrecondition;
                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, false, ex.Message, DateTimeOffset.UtcNow,
                    permanentAuthorizationFailure ? null : DateTimeOffset.UtcNow.Add(RetryDelay(ex)),
                    maxAttempts: permanentAuthorizationFailure ? 1
                        : IsChannelRateLimit(ex) ? int.MaxValue
                        : 10);
                continue;
            }
            catch (InvalidOperationException ex)
            {
                LogOutboxIntentDeliveryFailed(logger, entry.Id, ex);
                outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, false, ex.Message, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddSeconds(3));
                continue;
            }
            catch (Exception ex)
            {
                LogOutboxIntentUnhandledFailure(logger, entry.Id, ex);
                try
                {
                    outbox.Acknowledge(entry.Id, workerId, entry.LeaseId ?? string.Empty, false, ex.Message, DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddSeconds(3));
                }
                catch (Exception ackEx)
                {
                    LogOutboxAcknowledgeFailed(logger, entry.Id, ackEx);
                }
            }
        }
    }

    private static TimeSpan RetryDelay(RpcException exception)
        => exception.StatusCode is StatusCode.ResourceExhausted
            ? rateLimitDelay
            : errorDelay;

    private static bool IsChannelRateLimit(RpcException exception)
        => exception.StatusCode is StatusCode.ResourceExhausted
            && string.Equals(exception.Status.Detail, "channel_rate_limit_exceeded", StringComparison.Ordinal);

    private static bool IsProgressiveAssistantTextDelta(ChannelOutboxEntry entry)
    {
        if (!string.Equals(entry.ActionType, ChannelOutboundActionTypes.RuntimeEvent, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(entry.PayloadJson);
            return document.RootElement.TryGetProperty("eventType", out JsonElement eventType)
                && string.Equals(
                    eventType.GetString(),
                    ChannelNaradProjectionPayload.WireEventTypes.AssistantTextDelta,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (string ConversationId, string ConversationThreadId) ReadConversationBinding(string payloadJson)
    {
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payloadJson);
            string conversation = document.RootElement.TryGetProperty("conversationId", out System.Text.Json.JsonElement conversationElement)
                ? conversationElement.GetString() ?? string.Empty : string.Empty;
            string thread = document.RootElement.TryGetProperty("conversationThreadId", out System.Text.Json.JsonElement threadElement)
                ? threadElement.GetString() ?? string.Empty : string.Empty;
            return (conversation, thread);
        }
        catch (System.Text.Json.JsonException) { return (string.Empty, string.Empty); }
    }

    private static string ReadConfiguredConversationBinding(GatewayChannelRegistration? registration)
    {
        if (registration?.Settings.TryGetPropertyValue("channel_device_id", out System.Text.Json.Nodes.JsonNode? value) == true
            && value is System.Text.Json.Nodes.JsonValue jsonValue
            && jsonValue.TryGetValue(out string? conversationId)
            && !string.IsNullOrWhiteSpace(conversationId))
        {
            return conversationId;
        }

        return string.Empty;
    }

    private void ApplyTransportMessageId(string registrationId, string? transportMessageId, string payloadJson)
    {
        if (string.IsNullOrEmpty(transportMessageId))
        {
            return;
        }

        // Couples the provider message id back onto GatewayChannelInteraction via payload
        // interactionId. Works for legacy send_semantic_message and runtime_event envelopes
        // (ChannelRuntimeEventAction.InteractionId serializes as camelCase interactionId).
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("interactionId", out JsonElement idEl))
            {
                string? interactionId = idEl.GetString();
                if (!string.IsNullOrEmpty(interactionId)
                    && state.ChannelInteractions.TryGetValue(interactionId, out GatewayChannelInteraction? interaction))
                {
                    long? sourceMessageId = long.TryParse(transportMessageId, out long parsed) ? parsed : null;
                    state.UpsertChannelInteraction(interaction with
                    {
                        SourceMessageId = sourceMessageId,
                        SourceTransportMessageId = transportMessageId,
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            LogOutboxTransportIdApplyFailed(logger, registrationId, ex);
        }
    }

    private async Task SyncResolvedApprovalAfterDeliveryAsync(ChannelOutboxEntry entry, CancellationToken cancellationToken)
    {
        if (!string.Equals(entry.ActionType, ChannelOutboundActionTypes.RuntimeEvent, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            ChannelRuntimeEventAction? runtimeEvent = JsonSerializer.Deserialize(
                entry.PayloadJson,
                ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
            if (channelIngress is not null
                && runtimeEvent is { EventType: ChannelNaradProjectionPayload.WireEventTypes.ToolApprovalRequested, InteractionId.Length: > 0 } request)
            {
                await channelIngress.SyncResolvedChannelApprovalAfterDeliveryAsync(request.InteractionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            LogOutboxTransportIdApplyFailed(logger, entry.RegistrationId, ex);
        }
    }

    private static ScopedRequestContext BuildOwnerContext(string? ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId)) throw new InvalidOperationException("Channel outbox entry has no owner binding.");
        ScopedRequestContext context = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = ownerUserId,
            TenantId = "default",
            SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            GatewaySessionId = "gateway-outbox",
        };
        context.ActorCapabilities.Add("channels.runtime");
        return context;
    }

    [LoggerMessage(1, LogLevel.Information, "ChannelHostOutboxWorker started")]
    private static partial void LogOutboxWorkerStarted(ILogger logger);

    [LoggerMessage(2, LogLevel.Information, "ChannelHostOutboxWorker stopped")]
    private static partial void LogOutboxWorkerStopped(ILogger logger);

    [LoggerMessage(3, LogLevel.Warning, "ChannelHost is unavailable; outbox worker backing off")]
    private static partial void LogOutboxWorkerChannelHostUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(4, LogLevel.Warning, "ChannelHostOutboxWorker RPC error. status={StatusCode} detail={Detail}")]
    private static partial void LogOutboxWorkerRpcError(ILogger logger, StatusCode statusCode, string detail);

    [LoggerMessage(9, LogLevel.Error, "ChannelHostOutboxWorker unhandled error; backing off")]
    private static partial void LogOutboxWorkerUnhandledError(ILogger logger, Exception ex);

    [LoggerMessage(5, LogLevel.Debug, "Delivering intent. actionId={ActionId} actionType={ActionType}")]
    private static partial void LogOutboxIntentDelivering(ILogger logger, string actionId, string actionType);

    [LoggerMessage(6, LogLevel.Information, "Intent delivered. actionId={ActionId} success={Success} error={Error}")]
    private static partial void LogOutboxIntentDelivered(ILogger logger, string actionId, bool success, string error);

    [LoggerMessage(7, LogLevel.Warning, "Intent delivery failed. actionId={ActionId}")]
    private static partial void LogOutboxIntentDeliveryFailed(ILogger logger, string actionId, Exception ex);

    [LoggerMessage(8, LogLevel.Warning, "Failed to apply transport message id for registration={RegistrationId}")]
    private static partial void LogOutboxTransportIdApplyFailed(ILogger logger, string registrationId, Exception ex);

    [LoggerMessage(10, LogLevel.Error, "Intent processing hit unhandled exception. actionId={ActionId}")]
    private static partial void LogOutboxIntentUnhandledFailure(ILogger logger, string actionId, Exception ex);

    [LoggerMessage(11, LogLevel.Error, "Failed to acknowledge outbox action after exception. actionId={ActionId}")]
    private static partial void LogOutboxAcknowledgeFailed(ILogger logger, string actionId, Exception ex);

}
