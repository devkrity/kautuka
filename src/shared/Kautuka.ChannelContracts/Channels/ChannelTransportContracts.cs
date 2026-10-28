using System.Text.Json;

namespace Kautuka.ChannelContracts.Channels;

public sealed record ChannelInboundMediaDescriptor(
    string MediaId,
    string MediaKind,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? ProviderUniqueId = null);

public sealed record ChannelInboundEvent(
    string EventId,
    string ChannelType,
    string RegistrationId,
    string? OwnerUserId,
    string ExternalUserId,
    string ConversationId,
    string? ConversationThreadId,
    string EventType,
    string? Text,
    string? CallbackData,
    string? AcknowledgeToken,
    long? SourceMessageId,
    JsonElement Payload,
    IReadOnlyList<ChannelInboundMediaDescriptor>? InboundMedia = null,
    string? SourceTransportMessageId = null,
    string? ReplySourceTransportMessageId = null);

public sealed record ChannelIngressAcceptedResponse(
    bool Success,
    string Status,
    string Detail,
    string? SessionId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? InteractionId = null,
    string? RunId = null);

public sealed record ChannelRuntimeRegistration(
    string ChannelType,
    string RegistrationId,
    string OwnerUserId,
    string Credential,
    JsonElement Settings);

public sealed record ChannelRuntimeRegistrationsResponse(
    IReadOnlyList<ChannelRuntimeRegistration> Registrations);

public sealed record ChannelOutboxAction(
    string Id,
    string ChannelType,
    string RegistrationId,
    string ActionType,
    string PayloadJson,
    string Status,
    int AttemptCount,
    string CreatedAt,
    string UpdatedAt,
    string? AvailableAt,
    string? LeaseId,
    string? LeaseOwner,
    string? LeasedUntil,
    string? LastError);

public sealed record ChannelOutboxLeaseRequest(
    string WorkerId,
    int MaxItems,
    int LeaseSeconds);

public sealed record ChannelOutboxLeaseResponse(
    IReadOnlyList<ChannelOutboxAction> Actions);

public sealed record ChannelOutboxAckRequest(
    string WorkerId,
    string LeaseId,
    IReadOnlyList<ChannelOutboxAckItem> Items);

public sealed record ChannelOutboxAckItem(
    string ActionId,
    bool Success,
    string? Error,
    int? RetryAfterSeconds);

public sealed record ChannelOutboxEnqueueRequest(
    string ChannelType,
    string RegistrationId,
    string ActionType,
    JsonElement Payload,
    int? DelaySeconds = null);

public sealed record ChannelTextMessageAction(
    string ConversationId,
    string? ConversationThreadId,
    string Text,
    string? ParseMode = null,
    string? SenderDisplayName = null);

public sealed record ChannelChatAction(
    string ConversationId,
    string? ConversationThreadId,
    string Action);

public sealed record ChannelInlineKeyboardButton(
    string Label,
    string CallbackData);

public sealed record ChannelInlineKeyboardRow(
    IReadOnlyList<ChannelInlineKeyboardButton> Buttons);

public sealed record ChannelInlineKeyboardAction(
    string ConversationId,
    string? ConversationThreadId,
    string Text,
    IReadOnlyList<ChannelInlineKeyboardRow> Rows,
    string? ParseMode = null);

public sealed record ChannelInteractionPromptAction(
    string InteractionId,
    string ConversationId,
    string? ConversationThreadId,
    string Prompt,
    IReadOnlyList<ChannelInlineKeyboardRow> Rows,
    string? ParseMode = null,
    string? SenderDisplayName = null);

public sealed record ChannelCallbackAckAction(
    string AcknowledgeToken,
    string Text);

public sealed record ChannelMessageEditAction(
    string ConversationId,
    string MessageId,
    string Text,
    bool ClearInlineKeyboard,
    string? ParseMode = null,
    string? SenderDisplayName = null);

public sealed record ChannelMessagePinAction(string ConversationId, string MessageId);

public sealed record ChannelMessageUnpinAction(string ConversationId, string MessageId);

public sealed record ChannelTopicRenameAction(string ConversationId, string TopicId, string TargetKey, string Name);

public sealed record ChannelMessageDeleteAction(
    string ConversationId,
    string MessageId);

public sealed record ChannelCommandDescriptor(
    string Command,
    string Description);

public sealed record ChannelTargetDescriptor(
    string TargetKey,
    string DisplayName);

public sealed record ChannelRegistrationProvisioningAction(
    string ConversationId,
    string BotName,
    string Description,
    string ShortDescription,
    IReadOnlyList<ChannelCommandDescriptor> Commands,
    IReadOnlyList<ChannelTargetDescriptor> Targets);

public sealed record ChannelInteractionTransportUpdateRequest(
    string? SourceMessageId);

public sealed record ChannelRegistrationProviderStateUpdateRequest(
    JsonElement SettingsPatch);
