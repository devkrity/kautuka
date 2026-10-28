namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private void BindChannelConversationToSession(
        string registrationId,
        string targetType,
        string targetId,
        ChannelInboundEnvelope envelope,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(registrationId)
            || string.IsNullOrWhiteSpace(targetType)
            || string.IsNullOrWhiteSpace(targetId)
            || string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(envelope.ChatId))
        {
            return;
        }

        GatewayChannelTargetBinding? binding = attachmentRegistry.GetBindings(registrationId).FirstOrDefault(item =>
            string.Equals(item.TargetType, targetType, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TargetId, targetId, StringComparison.Ordinal));
        if (binding is null) return;
        ChannelConversationMapEntry map = routingCoordinator.ResolveConversationMap(registrationId, binding, envelope);
        _ = conversationMapStore.AttachSession(map.Id, sessionId,
            string.Equals(map.ConversationKind, "transient_thread", StringComparison.Ordinal) ? DateTimeOffset.UtcNow.Add(transientThreadTtl) : null);
    }

    private void BindSelectedSessionAcrossLinkedChannels(
        GatewayChannelRegistration source,
        ChannelInboundEnvelope sourceEnvelope,
        string targetType,
        string targetId,
        string sessionId)
    {
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachmentRegistry, source, sourceEnvelope.ChatId, sourceEnvelope.MessageThreadId))
        {
            if (destination.Registration.Id == source.Id) continue;
            string? externalUserId = ChannelOutboundDestinations.ResolveExternalUserId(
                destination.Registration, destination.ConversationId);
            if (string.IsNullOrWhiteSpace(externalUserId)
                || attachmentRegistry.GetBindings(destination.Registration.Id).All(binding =>
                    !string.Equals(binding.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(binding.TargetId, targetId, StringComparison.Ordinal)))
            {
                continue;
            }

            var mirrorEnvelope = new ChannelInboundEnvelope(
                UpdateId: "linked-session-" + Guid.NewGuid().ToString("N"),
                ExternalUserId: externalUserId,
                ChatId: destination.ConversationId,
                ProviderThreadId: destination.ConversationId,
                MessageThreadId: destination.ConversationThreadId,
                MessageText: string.Empty,
                IsCallback: false,
                CallbackAction: null,
                CallbackInteractionId: null,
                CallbackQueryId: null,
                CallbackMessageId: null,
                InboundMedia: []);
            BindChannelConversationToSession(destination.Registration.Id, targetType, targetId,
                mirrorEnvelope, sessionId);
        }
    }

}
