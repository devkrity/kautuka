namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelConversationMapStore
{
    private static readonly string[] supportedChannelTypes = ["telegram", "slack"];
    ChannelConversationMapEntry GetOrCreatePermanentTopic(
        string channelType,
        string registrationId,
        string targetType,
        string targetId,
        string providerConversationId,
        string providerTopicId,
        string? sessionId);

    ChannelConversationMapEntry CreateTransientThread(
        string channelType,
        string registrationId,
        string targetType,
        string targetId,
        string providerConversationId,
        string providerThreadId,
        string? sessionId,
        DateTimeOffset expiresAtUtc);

    ChannelConversationMapEntry? TryResolve(
        string channelType,
        string registrationId,
        string providerConversationId,
        string providerThreadId);

    ChannelConversationMapEntry? TryResolveBySession(string sessionId, string channelType);

    IReadOnlyList<ChannelConversationMapEntry> ListBySession(string sessionId)
        => supportedChannelTypes.Select(type => TryResolveBySession(sessionId, type)).OfType<ChannelConversationMapEntry>().ToArray();

    ChannelConversationMapEntry? TryResolveLatestByRegistration(string channelType, string registrationId);

    IReadOnlyList<ChannelConversationMapEntry> ListAll() => [];

    ChannelConversationMapEntry? AttachSession(
        string entryId,
        string sessionId,
        DateTimeOffset? expiresAtUtc = null);

    int CleanupExpiredTransientThreads(DateTimeOffset nowUtc);
}

internal sealed record ChannelConversationMapEntry(
    string Id,
    string ChannelType,
    string RegistrationId,
    string TargetType,
    string TargetId,
    string ProviderConversationId,
    string ProviderThreadId,
    string ConversationKind,
    string? SessionId,
    string CreatedAt,
    string UpdatedAt,
    string? ExpiresAt);
