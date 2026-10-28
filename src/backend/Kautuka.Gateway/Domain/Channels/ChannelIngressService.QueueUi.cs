using System.Globalization;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task<int> PublishBotQueueAsync(
        GatewayChannelRegistration registration,
        string owner,
        string chat,
        string? topic,
        int offset,
        bool onlyIfChanged,
        bool bringToFront,
        CancellationToken cancellationToken)
    {
        List<(ChannelQueueEntry Entry, long CreatedAt)> queued = [];
        GatewayChannelTargetBinding[] targets = attachmentRegistry.GetBindings(registration.Id)
            .DistinctBy(binding => (binding.TargetType, binding.TargetId)).ToArray();
        foreach (GatewayChannelTargetBinding target in targets)
        {
            foreach (QueuedSessionTurn turn in pendingTurns.ListTarget(owner, target.TargetId))
            {
                GatewaySession? session = AccessibleSession(owner, turn.SessionId, target);
                if (session is null) continue;
                string preview = JsonNode.Parse(turn.PayloadJson)?["message"]?.GetValue<string>()?.Replace('\n', ' ')
                    ?? "Message with attachments";
                if (preview.Length > 140) preview = preview[..137] + "…";
                queued.Add((new ChannelQueueEntry(turn.Id, turn.SessionId, 0, preview,
                    routingCoordinator.ResolveTargetDisplayName(target.TargetType, target.TargetId), SessionLabel(session),
                    routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId)), turn.Order));
            }
        }

        ChannelQueueEntry[] ordered = queued.OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Entry.TurnId, StringComparer.Ordinal)
            .Select((item, index) => item.Entry with { Order = index + 1 }).ToArray();
        await factPublisher.PublishBotQueueFactsAsync(registration, chat, topic, ordered, offset, onlyIfChanged,
            bringToFront, cancellationToken).ConfigureAwait(false);
        return ordered.Length;
    }

    private GatewaySession? AccessibleSession(string owner, string? sessionId, GatewayChannelTargetBinding target)
        => sessionId is not null && sessionStore.TryGetSession(sessionId, out GatewaySession? session)
            && session is not null && session.OwnerUserId == owner && session.TargetId == target.TargetId
            && session.TargetType == target.TargetType && session.ArchivedAt is null ? session : null;

    private static string SessionLabel(GatewaySession session)
    {
        string label = session.Title.Trim();
        return string.IsNullOrEmpty(label) ? "New chat" : label.Length > 90 ? label[..87] + "…" : label;
    }

    private async Task<ChannelIngressResult?> TryHandleQueueCallbackAsync(GatewayChannelRegistration registration,
        GatewayChannelIdentityLink identity, ChannelInboundEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.MessageText.StartsWith("q:", StringComparison.Ordinal) && !envelope.MessageText.StartsWith("ui:", StringComparison.Ordinal)) return null;
        string? topic = ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId);
        GatewayChannelTargetBinding? target = routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId);
        string scope = ChannelQueueViewStore.RegistrationScope(registration);
        ChannelQueueView? previousView = queueViews.Read(identity.UserId, scope + ":session", envelope.ChatId, topic);
        string message = "Queue refreshed.";
        GatewaySession? switchedSession = null;
        int offset = previousView?.PageOffset ?? 0;
        if (envelope.MessageText.StartsWith("ui:", StringComparison.Ordinal))
        {
            ChannelUiAction? action = queueViews.ReadAction(envelope.MessageText[3..], identity.UserId,
                scope, envelope.ChatId, topic);
            GatewaySession? session = null;
            if (action is not null && action.Kind is "remove" or "clear"
                && sessionStore.TryGetSession(action.SessionId, out GatewaySession? candidate)
                && candidate is not null && candidate.OwnerUserId == identity.UserId && candidate.ArchivedAt is null)
            {
                GatewayChannelTargetBinding? actionTarget = attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(binding =>
                    binding.TargetType == candidate.TargetType && binding.TargetId == candidate.TargetId);
                if (actionTarget is not null)
                {
                    target = actionTarget;
                    session = AccessibleSession(identity.UserId, action.SessionId, actionTarget);
                }
            }
            else
            {
                session = target is null || action is null ? null : AccessibleSession(identity.UserId, action.SessionId, target);
            }
            if (session is null || action is null) message = "This action expired or its session is unavailable. Use /sessions or /queue.";
            else if (action.Kind == "remove")
                message = pendingTurns.RemoveSessionWaiting(identity.UserId, session.Id, action.Value) == 1 ? "Removed from queue." : "That message is no longer waiting.";
            else if (action.Kind == "clear" && long.TryParse(action.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long through))
                message = $"Removed {pendingTurns.RemoveSessionWaiting(identity.UserId, session.Id, null, through)} waiting messages from {SessionLabel(session)}.";
            else if (action.Kind == "switch")
            {
                BindChannelConversationToSession(registration.Id, target!.TargetType, target.TargetId, envelope, session.Id);
                BindSelectedSessionAcrossLinkedChannels(registration, envelope,
                    target.TargetType, target.TargetId, session.Id);
                switchedSession = session;
                message = "Sending to: " + SessionLabel(session) + ".";
            }
        }
        else
        {
            string[] parts = envelope.MessageText.Split(':', 3);
            if (parts.Length == 3 && parts[1] == "view" && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int page)) offset = page;
            else if (envelope.MessageText == "q:home:0") message = "Session panel refreshed.";
            else message = "This older queue action is no longer available. Use /queue to refresh.";
        }
        await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, message, cancellationToken).ConfigureAwait(false);
        if (switchedSession is not null && target is not null)
        {
            string? messageId = envelope.CallbackTransportMessageId
                ?? envelope.CallbackMessageId?.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(messageId))
                await DeleteChannelMessageAsync(registration, envelope.ChatId, messageId, cancellationToken).ConfigureAwait(false);
            queueViews.Close(identity.UserId, ChannelQueueViewStore.RegistrationScope(registration) + ":session", envelope.ChatId, topic);
            string agentName = routingCoordinator.ResolveTargetDisplayName(target.TargetType, target.TargetId);
            await outboundMessagePublisher.PublishTextMessageAsync(new ChannelOutboundTextMessage(
                registration.ChannelType, registration.Id, envelope.ChatId, null,
                $"Now talking to {agentName}\nSession: {SessionLabel(switchedSession)}\nYour next message goes here."), cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "queue_updated", "Session selection changed.");
        }

        await PublishBotQueueAsync(registration, identity.UserId, envelope.ChatId, topic,
            offset,
            onlyIfChanged: false,
            bringToFront: false,
            cancellationToken).ConfigureAwait(false);
        return new ChannelIngressResult(true, "queue_updated", message);
    }
}
