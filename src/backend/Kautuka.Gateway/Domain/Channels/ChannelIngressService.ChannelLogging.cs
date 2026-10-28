namespace Kautuka.Gateway.Domain.Channels;

internal static partial class ChannelIngressServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to deliver Channel HITL approval notification for request {RequestId}")]
    public static partial void ChannelHitlApprovalDeliveryFailed(ILogger logger, string requestId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Channel bot_token is missing for registration {RegistrationId}")]
    public static partial void MissingBotToken(ILogger logger, string registrationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Channel sendMessage failed. registration={RegistrationId} status={StatusCode} body={Body}")]
    public static partial void SendMessageFailed(ILogger logger, string registrationId, System.Net.HttpStatusCode statusCode, string body);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Channel sendMessage request failed for registration {RegistrationId}")]
    public static partial void SendMessageRequestFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Channel sendChatAction typing failed for registration {RegistrationId}")]
    public static partial void SendChatActionTypingFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Channel interactive sendMessage failed. registration={RegistrationId} status={StatusCode} body={Body}")]
    public static partial void SendInteractiveMessageFailed(ILogger logger, string registrationId, System.Net.HttpStatusCode statusCode, string body);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Channel interactive sendMessage request failed for registration {RegistrationId}")]
    public static partial void SendInteractiveMessageRequestFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Channel inline keyboard sendMessage failed. registration={RegistrationId} status={StatusCode} body={Body}")]
    public static partial void SendInlineKeyboardMessageFailed(ILogger logger, string registrationId, System.Net.HttpStatusCode statusCode, string body);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Channel inline keyboard sendMessage request failed for registration {RegistrationId}")]
    public static partial void SendInlineKeyboardMessageRequestFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "Channel deleteMessage failed (may already be deleted). registration={RegistrationId} msgId={MessageId} status={StatusCode}")]
    public static partial void DeleteChannelMessageFailed(ILogger logger, string registrationId, long messageId, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug, Message = "DeleteChannelMessage request failed for registration {RegistrationId}")]
    public static partial void DeleteChannelMessageRequestFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "Failed to answer Channel callback query for registration {RegistrationId}")]
    public static partial void AnswerChannelCallbackFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Channel approval attachment refresh failed for userId={UserId}; using cached routing if present")]
    public static partial void ChannelApprovalRefreshFailed(ILogger logger, Exception exception, string userId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Channel approval notify entered for request {RequestId} userId={UserId} sessionId={SessionId}")]
    public static partial void ChannelApprovalNotifyEntered(ILogger logger, string requestId, string userId, string sessionId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Channel approval skipped for request {RequestId}; interaction already pending")]
    public static partial void ChannelApprovalAlreadyPending(ILogger logger, string requestId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "Channel approval routing skipped for request {RequestId} userId={UserId} sessionId={SessionId}; no active channel routing context")]
    public static partial void ChannelApprovalRoutingFailed(ILogger logger, string requestId, string userId, string sessionId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug, Message = "Channel approval interaction created for request {RequestId} interactionId={InteractionId}")]
    public static partial void ChannelApprovalInteractionCreated(ILogger logger, string requestId, string interactionId);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Channel approval delivered for request {RequestId} interactionId={InteractionId} msgId={MessageId}")]
    public static partial void ChannelApprovalDelivered(ILogger logger, string requestId, string interactionId, long? messageId);

    [LoggerMessage(EventId = 19, Level = LogLevel.Debug, Message = "Channel approval routing: no active registration for userId={UserId}")]
    public static partial void ChannelApprovalNoActiveRegistration(ILogger logger, string userId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "Channel approval routing: no fallback binding found for registration={RegistrationId} userId={UserId} sessionId={SessionId}")]
    public static partial void ChannelApprovalNoFallbackBinding(ILogger logger, string registrationId, string userId, string sessionId);

    [LoggerMessage(EventId = 21, Level = LogLevel.Debug, Message = "Channel approval routing resolved: registration={RegistrationId} chatId={ChatId} targetType={TargetType} targetId={TargetId}")]
    public static partial void ChannelApprovalRoutingResolved(ILogger logger, string registrationId, string chatId, string targetType, string targetId);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "Channel approval {RequestId} interaction {InteractionId} was closed without projection because its registration is missing or inactive")]
    public static partial void ChannelApprovalResolutionSkipped(ILogger logger, string requestId, string interactionId);
}
