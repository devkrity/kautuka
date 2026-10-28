using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI.ProviderPathPolicy;

internal interface IProviderPathTelemetryChatClientDecorator
{
    IChatClient Decorate(IChatClient chatClient, ProviderPathKey pathKey);
}
