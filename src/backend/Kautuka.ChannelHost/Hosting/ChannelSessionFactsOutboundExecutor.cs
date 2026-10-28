using System.Text.Json;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Hosting;

internal sealed partial class ChannelSessionFactsOutboundExecutor(
    ChannelAttachmentStore attachmentStore,
    ChannelSessionFactsViewStore viewStore,
    ILogger<ChannelSessionFactsOutboundExecutor> logger)
{
    public async Task<SendOutboundMessageResponse> ExecuteAsync(
        string attachmentId,
        IChannelAdapter adapter,
        SendOutboundMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Content.ActionType != "session_facts")
            return Rejected("invalid_session_facts_action");

        ChannelSessionFactsEnvelope facts;
        try
        {
            facts = JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)
                ?? throw new JsonException("Session facts are empty.");
        }
        catch (JsonException)
        {
            return Rejected("invalid_session_facts");
        }

        string? owner = attachmentStore.GetAttachmentOwner(attachmentId);
        if (string.IsNullOrWhiteSpace(owner)) return Rejected("attachment_not_found");
        using IDisposable gate = await viewStore.EnterAsync(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, cancellationToken).ConfigureAwait(false);
        ChannelSessionFactsViewStore.ViewState? previous = viewStore.Read(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId);
        if (previous is not null && facts.Revision <= previous.Revision)
            return Delivered("superseded_session_facts", previous.MessageId);

        if (facts.View == "queue" && facts.QueueFacts!.TotalCount <= 1)
        {
            if (previous?.ViewMode == "queue" && !string.IsNullOrWhiteSpace(previous.MessageId))
            {
                viewStore.QueueDeletion(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, previous.MessageId);
                bool deleted = await TryDeleteAsync(attachmentId, adapter, request, facts.ConversationId,
                    previous.MessageId, cancellationToken).ConfigureAwait(false);
                if (!deleted) return Rejected("queue_card_delete_failed");
                viewStore.CompleteDeletion(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, previous.MessageId);
            }
            viewStore.Delivered(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, facts.Revision, string.Empty, "hidden");
            return Delivered("queue_hidden_below_threshold", string.Empty);
        }

        string? editId = facts.BringToFront || string.IsNullOrWhiteSpace(previous?.MessageId) ? null : previous.MessageId;
        ChannelSemanticMessageAction semantic = ChannelSessionFactsRenderer.Render(request.Content.PayloadJson, editId);
        SendOutboundMessageRequest delivery = request.Clone();
        delivery.Content.ActionType = editId is null ? ChannelOutboundActionTypes.SendSemanticMessage : ChannelOutboundActionTypes.EditSemanticMessage;
        delivery.Content.PayloadJson = JsonSerializer.Serialize(semantic, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        SendOutboundMessageResponse response = await adapter.SendOutboundMessageAsync(attachmentId, delivery, cancellationToken).ConfigureAwait(false);
        if (!response.Accepted && editId is not null && response.Detail == "message_not_found")
        {
            semantic = semantic with { TransportMessageId = null };
            delivery = request.Clone();
            delivery.Content.ActionType = ChannelOutboundActionTypes.SendSemanticMessage;
            delivery.Content.PayloadJson = JsonSerializer.Serialize(semantic, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
            response = await adapter.SendOutboundMessageAsync(attachmentId, delivery, cancellationToken).ConfigureAwait(false);
            editId = null;
        }
        if (!response.Accepted) return response;

        string? deliveredId = string.IsNullOrWhiteSpace(response.TransportMessageId) ? editId : response.TransportMessageId;
        if (deliveredId is not null)
        {
            bool queueCard = facts.View == "queue" && facts.QueueFacts!.TotalCount > 1;
            viewStore.Delivered(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, facts.Revision,
                deliveredId, queueCard ? "queue" : facts.View,
                queueCard ? DateTimeOffset.UtcNow.AddSeconds(90) : null);
            if (previous?.MessageId is { Length: > 0 } previousId && previousId != deliveredId)
            {
                // Cards are temporary chat UI, never pinned UI.
                viewStore.QueueDeletion(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, previousId);
                if (await TryDeleteAsync(attachmentId, adapter, request, facts.ConversationId, previousId, cancellationToken).ConfigureAwait(false))
                    viewStore.CompleteDeletion(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, previousId);
            }
        }
        else
        {
            viewStore.Delivered(owner, attachmentId, facts.ConversationId, facts.ConversationThreadId, facts.Revision,
                string.Empty, facts.View == "queue" ? "hidden" : facts.View);
        }
        return response;
    }

    public async Task<int> ExpireQueueCardsAsync(IEnumerable<IChannelAdapter> adapters, CancellationToken cancellationToken)
    {
        IChannelAdapter[] providerAdapters = adapters.ToArray();
        int deletedCount = 0;
        foreach (ChannelSessionFactsViewStore.ExpiredQueueCard candidate in viewStore.ListExpiredQueueCards(DateTimeOffset.UtcNow))
        {
            string? providerKey = attachmentStore.GetAttachmentProvider(candidate.AttachmentId);
            IChannelAdapter? adapter = providerAdapters.FirstOrDefault(candidateAdapter =>
                string.Equals(candidateAdapter.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) continue;
            using IDisposable gate = await viewStore.EnterAsync(candidate.Owner, candidate.AttachmentId, candidate.ChatId,
                candidate.TopicId == "root" ? null : candidate.TopicId, cancellationToken).ConfigureAwait(false);
            ChannelSessionFactsViewStore.ViewState? current = viewStore.Read(candidate.Owner, candidate.AttachmentId,
                candidate.ChatId, candidate.TopicId == "root" ? null : candidate.TopicId);
            if (current is null || current.Revision != candidate.Revision || current.ViewMode != "queue"
                || current.MessageId != candidate.MessageId || current.ExpiresAtUtc is null
                || !DateTimeOffset.TryParse(current.ExpiresAtUtc, out DateTimeOffset expiresAt) || expiresAt > DateTimeOffset.UtcNow)
                continue;

            SendOutboundMessageRequest request = new()
            {
                AttachmentId = candidate.AttachmentId,
                MessageId = $"queue-expiry:{candidate.Revision}:{candidate.MessageId}",
                Content = new OutboundContentEnvelope { ActionType = "delete_message" },
            };
            if (!await TryDeleteAsync(candidate.AttachmentId, adapter, request, candidate.ChatId, candidate.MessageId, cancellationToken).ConfigureAwait(false))
                continue;
            viewStore.CompleteDeletion(candidate.Owner, candidate.AttachmentId, candidate.ChatId,
                candidate.TopicId == "root" ? null : candidate.TopicId, candidate.MessageId);
            viewStore.Delivered(candidate.Owner, candidate.AttachmentId, candidate.ChatId,
                candidate.TopicId == "root" ? null : candidate.TopicId, candidate.Revision, string.Empty, "expired");
            deletedCount++;
        }
        deletedCount += await RetryCardDeletionsAsync(providerAdapters, cancellationToken).ConfigureAwait(false);
        return deletedCount;
    }

    private async Task<int> RetryCardDeletionsAsync(IReadOnlyCollection<IChannelAdapter> adapters, CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        foreach (ChannelSessionFactsViewStore.PendingCardDeletion candidate in viewStore.ListPendingDeletions())
        {
            string? providerKey = attachmentStore.GetAttachmentProvider(candidate.AttachmentId);
            IChannelAdapter? adapter = adapters.FirstOrDefault(item =>
                string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) continue;
            SendOutboundMessageRequest request = new()
            {
                AttachmentId = candidate.AttachmentId,
                MessageId = $"card-cleanup:{candidate.MessageId}",
                Content = new OutboundContentEnvelope { ActionType = "delete_message" },
            };
            if (!await TryDeleteAsync(candidate.AttachmentId, adapter, request, candidate.ChatId,
                candidate.MessageId, cancellationToken).ConfigureAwait(false)) continue;
            viewStore.CompleteDeletion(candidate.Owner, candidate.AttachmentId, candidate.ChatId,
                candidate.TopicId == "root" ? null : candidate.TopicId, candidate.MessageId);
            deletedCount++;
        }
        return deletedCount;
    }

    public async Task<SendOutboundMessageResponse> ExecuteLegacyViewAsync(
        string attachmentId,
        IChannelAdapter adapter,
        SendOutboundMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Content.ActionType is not ("queue_view" or "session_view" or "session_picker_view"))
            return Rejected("invalid_channel_view_action");
        ChannelSemanticMessageAction action;
        try
        {
            action = JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)
                ?? throw new JsonException("Channel view is empty.");
        }
        catch (JsonException)
        {
            return Rejected("invalid_channel_view");
        }
        if (action.Revision is not > 0) return Rejected("channel_view_revision_missing");
        string? owner = attachmentStore.GetAttachmentOwner(attachmentId);
        if (string.IsNullOrWhiteSpace(owner)) return Rejected("attachment_not_found");
        using IDisposable gate = await viewStore.EnterAsync(owner, attachmentId, action.ConversationId, action.ConversationThreadId, cancellationToken).ConfigureAwait(false);
        ChannelSessionFactsViewStore.ViewState? previous = viewStore.Read(owner, attachmentId, action.ConversationId, action.ConversationThreadId);
        if (previous is not null && action.Revision.Value <= previous.Revision)
            return Delivered("superseded_channel_view", previous.MessageId);

        bool replace = action.BringToFront;
        string? editId = replace || string.IsNullOrWhiteSpace(previous?.MessageId) ? null : previous.MessageId;
        ChannelSemanticMessageAction semantic = action with { MessageKey = null, Revision = null, TransportMessageId = editId };
        SendOutboundMessageRequest delivery = request.Clone();
        delivery.Content.ActionType = editId is null ? ChannelOutboundActionTypes.SendSemanticMessage : ChannelOutboundActionTypes.EditSemanticMessage;
        delivery.Content.PayloadJson = JsonSerializer.Serialize(semantic, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        SendOutboundMessageResponse response = await adapter.SendOutboundMessageAsync(attachmentId, delivery, cancellationToken).ConfigureAwait(false);
        if (!response.Accepted && editId is not null && response.Detail == "message_not_found")
        {
            editId = null;
            delivery = request.Clone();
            delivery.Content.ActionType = ChannelOutboundActionTypes.SendSemanticMessage;
            delivery.Content.PayloadJson = JsonSerializer.Serialize(semantic with { TransportMessageId = null }, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
            response = await adapter.SendOutboundMessageAsync(attachmentId, delivery, cancellationToken).ConfigureAwait(false);
        }
        if (!response.Accepted) return response;
        string? deliveredId = string.IsNullOrWhiteSpace(response.TransportMessageId) ? editId : response.TransportMessageId;
        if (deliveredId is null)
        {
            viewStore.Delivered(owner, attachmentId, action.ConversationId, action.ConversationThreadId, action.Revision.Value, string.Empty);
            return response;
        }
        viewStore.Delivered(owner, attachmentId, action.ConversationId, action.ConversationThreadId, action.Revision.Value, deliveredId);
        if (replace && previous?.MessageId is { Length: > 0 } previousMessageId && previousMessageId != deliveredId)
        {
            viewStore.QueueDeletion(owner, attachmentId, action.ConversationId, action.ConversationThreadId, previousMessageId);
            if (await TryDeleteAsync(attachmentId, adapter, request, action.ConversationId, previousMessageId, cancellationToken).ConfigureAwait(false))
                viewStore.CompleteDeletion(owner, attachmentId, action.ConversationId, action.ConversationThreadId, previousMessageId);
        }
        return response;
    }

    private Task<bool> TryDeleteAsync(string attachmentId, IChannelAdapter adapter, SendOutboundMessageRequest request,
        string conversationId, string messageId, CancellationToken cancellationToken)
    {
        SendOutboundMessageRequest delete = request.Clone();
        delete.MessageId += ":delete-previous-card:" + messageId;
        delete.IdempotencyKey += ":delete-previous-card:" + messageId;
        delete.Content.ActionType = "delete_message";
        delete.Content.PayloadJson = JsonSerializer.Serialize(new ChannelMessageDeleteAction(conversationId, messageId), ChannelContractsJsonContext.Default.ChannelMessageDeleteAction);
        return RunMaintenanceActionAsync("delete", attachmentId, adapter, delete, cancellationToken);
    }

    private async Task<bool> RunMaintenanceActionAsync(string operation, string attachmentId, IChannelAdapter adapter,
        SendOutboundMessageRequest action, CancellationToken cancellationToken)
    {
        try
        {
            SendOutboundMessageResponse response = await adapter.SendOutboundMessageAsync(attachmentId, action, cancellationToken).ConfigureAwait(false);
            if (!response.Accepted && operation == "delete"
                && (response.Detail?.Contains("message_not_found", StringComparison.OrdinalIgnoreCase) == true
                    || response.Detail?.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase) == true))
                return true;
            if (!response.Accepted)
                MaintenanceActionRejected(logger, operation, attachmentId, response.Detail ?? response.DeliveryStatus.ToString());
            return response.Accepted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or JsonException)
        {
            MaintenanceActionFailed(logger, ex, operation, attachmentId);
            return false;
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Telegram session card {Operation} failed for attachment {AttachmentId}: {Detail}")]
    private static partial void MaintenanceActionRejected(ILogger logger, string operation, string attachmentId, string detail);

    [LoggerMessage(2, LogLevel.Warning, "Telegram session card {Operation} failed for attachment {AttachmentId}")]
    private static partial void MaintenanceActionFailed(ILogger logger, Exception exception, string operation, string attachmentId);

    private static async Task IgnoreAsync(Task<SendOutboundMessageResponse> operation)
    {
        try { _ = await operation.ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or JsonException)
        {
            // The primary state card has already been delivered; cleanup is best effort.
        }
    }

    private static SendOutboundMessageResponse Rejected(string detail)
        => new() { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = detail };

    private static SendOutboundMessageResponse Delivered(string detail, string? messageId)
        => new() { Accepted = true, DeliveryStatus = DeliveryStatus.Delivered, Detail = detail, TransportMessageId = messageId ?? string.Empty };
}
