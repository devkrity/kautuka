namespace Kautuka.ChannelContracts.Channels;

/// <summary>Outbound ChannelHost action type strings used on the Gateway→ChannelHost envelope.</summary>
public static class ChannelOutboundActionTypes
{
    /// <summary>Canonical runtime event fan-out. Payload is <see cref="ChannelRuntimeEventAction"/>.</summary>
    public const string RuntimeEvent = "runtime_event";

    public const string SendSemanticMessage = "send_semantic_message";
    public const string EditSemanticMessage = "edit_semantic_message";
    public const string StreamMessage = "stream_message";
    public const string EditMessageText = "edit_message_text";
}

/// <summary>Gateway→ChannelHost envelope for a single canonical runtime event intended for channel delivery.</summary>
public sealed record ChannelRuntimeEventAction(
    string EventId,
    string EventType,
    string PayloadJson,
    string OwnerUserId,
    string RegistrationId,
    string SessionId,
    string RootRunId,
    string RunId,
    string? ConversationId,
    string? ConversationThreadId,
    string? AgentId = null,
    string? InteractionId = null,
    string? AgentDisplayName = null,
    string? SessionDisplayName = null,
    string? ReplyToMessageId = null,
    string? PromptSessionId = null,
    string? PromptRootRunId = null,
    string? PromptText = null,
    string? AgentIcon = null,
    string? AgentColor = null);
