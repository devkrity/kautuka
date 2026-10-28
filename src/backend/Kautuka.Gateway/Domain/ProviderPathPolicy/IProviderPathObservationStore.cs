namespace Kautuka.Gateway.Domain.ProviderPathPolicy;

/// <summary>
/// Persistence store for provider-path runtime observations.
/// All counter increments are atomic at the SQLite level.
/// </summary>
internal interface IProviderPathObservationStore
{
    ValueTask<ProviderPathObservation?> GetAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask<IReadOnlyList<ProviderPathObservation>> ListAsync(CancellationToken ct = default);

    ValueTask<ProviderPathEffectiveMode?> GetEffectiveModeAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask SaveEffectiveModeAsync(ProviderPathEffectiveMode mode, CancellationToken ct = default);

    ValueTask RecordNativeToolSuccessAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordNativeToolFailureAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordStrictJsonSuccessAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordRelaxedJsonSuccessAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordJsonFailureAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordReasoningOnlyAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordVisionSuccessAsync(ProviderPathKey key, CancellationToken ct = default);

    ValueTask RecordVisionFailureAsync(ProviderPathKey key, CancellationToken ct = default);
}
