using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class ChatClientMemoryDecorator : IChatClientMemoryDecorator
{
    public IChatClient Decorate(IChatClient chatClient, string memoryContext)
    {
        if (string.IsNullOrWhiteSpace(memoryContext))
        {
            return chatClient;
        }

        return new MemoryContextMiddleware(chatClient, memoryContext);
    }
}
