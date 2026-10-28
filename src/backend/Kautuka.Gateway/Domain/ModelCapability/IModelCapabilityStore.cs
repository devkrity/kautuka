namespace Kautuka.Gateway.Domain.ModelCapability;

internal interface IModelCapabilityStore
{
    ValueTask<ModelCapabilityProfile?> TryGetAsync(ModelCapabilityKey key, CancellationToken ct = default);
    ValueTask SaveAsync(ModelCapabilityProfile profile, CancellationToken ct = default);
}
