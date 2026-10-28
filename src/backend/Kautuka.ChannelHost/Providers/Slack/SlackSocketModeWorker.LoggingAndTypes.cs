

namespace Kautuka.ChannelHost.Providers.Slack;

internal static partial class SlackSocketModeWorkerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to open Slack connection for attachment {AttachmentId}: {Content}")]
    public static partial void ConnectionOpenFailed(ILogger logger, string attachmentId, string content);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Slack connection loop error for attachment {AttachmentId}")]
    public static partial void ConnectionLoopError(ILogger logger, Exception exception, string attachmentId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Slack Socket Mode connected for attachment {AttachmentId}")]
    public static partial void SocketModeConnected(ILogger logger, string attachmentId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Error processing Slack message for attachment {AttachmentId}")]
    public static partial void MessageProcessingError(ILogger logger, Exception exception, string attachmentId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Error sending Slack message for attachment {AttachmentId}")]
    public static partial void SendOutboundMessageError(ILogger logger, Exception exception, string attachmentId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Dropped Slack outbound delivery to a destination outside the owner's direct message. AttachmentId={AttachmentId}")]
    public static partial void OutboundDestinationRejected(ILogger logger, string attachmentId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Slack response_url ephemeral delivery failed")]
    public static partial void ResponseUrlEphemeralFailed(ILogger logger, Exception exception);
}
