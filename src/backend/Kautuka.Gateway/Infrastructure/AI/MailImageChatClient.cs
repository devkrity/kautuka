using Kautuka.Gateway.Domain.Assets;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

/// <summary>
/// Adds a held mail image to the message list the model is about to read.
/// Sits inside function-invocation middleware so the picture is on that continuation.
/// </summary>
internal sealed class MailImageChatClient(IChatClient inner, MailImageHandoff images) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Attach(chatMessages, images), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Attach(chatMessages, images), options, cancellationToken);

    internal static List<ChatMessage> Attach(IEnumerable<ChatMessage> messages, MailImageHandoff images)
    {
        List<ChatMessage> list = messages.ToList();
        List<AIContent>? pictures = null;
        foreach (ChatMessage message in list)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionResultContent result || string.IsNullOrEmpty(result.CallId))
                {
                    continue;
                }

                if (!images.TryTake(result.CallId, out MailImageHandoff.Pending image) || image.Bytes.Length == 0)
                {
                    continue;
                }

                pictures ??= [];
                pictures.Add(new DataContent(image.Bytes, image.MediaType) { Name = image.Name });
            }
        }

        if (pictures is not null)
        {
            list.Add(new ChatMessage(ChatRole.User, pictures));
        }

        return list;
    }
}
