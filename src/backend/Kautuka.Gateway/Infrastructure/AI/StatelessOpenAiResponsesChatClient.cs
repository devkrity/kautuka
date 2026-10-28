using Microsoft.Extensions.AI;

using OpenAI.Responses;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class StatelessOpenAiResponsesChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(chatMessages, WithStoredOutputDisabled(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(chatMessages, WithStoredOutputDisabled(options), cancellationToken);

    private static ChatOptions WithStoredOutputDisabled(ChatOptions? options)
    {
        ChatOptions next = options?.Clone() ?? new ChatOptions();
        Func<IChatClient, object?>? existing = next.RawRepresentationFactory;
        next.RawRepresentationFactory = client => CreateStatelessOptions(client, existing);
        return next;
    }

#pragma warning disable OPENAI001
    private static CreateResponseOptions CreateStatelessOptions(
        IChatClient client,
        Func<IChatClient, object?>? existing)
    {
        object? raw = existing?.Invoke(client);
        CreateResponseOptions create = raw as CreateResponseOptions ?? new CreateResponseOptions();
        create.StoredOutputEnabled = false;
        const string encryptedReasoning = "reasoning.encrypted_content";
        if (!create.IncludedProperties.Contains(encryptedReasoning))
        {
            create.IncludedProperties.Add(encryptedReasoning);
        }

        return create;
    }
#pragma warning restore OPENAI001
}
