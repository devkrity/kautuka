using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal interface IChatClientMemoryDecorator
{
    IChatClient Decorate(IChatClient chatClient, string memoryContext);
}
