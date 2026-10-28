using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class GeminiDeveloperApiChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(GeminiDeveloperApiDisplayName.Strip(chatMessages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(GeminiDeveloperApiDisplayName.Strip(chatMessages), options, cancellationToken);
}
