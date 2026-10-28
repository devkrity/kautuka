using System.Text.Json;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Hosting;

/// <summary>Shared adapter wiring for <see cref="ChannelOutboundActionTypes.RuntimeEvent"/> envelopes.</summary>
internal static class ChannelRuntimeEventOutboundExecutor
{
    public static async Task<SendOutboundMessageResponse> ExecuteGrpcAsync(
        JsonElement attachmentSettings,
        SendOutboundMessageRequest request,
        Func<SendOutboundMessageRequest, CancellationToken, Task<SendOutboundMessageResponse>> executeChild,
        CancellationToken cancellationToken,
        string? approvalTransportMessageId = null,
        bool richContext = false)
    {
        ChannelRuntimeEventAction? action = TryDeserialize(request.Content?.PayloadJson);
        if (action is null)
        {
            return new SendOutboundMessageResponse
            {
                Accepted = false,
                DeliveryStatus = DeliveryStatus.Rejected,
                Detail = "invalid_payload",
            };
        }

        IReadOnlyList<ChannelTranslatedOutboundAction> translated = ChannelRuntimeEventTranslator.Shared.Translate(
            attachmentSettings,
            action,
            approvalTransportMessageId,
            richContext);

        // Defense in depth for every provider (Telegram/Slack): runtime_event must never
        // fan progressive stream_message edits — only one-shot transport actions.
        translated = [.. translated.Where(static item =>
            !string.Equals(item.ActionType, ChannelOutboundActionTypes.StreamMessage, StringComparison.Ordinal))];

        if (translated.Count == 0)
        {
            return new SendOutboundMessageResponse
            {
                Accepted = true,
                DeliveryStatus = DeliveryStatus.Delivered,
                Detail = "filtered",
            };
        }

        SendOutboundMessageResponse last = new()
        {
            Accepted = true,
            DeliveryStatus = DeliveryStatus.Delivered,
            Detail = string.Empty,
        };

        foreach (ChannelTranslatedOutboundAction item in translated)
        {
            SendOutboundMessageRequest child = new()
            {
                AttachmentId = request.AttachmentId,
                MessageId = request.MessageId,
                ConversationId = request.ConversationId,
                ConversationThreadId = request.ConversationThreadId,
                RequestedAtUtc = request.RequestedAtUtc,
                CorrelationId = request.CorrelationId,
                IdempotencyKey = request.IdempotencyKey,
                Context = request.Context,
                Content = new OutboundContentEnvelope
                {
                    ActionType = item.ActionType,
                    PayloadJson = item.PayloadJson,
                },
            };

            last = await executeChild(child, cancellationToken).ConfigureAwait(false);
            if (!last.Accepted)
                return last;
        }

        return last;
    }

    public static ChannelRuntimeEventAction? TryDeserialize(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
