using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelRoutingCoordinator
{
    ChannelConversationMapEntry ResolveConversationMap(string registrationId, GatewayChannelTargetBinding targetBinding, ChannelInboundEnvelope envelope);
    GatewayChannelTargetBinding? ResolveTargetBinding(GatewayChannelRegistration registration, string externalUserId, string? messageThreadId);
    (string TargetType, string TargetId) ResolveCurrentTargetSelection(GatewayChannelRegistration registration, string externalUserId);
    string ResolveTargetDisplayName(string targetType, string targetId);
    string? ResolveTargetColor(string targetType, string targetId);
    string BuildPickerLabel(string targetType, bool selected, string displayName);
    bool HasThreadTargetBinding(GatewayChannelRegistration registration, string? messageThreadId);
    string? ResolveTargetThreadId(GatewayChannelRegistration registration, string targetType, string targetId);
}

internal sealed class ChannelRoutingCoordinator(
    GatewayState state,
    IChannelAttachmentRegistry attachmentRegistry,
    IChannelConversationMapStore conversationMapStore) : IChannelRoutingCoordinator
{
    private static readonly TimeSpan transientThreadTtl = TimeSpan.FromHours(8);

    public ChannelConversationMapEntry ResolveConversationMap(
        string registrationId,
        GatewayChannelTargetBinding targetBinding,
        ChannelInboundEnvelope envelope)
    {
        if (!attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration) || registration is null)
            throw new InvalidOperationException("Channel registration is unavailable.");
        string providerThreadId = ChannelThreadRouting.ConversationKey(registration, envelope.ChatId, envelope.ProviderThreadId, targetBinding.TargetType, targetBinding.TargetId);
        ChannelConversationMapEntry? existing = conversationMapStore.TryResolve(registration.ChannelType, registrationId, envelope.ChatId, providerThreadId);
        bool persistent = !providerThreadId.StartsWith("task:", StringComparison.Ordinal);
        if (persistent && existing is not null && ChannelThreadRouting.MatchesTarget(existing, targetBinding.TargetType, targetBinding.TargetId))
            return conversationMapStore.GetOrCreatePermanentTopic(registration.ChannelType, registrationId,
                targetBinding.TargetType, targetBinding.TargetId, envelope.ChatId, providerThreadId, existing.SessionId);
        if (existing is not null && ChannelThreadRouting.MatchesTarget(existing, targetBinding.TargetType, targetBinding.TargetId)) return existing;

        // Preserve sessions from before target-scoped keys, only for this exact target.
        ChannelConversationMapEntry? legacy = providerThreadId != envelope.ProviderThreadId
            && !envelope.ProviderThreadId.StartsWith("task:", StringComparison.Ordinal)
                ? conversationMapStore.TryResolve(registration.ChannelType, registrationId, envelope.ChatId, envelope.ProviderThreadId)
                : null;
        string? sessionId = legacy is not null && ChannelThreadRouting.MatchesTarget(legacy, targetBinding.TargetType, targetBinding.TargetId)
            ? legacy.SessionId : null;
        if (persistent)
            return conversationMapStore.GetOrCreatePermanentTopic(registration.ChannelType, registrationId,
                targetBinding.TargetType, targetBinding.TargetId, envelope.ChatId, providerThreadId, sessionId);
        return conversationMapStore.CreateTransientThread(registration.ChannelType, registrationId,
            targetBinding.TargetType, targetBinding.TargetId, envelope.ChatId, providerThreadId,
            sessionId, DateTimeOffset.UtcNow.Add(transientThreadTtl));
    }

    public GatewayChannelTargetBinding? ResolveTargetBinding(GatewayChannelRegistration registration, string externalUserId, string? messageThreadId)
    {
        GatewayChannelTargetBinding? topicBinding = ResolveTargetBindingByThread(registration, messageThreadId);
        if (topicBinding is not null)
        {
            return topicBinding;
        }

        // Once target topics are configured, an explicit but unrecognized thread must
        // not silently inherit another target. This also protects against stale topic IDs
        // after a user deletes and recreates a managed topic.
        if (HasExplicitThread(messageThreadId)
            && (ChannelThreadRouting.IsManagedTopic(registration, messageThreadId)
                || ChannelThreadRouting.HasManagedTargetThreads(registration))) return null;

        (string activeTargetType, string activeTargetId) = ResolveCurrentTargetSelection(registration, externalUserId);
        if (!string.IsNullOrWhiteSpace(activeTargetId))
        {
            GatewayChannelTargetBinding? activeBinding = attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(item =>
                string.Equals(item.TargetType, activeTargetType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.TargetId, activeTargetId, StringComparison.Ordinal));

            if (activeBinding is not null)
            {
                return activeBinding;
            }
        }

        GatewayChannelTargetBinding? defaultBinding = attachmentRegistry.GetDefaultBinding(registration.Id);
        if (defaultBinding is not null)
        {
            return defaultBinding;
        }

        return attachmentRegistry.GetBindings(registration.Id).FirstOrDefault();
    }

    private GatewayChannelTargetBinding? ResolveTargetBindingByThread(GatewayChannelRegistration registration, string? messageThreadId)
    {
        if (!HasExplicitThread(messageThreadId))
        {
            return null;
        }

        if (registration.Settings["target_threads"] is not JsonObject threadMap)
        {
            return null;
        }

        foreach ((string key, JsonNode? value) in threadMap)
        {
            if (!string.Equals(value?.ToString(), messageThreadId, StringComparison.Ordinal))
            {
                continue;
            }

            string[] targetParts = key.Split(':', 2, StringSplitOptions.TrimEntries);
            if (targetParts.Length != 2)
            {
                return null;
            }

            string targetType = targetParts[0];
            string targetId = targetParts[1];
            return attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(item =>
                string.Equals(item.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.TargetId, targetId, StringComparison.Ordinal));
        }

        return null;
    }

    public bool HasThreadTargetBinding(GatewayChannelRegistration registration, string? messageThreadId)
        => ResolveTargetBindingByThread(registration, messageThreadId) is not null;

    public string? ResolveTargetThreadId(GatewayChannelRegistration registration, string targetType, string targetId)
        => ChannelThreadRouting.FindTargetTopic(registration, targetType, targetId);

    private static bool HasExplicitThread(string? messageThreadId)
        => !string.IsNullOrWhiteSpace(messageThreadId)
            && !string.Equals(messageThreadId, "root", StringComparison.OrdinalIgnoreCase);

    public (string TargetType, string TargetId) ResolveCurrentTargetSelection(GatewayChannelRegistration registration, string externalUserId)
    {
        string? storedTarget = GetActiveTargetForUser(registration, externalUserId);
        if (!string.IsNullOrWhiteSpace(storedTarget))
        {
            string[] targetParts = storedTarget.Split(':', 2, StringSplitOptions.TrimEntries);
            if (targetParts.Length == 2)
            {
                string targetType = targetParts[0].ToUpperInvariant();
                string targetId = targetParts[1];
                bool exists = attachmentRegistry.GetBindings(registration.Id).Any(item =>
                    string.Equals(item.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.TargetId, targetId, StringComparison.Ordinal));

                if (exists)
                {
                    return (targetType, targetId);
                }
            }
        }

        GatewayChannelTargetBinding? defaultBinding = attachmentRegistry.GetDefaultBinding(registration.Id);
        if (defaultBinding is not null)
        {
            return (defaultBinding.TargetType, defaultBinding.TargetId);
        }

        GatewayChannelTargetBinding? firstBinding = attachmentRegistry.GetBindings(registration.Id).FirstOrDefault();
        return firstBinding is null ? ("agent", string.Empty) : (firstBinding.TargetType, firstBinding.TargetId);
    }

    public string ResolveTargetDisplayName(string targetType, string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return "none";
        }

        return ResolveAgentDisplayName(targetId);
    }

    public string BuildPickerLabel(string targetType, bool selected, string displayName)
    {
        string prefix = "Agent";
        string check = selected ? " ✅" : string.Empty;
        return $"{prefix}: {displayName}{check}";
    }

    private string ResolveAgentDisplayName(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "none";
        }

        if (!state.Agents.TryGetValue(agentId, out GatewayAgent? agent) || string.IsNullOrWhiteSpace(agent?.Name))
            return "Assistant";

        string icon = GatewayPersonaProfileMapper.NormalizeIdentityIcon(agent.Persona?.GetStringNodeValue("icon"));
        return $"{icon} {agent.Name.Trim()}";
    }

    public string? ResolveTargetColor(string targetType, string targetId)
    {
        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            || !state.Agents.TryGetValue(targetId, out GatewayAgent? agent)
            || agent?.Persona is not JsonObject persona)
            return null;

        string? color = persona.GetStringNodeValue("color");
        return color is "#0F766E" or "#2563EB" or "#7C3AED" or "#DB2777" or "#EA580C" or "#16A34A"
            ? color
            : null;
    }

    private static string? GetActiveTargetForUser(GatewayChannelRegistration registration, string externalUserId)
    {
        if (registration.Settings["active_target_by_external_user"] is not JsonObject activeByUser)
        {
            return null;
        }

        return activeByUser[externalUserId]?.ToString();
    }

}
