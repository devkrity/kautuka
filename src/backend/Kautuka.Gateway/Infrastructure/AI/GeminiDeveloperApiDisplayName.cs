using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

/// <summary>
/// Google.GenAI Developer API conversion throws when a blob or file part has
/// <c>displayName</c> (Vertex / Gemini Enterprise only). MEAI maps
/// <see cref="DataContent.Name"/> and <see cref="HostedFileContent.Name"/> onto that field.
/// </summary>
internal static class GeminiDeveloperApiDisplayName
{
    internal static IReadOnlyList<ChatMessage> Strip(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<ChatMessage> stripped = [];
        foreach (ChatMessage message in messages)
        {
            stripped.Add(StripMessage(message));
        }

        return stripped;
    }

    private static ChatMessage StripMessage(ChatMessage message)
    {
        if (!NeedsStrip(message.Contents))
        {
            return message;
        }

        List<AIContent> contents = [.. message.Contents.Select(StripContent)];
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    private static bool NeedsStrip(IList<AIContent> contents)
    {
        foreach (AIContent content in contents)
        {
            if (content is DataContent data && !string.IsNullOrWhiteSpace(data.Name))
            {
                return true;
            }

            if (content is HostedFileContent hosted && !string.IsNullOrWhiteSpace(hosted.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static AIContent StripContent(AIContent content)
    {
        if (content is DataContent data && !string.IsNullOrWhiteSpace(data.Name))
        {
            return new DataContent(data.Data, data.MediaType)
            {
                Annotations = data.Annotations,
                RawRepresentation = data.RawRepresentation,
                AdditionalProperties = data.AdditionalProperties,
            };
        }

        if (content is HostedFileContent hosted && !string.IsNullOrWhiteSpace(hosted.Name))
        {
            return new HostedFileContent(hosted.FileId)
            {
                MediaType = hosted.MediaType,
                SizeInBytes = hosted.SizeInBytes,
                CreatedAt = hosted.CreatedAt,
                Purpose = hosted.Purpose,
                Scope = hosted.Scope,
                Annotations = hosted.Annotations,
                RawRepresentation = hosted.RawRepresentation,
                AdditionalProperties = hosted.AdditionalProperties,
            };
        }

        return content;
    }
}
