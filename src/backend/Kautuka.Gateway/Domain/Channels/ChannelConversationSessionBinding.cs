namespace Kautuka.Gateway.Domain.Channels;

internal static class ChannelConversationSessionBinding
{
    public static ChannelConversationMapEntry BindSelectedSession(
        IChannelConversationMapStore maps,
        GatewayChannelRegistration registration,
        string chatId,
        string? topicId,
        string targetType,
        string targetId,
        string sessionId,
        bool replaceExisting = true)
    {
        string key = ChannelThreadRouting.ConversationKey(registration, chatId, topicId, targetType, targetId);
        // Preserve the selected session while promoting a legacy root-chat map into the
        // target-scoped key. Never borrow a map from another target or conversation.
        if (!replaceExisting && ChannelThreadRouting.ProviderTopic(chatId, topicId) is null
            && maps.TryResolve(registration.ChannelType, registration.Id, chatId, chatId) is { } legacy
            && ChannelThreadRouting.MatchesTarget(legacy, targetType, targetId)
            && !string.IsNullOrWhiteSpace(legacy.SessionId)) sessionId = legacy.SessionId;
        ChannelConversationMapEntry map = maps.GetOrCreatePermanentTopic(
            registration.ChannelType, registration.Id, targetType, targetId, chatId, key, sessionId);
        return (!replaceExisting && !string.IsNullOrWhiteSpace(map.SessionId)) || string.Equals(map.SessionId, sessionId, StringComparison.Ordinal)
            ? map : maps.AttachSession(map.Id, sessionId) ?? throw new InvalidOperationException("Channel session binding could not be saved.");
    }
}
