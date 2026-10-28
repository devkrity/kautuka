namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private const string chatIdProperty = "chat_id";
    private const string messageIdProperty = "message_id";
    private static readonly TimeSpan telegramGlobalMinInterval = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan telegramPerChatMinInterval = TimeSpan.FromSeconds(1);
    private readonly object telegramRateLimitSync = new();
    private readonly Dictionary<string, DateTimeOffset> nextPerChatSendAt = new(StringComparer.Ordinal);
    private DateTimeOffset nextGlobalSendAt = DateTimeOffset.MinValue;

    private Task<OutboxExecutionResult> ExecuteOutboxActionAsync(
        PollingRegistration registration,
        string actionType,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        return actionType switch
        {
            "send_text" => ExecuteSendTextAsync(registration, payloadJson, cancellationToken),
            "send_chat_action" => ExecuteSendChatActionAsync(registration, payloadJson, cancellationToken),
            "send_inline_keyboard" => ExecuteSendInlineKeyboardAsync(registration, payloadJson, cancellationToken),
            "send_interaction_prompt" => ExecuteSendInteractionPromptAsync(registration, payloadJson, cancellationToken),
            "send_semantic_message" => ExecuteSendSemanticMessageAsync(registration, payloadJson, cancellationToken),
            "edit_semantic_message" => ExecuteEditSemanticMessageAsync(registration, payloadJson, cancellationToken),
            "acknowledge_callback" => ExecuteAcknowledgeCallbackAsync(registration, payloadJson, cancellationToken),
            "edit_message_text" => ExecuteEditMessageTextAsync(registration, payloadJson, cancellationToken),
            "delete_message" => ExecuteDeleteMessageAsync(registration, payloadJson, cancellationToken),
            "add_reaction" => ExecuteAddReactionAsync(registration, payloadJson, cancellationToken),
            "stream_message" => ExecuteStreamMessageAsync(registration, payloadJson, cancellationToken),
            "send_file" => ExecuteSendFileAsync(registration, payloadJson, cancellationToken),
            "provision_registration" => ExecuteProvisionRegistrationAsync(registration, payloadJson, cancellationToken),
            ChannelOutboundActionTypes.RuntimeEvent => ExecuteRuntimeEventAsync(registration, payloadJson, cancellationToken),
            _ => Task.FromResult(new OutboxExecutionResult(false, $"unsupported_action:{actionType}", null)),
        };
    }
}
