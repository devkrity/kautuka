using Kautuka.Gateway.Domain.Usage;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI.ProviderPathPolicy;

internal sealed class ProviderPathTelemetryChatClientDecorator(
    IProviderPathObservationStore observationStore,
    IProviderPathPolicyResolver policyResolver,
    IGatewayUsageCaptureAccessor usageCaptureAccessor,
    ILogger<ProviderPathTelemetryChatClient> compatibilityLogger) : IProviderPathTelemetryChatClientDecorator
{
    public IChatClient Decorate(IChatClient chatClient, ProviderPathKey pathKey)
        => new ProviderPathTelemetryChatClient(chatClient, pathKey, observationStore, policyResolver, usageCaptureAccessor, compatibilityLogger);
}
