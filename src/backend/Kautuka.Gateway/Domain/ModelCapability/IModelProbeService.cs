namespace Kautuka.Gateway.Domain.ModelCapability;

internal interface IModelProbeService
{
    Task<GatewayModelCapabilities?> ProbeModelAsync(string modelId);

    Task<GatewayModelCapabilities> ProbeModelCandidateAsync(string providerId, string modelName);

    Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(string providerId);
}
