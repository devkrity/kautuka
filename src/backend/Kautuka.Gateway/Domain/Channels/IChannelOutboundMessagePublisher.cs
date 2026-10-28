namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelOutboundMessagePublisher
{
    Task PublishTextMessageAsync(ChannelOutboundTextMessage message, CancellationToken cancellationToken);
    Task PublishTargetPickerAsync(ChannelOutboundTargetPickerMessage message, CancellationToken cancellationToken);
    Task<long?> PublishPromptMessageAsync(ChannelOutboundPromptMessage message, CancellationToken cancellationToken);
    Task<bool> PublishMessageUpdateAsync(ChannelOutboundMessageUpdate message, CancellationToken cancellationToken);
    Task PublishFileAsync(ChannelOutboundFileMessage message, CancellationToken cancellationToken);
    Task PublishStreamMessageAsync(ChannelOutboundStreamMessage message, CancellationToken cancellationToken);
}

internal sealed record ChannelOutboundTextMessage(
    string ChannelType,
    string RegistrationId,
    string ConversationId,
    string? ConversationThreadId,
    string Text,
    string? SenderDisplayName = null,
    string? SessionDisplayName = null,
    string? SessionId = null,
    string? RootRunId = null);

internal sealed record ChannelOutboundTargetPickerMessage(
    string ChannelType,
    string RegistrationId,
    string ConversationId,
    string? ConversationThreadId,
    string Prompt,
    IReadOnlyList<ChannelOutboundTargetPickerOption> Options);

internal sealed record ChannelOutboundTargetPickerOption(string Label, string CallbackToken, bool Selected = false);

internal sealed record ChannelOutboundStreamMessage(
    string ChannelType,
    string RegistrationId,
    string StreamId,
    long Sequence,
    string ConversationId,
    string? ConversationThreadId,
    string AccumulatedText,
    bool IsFinal,
    string? SenderDisplayName = null);

internal sealed record ChannelOutboundPromptMessage(
    string ChannelType,
    string RegistrationId,
    string InteractionId,
    string ConversationId,
    string? ConversationThreadId,
    string Prompt,
    IReadOnlyList<ChannelOutboundPromptOption> Options,
    string? SenderDisplayName = null);

internal sealed record ChannelOutboundPromptOption(
    string Label,
    string Value,
    string Style = "default");

internal sealed record ChannelOutboundMessageUpdate(
    string ChannelType,
    string RegistrationId,
    string ConversationId,
    string MessageId,
    string Text,
    bool ClearActions,
    bool UseRichText = false,
    string? SenderDisplayName = null);

internal sealed record ChannelOutboundFileMessage(
    string ChannelType,
    string RegistrationId,
    string SessionId,
    string OwnerUserId,
    string AssetId,
    string ConversationId,
    string? ConversationThreadId,
    string SafeFileName,
    string ContentType,
    long SizeBytes,
    string? Caption = null,
    string? SenderDisplayName = null);
