using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.ModelCapability;

internal interface IModelCapabilityService
{
    /// <summary>
    /// Returns the effective capability profile for a model.
    /// Runs bounded probes on first encounter, then serves from the persisted cache
    /// until provider, endpoint, model, or probe schema identity changes.
    /// Later runs update the profile through <see cref="RecordObservationAsync"/>.
    /// </summary>
    ValueTask<ModelCapabilityProfile> GetOrProbeAsync(
        ModelCapabilityKey key,
        IChatClient rawClient,
        CancellationToken ct = default);

    /// <summary>
    /// Records a runtime observation (e.g. reasoning-only output) back into the profile.
    /// Updates observed behaviors and re-persists the profile asynchronously.
    /// </summary>
    ValueTask RecordObservationAsync(
        ModelCapabilityKey key,
        RuntimeObservation observation,
        CancellationToken ct = default);
}
