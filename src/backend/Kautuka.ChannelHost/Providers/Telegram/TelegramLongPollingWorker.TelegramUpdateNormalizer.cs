using System.Text.Json;

using Kautuka.ChannelContracts.Assets;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private const string telegramChannelType = "telegram";
    private const long maxInboundMediaSizeBytes = 2 * 1024 * 1024;
    private const string acceptedInboundMediaDescription = "Accepted attachments: PDF, Word, Excel, PowerPoint, text/code files, images, audio, video, archives, and other non-executable files. Maximum size is 2 MB per attachment.";

    private static ChannelInboundEvent? CreateInboundEvent(PollingRegistration registration, JsonElement update)
    {
        string eventId = update.TryGetProperty("update_id", out JsonElement updateIdElement)
            ? updateIdElement.ToString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");
        eventId = $"telegram:{registration.BotIdentity}:{eventId}";

        return TryCreateCallbackInboundEvent(registration, update, eventId)
            ?? TryCreateMessageInboundEvent(registration, update, eventId);
    }

    internal static ChannelInboundEvent? CreateInboundEventForTest(
        string registrationId,
        string ownerUserId,
        JsonElement update)
        => CreateInboundEvent(new PollingRegistration(
            registrationId,
            ownerUserId,
            "test-only-not-used-for-normalization:" + registrationId,
            1,
            default), update);

    private static ChannelInboundEvent? TryCreateCallbackInboundEvent(
        PollingRegistration registration,
        JsonElement update,
        string eventId)
    {
        if (!update.TryGetProperty("callback_query", out JsonElement callback)
            || callback.ValueKind != JsonValueKind.Object
            || !callback.TryGetProperty("id", out JsonElement callbackQueryId)
            || !callback.TryGetProperty("from", out JsonElement from)
            || !from.TryGetProperty("id", out JsonElement fromId)
            || !callback.TryGetProperty("message", out JsonElement callbackMessage)
            || !callbackMessage.TryGetProperty("chat", out JsonElement callbackChat)
            || !callbackChat.TryGetProperty("id", out JsonElement callbackChatId))
        {
            return null;
        }

        string? callbackData = callback.TryGetProperty("data", out JsonElement dataElement)
            ? dataElement.ToString()
            : null;
        string callbackThreadId = callbackMessage.TryGetProperty("message_thread_id", out JsonElement callbackThread)
            ? callbackThread.ToString() ?? "root"
            : "root";
        long? callbackMessageId = callbackMessage.TryGetProperty("message_id", out JsonElement callbackMessageIdElement)
            && callbackMessageIdElement.TryGetInt64(out long parsedCallbackMessageId)
            ? parsedCallbackMessageId
            : null;

        return new ChannelInboundEvent(
            EventId: eventId,
            ChannelType: telegramChannelType,
            RegistrationId: registration.RegistrationId,
            OwnerUserId: registration.OwnerUserId,
            ExternalUserId: fromId.ToString() ?? string.Empty,
            ConversationId: callbackChatId.ToString() ?? string.Empty,
            ConversationThreadId: callbackThreadId,
            EventType: "callback",
            Text: null,
            CallbackData: callbackData,
            AcknowledgeToken: callbackQueryId.GetString(),
            SourceMessageId: callbackMessageId,
            Payload: update.Clone());
    }

    private static ChannelInboundEvent? TryCreateMessageInboundEvent(
        PollingRegistration registration,
        JsonElement update,
        string eventId)
    {
        if (!update.TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("from", out JsonElement messageFrom)
            || !messageFrom.TryGetProperty("id", out JsonElement messageFromId)
            || !message.TryGetProperty("chat", out JsonElement messageChat)
            || !messageChat.TryGetProperty("id", out JsonElement messageChatId))
        {
            return null;
        }

        string text = ResolveTelegramMessageText(message);
        IReadOnlyList<ChannelInboundMediaDescriptor> inboundMedia = ResolveTelegramInboundMediaDescriptors(message);
        string? messageThreadId = message.TryGetProperty("message_thread_id", out JsonElement threadElement)
            ? threadElement.ToString()
            : null;

        return new ChannelInboundEvent(
            EventId: eventId,
            ChannelType: telegramChannelType,
            RegistrationId: registration.RegistrationId,
            OwnerUserId: registration.OwnerUserId,
            ExternalUserId: messageFromId.ToString() ?? string.Empty,
            ConversationId: messageChatId.ToString() ?? string.Empty,
            ConversationThreadId: messageThreadId,
            EventType: "message",
            Text: text,
            CallbackData: null,
            AcknowledgeToken: null,
            SourceMessageId: message.TryGetProperty("message_id", out JsonElement promptId) && promptId.TryGetInt64(out long numericPromptId) ? numericPromptId : null,
            Payload: update.Clone(),
            InboundMedia: inboundMedia,
            ReplySourceTransportMessageId: ResolveTelegramReplySourceMessageId(message));
    }

    private static string? ResolveTelegramReplySourceMessageId(JsonElement message)
    {
        if (!message.TryGetProperty("reply_to_message", out JsonElement reply)
            || reply.ValueKind != JsonValueKind.Object)
            return message.TryGetProperty("external_reply", out _) ? "external-reply" : null;

        // Telegram includes a topic-creation service message as an implicit reply. It is
        // not a user-selected prompt and must not override the selected session.
        if (reply.TryGetProperty("forum_topic_created", out _)
            || (message.TryGetProperty("message_thread_id", out JsonElement topic)
                && reply.TryGetProperty("message_id", out JsonElement repliedId)
                && topic.ToString() == repliedId.ToString()))
            return null;

        return reply.TryGetProperty("message_id", out JsonElement id) ? id.ToString() : null;
    }

    internal static IReadOnlyList<ChannelInboundMediaDescriptor> ResolveTelegramInboundMediaDescriptors(JsonElement message)
    {
        List<ChannelInboundMediaDescriptor> media = [];

        if (TryCreateDocumentDescriptor(message, out ChannelInboundMediaDescriptor? document))
        {
            media.Add(document!);
        }

        if (TryCreatePhotoDescriptor(message, out ChannelInboundMediaDescriptor? photo))
        {
            media.Add(photo!);
        }

        if (TryCreateVideoDescriptor(message, out ChannelInboundMediaDescriptor? video))
        {
            media.Add(video!);
        }

        if (TryCreateAudioDescriptor(message, out ChannelInboundMediaDescriptor? audio))
        {
            media.Add(audio!);
        }

        if (TryCreateVoiceDescriptor(message, out ChannelInboundMediaDescriptor? voice))
        {
            media.Add(voice!);
        }

        return media;
    }

    internal static InboundMediaValidationResult ValidateInboundMediaDescriptors(IReadOnlyList<ChannelInboundMediaDescriptor>? inboundMedia)
    {
        if (inboundMedia is null || inboundMedia.Count == 0)
        {
            return new InboundMediaValidationResult([], 0, false, false);
        }

        List<ChannelInboundMediaDescriptor> accepted = [];
        int rejectedCount = 0;
        bool rejectedOversizedMedia = false;
        bool rejectedBlockedMedia = false;

        foreach (ChannelInboundMediaDescriptor media in inboundMedia)
        {
            if (media.SizeBytes is long sizeBytes && sizeBytes > maxInboundMediaSizeBytes)
            {
                rejectedCount++;
                rejectedOversizedMedia = true;
                continue;
            }

            try
            {
                string fileName = string.IsNullOrWhiteSpace(media.FileName)
                    ? BuildSyntheticFileName(media.MediaKind, media.ProviderUniqueId, media.ContentType ?? "application/octet-stream", null)
                    : media.FileName;
                string normalizedContentType = AttachmentUploadPolicy.NormalizeContentType(media.ContentType ?? string.Empty, fileName, enforceBlockedUploadPolicy: true);
                accepted.Add(media with
                {
                    FileName = fileName,
                    ContentType = normalizedContentType,
                });
            }
            catch (InvalidOperationException)
            {
                rejectedCount++;
                rejectedBlockedMedia = true;
            }
        }

        return new InboundMediaValidationResult(accepted, rejectedCount, rejectedOversizedMedia, rejectedBlockedMedia);
    }

    internal static string BuildInboundMediaRejectionText(InboundMediaValidationResult validation)
    {
        if (validation.RejectedMediaCount <= 0)
        {
            return string.Empty;
        }

        if (validation.RejectedOversizedMedia && validation.RejectedBlockedMedia)
        {
            return $"Some attachments were rejected because they were unsupported, unsafe, or over the 2 MB limit. {acceptedInboundMediaDescription}";
        }

        if (validation.RejectedOversizedMedia)
        {
            return $"Some attachments were rejected because they exceed the 2 MB limit. {acceptedInboundMediaDescription}";
        }

        return $"Some attachments were rejected because they are unsupported or blocked. {acceptedInboundMediaDescription}";
    }

    private static string ResolveTelegramMessageText(JsonElement message)
    {
        if (message.TryGetProperty("text", out JsonElement textElement))
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (message.TryGetProperty("caption", out JsonElement captionElement))
        {
            return captionElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool TryCreateDocumentDescriptor(JsonElement message, out ChannelInboundMediaDescriptor? descriptor)
    {
        if (!message.TryGetProperty("document", out JsonElement document) || document.ValueKind != JsonValueKind.Object)
        {
            descriptor = null;
            return false;
        }

        descriptor = CreateDescriptor(document, "document", defaultContentType: null, defaultFileNamePrefix: "document");
        return descriptor is not null;
    }

    private static bool TryCreatePhotoDescriptor(JsonElement message, out ChannelInboundMediaDescriptor? descriptor)
    {
        descriptor = null;

        if (!message.TryGetProperty("photo", out JsonElement photoArray) || photoArray.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement? selected = null;
        long selectedScore = long.MinValue;
        foreach (JsonElement candidate in photoArray.EnumerateArray())
        {
            if (!candidate.TryGetProperty("file_id", out _))
            {
                continue;
            }

            long score = 0;
            if (candidate.TryGetProperty("file_size", out JsonElement fileSizeElement)
                && fileSizeElement.TryGetInt64(out long fileSize))
            {
                score = fileSize;
            }
            else if (candidate.TryGetProperty("width", out JsonElement widthElement)
                && widthElement.TryGetInt32(out int width)
                && candidate.TryGetProperty("height", out JsonElement heightElement)
                && heightElement.TryGetInt32(out int height))
            {
                score = (long)width * height;
            }

            if (selected is null || score >= selectedScore)
            {
                selected = candidate;
                selectedScore = score;
            }
        }

        if (selected is null)
        {
            return false;
        }

        descriptor = CreateDescriptor(selected.Value, "photo", "image/jpeg", "photo", ".jpg");
        return descriptor is not null;
    }

    private static bool TryCreateVideoDescriptor(JsonElement message, out ChannelInboundMediaDescriptor? descriptor)
    {
        if (!message.TryGetProperty("video", out JsonElement video) || video.ValueKind != JsonValueKind.Object)
        {
            descriptor = null;
            return false;
        }

        descriptor = CreateDescriptor(video, "video", "video/mp4", "video", ".mp4");
        return descriptor is not null;
    }

    private static bool TryCreateAudioDescriptor(JsonElement message, out ChannelInboundMediaDescriptor? descriptor)
    {
        if (!message.TryGetProperty("audio", out JsonElement audio) || audio.ValueKind != JsonValueKind.Object)
        {
            descriptor = null;
            return false;
        }

        descriptor = CreateDescriptor(audio, "audio", "audio/mpeg", "audio", ".mp3");
        return descriptor is not null;
    }

    private static bool TryCreateVoiceDescriptor(JsonElement message, out ChannelInboundMediaDescriptor? descriptor)
    {
        if (!message.TryGetProperty("voice", out JsonElement voice) || voice.ValueKind != JsonValueKind.Object)
        {
            descriptor = null;
            return false;
        }

        descriptor = CreateDescriptor(voice, "voice", "audio/ogg", "voice", ".ogg");
        return descriptor is not null;
    }

    private static ChannelInboundMediaDescriptor? CreateDescriptor(
        JsonElement media,
        string mediaKind,
        string? defaultContentType,
        string defaultFileNamePrefix,
        string? defaultExtension = null)
    {
        if (!media.TryGetProperty("file_id", out JsonElement fileIdElement))
        {
            return null;
        }

        string? mediaId = fileIdElement.GetString();
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        string? providerUniqueId = media.TryGetProperty("file_unique_id", out JsonElement uniqueIdElement)
            ? uniqueIdElement.GetString()
            : null;
        string? fileName = media.TryGetProperty("file_name", out JsonElement fileNameElement)
            ? fileNameElement.GetString()
            : null;
        string? contentType = media.TryGetProperty("mime_type", out JsonElement mimeTypeElement)
            ? mimeTypeElement.GetString()
            : null;
        long? sizeBytes = media.TryGetProperty("file_size", out JsonElement fileSizeElement)
            && fileSizeElement.TryGetInt64(out long parsedFileSize)
            ? parsedFileSize
            : null;

        string resolvedContentType = string.IsNullOrWhiteSpace(contentType)
            ? defaultContentType ?? "application/octet-stream"
            : contentType;

        string resolvedFileName = string.IsNullOrWhiteSpace(fileName)
            ? BuildSyntheticFileName(defaultFileNamePrefix, providerUniqueId, resolvedContentType, defaultExtension)
            : fileName;

        return new ChannelInboundMediaDescriptor(
            mediaId,
            mediaKind,
            resolvedFileName,
            resolvedContentType,
            sizeBytes,
            providerUniqueId);
    }

    private static string BuildSyntheticFileName(string prefix, string? providerUniqueId, string contentType, string? defaultExtension)
    {
        string suffix = string.IsNullOrWhiteSpace(providerUniqueId) ? Guid.NewGuid().ToString("N") : providerUniqueId;
        string extension = string.IsNullOrWhiteSpace(defaultExtension)
            ? ExtensionFromContentType(contentType)
            : defaultExtension;

        return $"{prefix}-{suffix}{extension}";
    }

    private static string ExtensionFromContentType(string contentType)
        => contentType switch
        {
            "application/pdf" => ".pdf",
            "application/msword" => ".doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.ms-excel" => ".xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.ms-powerpoint" => ".ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/wav" => ".wav",
            _ => ".bin",
        };
}

internal sealed record InboundMediaValidationResult(
    IReadOnlyList<ChannelInboundMediaDescriptor> AcceptedMedia,
    int RejectedMediaCount,
    bool RejectedOversizedMedia,
    bool RejectedBlockedMedia)
{
    public bool HasRejections => RejectedMediaCount > 0;
}
