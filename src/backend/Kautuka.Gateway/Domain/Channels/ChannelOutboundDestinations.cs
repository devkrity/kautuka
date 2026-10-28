namespace Kautuka.Gateway.Domain.Channels;

internal sealed record ChannelOutboundDestination(
    GatewayChannelRegistration Registration,
    string ConversationId,
    string? ConversationThreadId);

/// <summary>
/// Resolves the owner's linked chat destinations for a user-visible outbound message.
/// Preserve the exact source conversation, and mirror to each other provider's paired DM.
/// Provider-specific agent bots remain the destination for their own provider.
/// </summary>
internal static class ChannelOutboundDestinations
{
    public static string? ResolveExternalUserId(GatewayChannelRegistration registration, string conversationId)
        => string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
            ? registration.Settings.GetStringNodeValue("slack_owner_user_id")
            : registration.Settings.GetStringNodeValue("channel_device_id") ?? conversationId;

    public static ChannelOutboundDestination[] Resolve(
        IChannelAttachmentRegistry attachments,
        GatewayChannelRegistration source,
        string sourceConversationId,
        string? sourceThreadId)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConversationId);

        List<ChannelOutboundDestination> destinations =
        [new(source, sourceConversationId, sourceThreadId)];
        bool sourceIsChat = ChannelAttachmentIds.IsChat(source.Id);

        foreach (GatewayChannelRegistration linked in attachments.GetActiveChatRegistrations(source.OwnerUserId))
        {
            if (string.Equals(linked.Id, source.Id, StringComparison.Ordinal)) continue;
            if (!sourceIsChat && string.Equals(linked.ChannelType, source.ChannelType, StringComparison.OrdinalIgnoreCase)) continue;

            string? conversationId = string.Equals(linked.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
                ? linked.Settings.GetStringNodeValue("slack_dm_channel_id")
                : linked.Settings.GetStringNodeValue("channel_device_id");
            if (string.IsNullOrWhiteSpace(conversationId)) continue;

            destinations.Add(new(linked, conversationId, null));
        }

        return [.. destinations];
    }
}
