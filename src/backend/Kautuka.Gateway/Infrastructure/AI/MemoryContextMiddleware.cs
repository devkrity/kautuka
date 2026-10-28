using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class MemoryContextMiddleware(IChatClient inner, string memoryContext) : DelegatingChatClient(inner)
{
    private readonly string memoryContext = memoryContext;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> augmented = AugmentMessages(messages);
        return base.GetResponseAsync(augmented, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> augmented = AugmentMessages(messages);
        return base.GetStreamingResponseAsync(augmented, options, cancellationToken);
    }

    private List<ChatMessage> AugmentMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        int systemIndex = list.FindIndex(static message => message.Role == ChatRole.System);

        if (systemIndex >= 0)
        {
            ChatMessage system = list[systemIndex];
            string systemText = system.Text ?? string.Empty;

            // Prevent runaway token growth by not re-appending identical managed context.
            if (!string.IsNullOrEmpty(systemText)
                && systemText.Contains(memoryContext, StringComparison.Ordinal))
            {
                TagPromptSize(list);
                return list;
            }

            string merged = string.IsNullOrWhiteSpace(systemText)
                ? memoryContext
                : $"{systemText}\n\n{memoryContext}";
            list[systemIndex] = new ChatMessage(ChatRole.System, merged);
            TagPromptSize(list);
            return list;
        }

        list.Insert(0, new ChatMessage(ChatRole.System, memoryContext));
        TagPromptSize(list);
        return list;
    }

    private static void TagPromptSize(List<ChatMessage> messages)
    {
        _ = messages;
    }
}
