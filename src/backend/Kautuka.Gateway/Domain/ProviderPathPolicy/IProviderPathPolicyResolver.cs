namespace Kautuka.Gateway.Domain.ProviderPathPolicy;

/// <summary>
/// Resolves the current effective compatibility mode for a provider path,
/// combining stored observations with current policy thresholds.
/// </summary>
internal interface IProviderPathPolicyResolver
{
    /// <summary>
    /// Returns the effective modes for the given path.
    /// Always returns a valid result (defaults when no observations exist).
    /// </summary>
    ValueTask<ProviderPathEffectiveMode> ResolveAsync(ProviderPathKey key, CancellationToken ct = default);
}
