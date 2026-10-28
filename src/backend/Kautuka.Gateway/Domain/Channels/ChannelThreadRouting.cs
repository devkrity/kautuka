using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Channels;

/// <summary>Separates internal conversation keys from provider topic ids.</summary>
internal static class ChannelThreadRouting
{
    private const string topicTargetPrefix = "topic-target:";
    private static readonly string[] topicMapKeys = ["target_threads", "retired_target_threads"];

    public static bool IsManagedTopic(GatewayChannelRegistration registration, string? topicId)
        => !string.IsNullOrWhiteSpace(topicId)
            && topicMapKeys.Any(key =>
                registration.Settings[key] is JsonObject map
                && map.Any(entry => string.Equals(entry.Value?.ToString(), topicId, StringComparison.Ordinal)));

    public static bool HasManagedTargetThreads(GatewayChannelRegistration registration)
        => topicMapKeys.Any(key => registration.Settings[key] is JsonObject { Count: > 0 });

    public static bool MatchesTarget(ChannelConversationMapEntry map, string targetType, string targetId)
        => string.Equals(map.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(map.TargetId, targetId, StringComparison.Ordinal);

    public static string? FindTargetTopic(GatewayChannelRegistration registration, string targetType, string targetId)
    {
        if (registration.Settings["target_threads"] is not JsonObject threads) return null;
        foreach ((string key, JsonNode? value) in threads)
        {
            string[] parts = key.Split(':', 2);
            if (parts.Length == 2
                && string.Equals(parts[0], targetType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[1], targetId, StringComparison.Ordinal))
                return value?.ToString();
        }
        return null;
    }

    public static string? ProviderTopic(string conversationId, string? threadId)
    {
        if (threadId?.StartsWith(topicTargetPrefix, StringComparison.Ordinal) is true)
        {
            string[] parts = threadId.Split(':', 4);
            if (parts.Length != 4) return null;
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); }
            catch (FormatException) { return null; }
        }
        return string.IsNullOrWhiteSpace(threadId)
            || string.Equals(threadId, "root", StringComparison.OrdinalIgnoreCase)
            || string.Equals(threadId, conversationId, StringComparison.Ordinal)
            || threadId.StartsWith("task:", StringComparison.Ordinal)
            || threadId.StartsWith("target:", StringComparison.Ordinal)
                ? null : threadId;
    }

    public static string? OutboundTopic(GatewayChannelRegistration registration, string conversationId, string? threadId, string targetType, string targetId)
    {
        // Telegram's private-chat UX no longer uses topics. Legacy topic mappings remain
        // only for ChannelHost cleanup and must not scope messages or callback tokens.
        if (string.Equals(registration.ChannelType, "telegram", StringComparison.OrdinalIgnoreCase)) return null;
        return ProviderTopic(conversationId, threadId) ?? FindTargetTopic(registration, targetType, targetId);
    }

    public static string ConversationKey(GatewayChannelRegistration registration, string conversationId, string? providerThreadId, string targetType, string targetId)
    {
        if (providerThreadId?.StartsWith("task:", StringComparison.Ordinal) is true) return providerThreadId;
        string? topic = OutboundTopic(registration, conversationId, providerThreadId, targetType, targetId);
        if (topic is not null)
        {
            // A provider conversation that is not bound to a target follows the selected
            // target, so one user's agents cannot share a session mapping accidentally.
            if (!IsManagedTopic(registration, topic))
                return $"{topicTargetPrefix}{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(topic))}:{targetType.ToUpperInvariant()}:{targetId}";
            return topic;
        }
        return $"target:{targetType.ToUpperInvariant()}:{targetId}";
    }
}
