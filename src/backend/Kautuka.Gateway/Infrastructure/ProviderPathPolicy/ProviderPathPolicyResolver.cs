namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>
/// Resolves effective compatibility modes by combining stored observations
/// with current policy thresholds from <see cref="SqlitePlatformConfigurationProvider"/>.
/// </summary>
internal sealed class ProviderPathPolicyResolver(
    IProviderPathObservationStore store,
    SqlitePlatformConfigurationProvider configProvider) : IProviderPathPolicyResolver
{
    public async ValueTask<ProviderPathEffectiveMode> ResolveAsync(
        ProviderPathKey key, CancellationToken ct = default)
    {
        var config = configProvider.GetCurrent();
        var policy = config.ProviderPathPolicy;

        if (!policy.ProviderPathFallbackModeEnabled)
        {
            return new ProviderPathEffectiveMode
            {
                Key = key,
                ToolMode = ToolCompatibilityMode.Native,
                JsonMode = JsonCompatibilityMode.Relaxed,
                VisionMode = VisionCompatibilityMode.Disabled,
                ReasoningMode = ReasoningCompatibilityMode.TextFirst,
                ResolvedAt = DateTimeOffset.UtcNow,
            };
        }

        var obs = await store.GetAsync(key, ct).ConfigureAwait(false);
        var cached = await store.GetEffectiveModeAsync(key, ct).ConfigureAwait(false);

        ToolCompatibilityMode toolMode = ResolveToolMode(obs, policy, cached);
        JsonCompatibilityMode jsonMode = ResolveJsonMode(obs, policy, cached);
        VisionCompatibilityMode visionMode = ResolveVisionMode(obs, policy, cached);
        ReasoningCompatibilityMode reasoningMode = ResolveReasoningMode(obs, cached);

        var resolved = new ProviderPathEffectiveMode
        {
            Key = key,
            ToolMode = toolMode,
            JsonMode = jsonMode,
            VisionMode = visionMode,
            ReasoningMode = reasoningMode,
            ResolvedAt = DateTimeOffset.UtcNow,
        };

        await store.SaveEffectiveModeAsync(resolved, ct).ConfigureAwait(false);
        return resolved;
    }

    private static ToolCompatibilityMode ResolveToolMode(
        ProviderPathObservation? obs,
        ProviderPathPolicySettings policy,
        ProviderPathEffectiveMode? cached)
    {
        if (obs is null || obs.TotalToolAttempts < policy.ToolMinObservations)
            return cached?.ToolMode ?? ToolCompatibilityMode.Native;

        double rate = obs.NativeToolSuccessRate;
        if (rate >= policy.ToolNativePromotionThreshold)
            return ToolCompatibilityMode.Native;
        if (rate <= policy.ToolNativeDemotionThreshold)
            return ToolCompatibilityMode.PromptedJson;

        // Borderline — keep current mode to avoid oscillation.
        return cached?.ToolMode ?? ToolCompatibilityMode.Native;
    }

    private static JsonCompatibilityMode ResolveJsonMode(
        ProviderPathObservation? obs,
        ProviderPathPolicySettings policy,
        ProviderPathEffectiveMode? cached)
    {
        if (obs is null || obs.TotalJsonAttempts < policy.StructuredJsonMinObservations)
            return cached?.JsonMode ?? JsonCompatibilityMode.Relaxed;

        return obs.StrictJsonSuccessRate >= policy.StructuredJsonStrictPromotionThreshold
            ? JsonCompatibilityMode.Strict
            : JsonCompatibilityMode.Relaxed;
    }

    private static VisionCompatibilityMode ResolveVisionMode(
        ProviderPathObservation? obs,
        ProviderPathPolicySettings policy,
        ProviderPathEffectiveMode? cached)
    {
        if (obs is null || obs.TotalVisionAttempts == 0)
            return cached?.VisionMode ?? VisionCompatibilityMode.Disabled;

        return obs.VisionSuccessRate >= policy.VisionEnableThreshold
            ? VisionCompatibilityMode.Enabled
            : VisionCompatibilityMode.Disabled;
    }

    private static ReasoningCompatibilityMode ResolveReasoningMode(
        ProviderPathObservation? obs,
        ProviderPathEffectiveMode? cached)
    {
        if (obs is null || obs.ReasoningOnlyCount == 0)
            return cached?.ReasoningMode ?? ReasoningCompatibilityMode.TextFirst;

        // If any reasoning-only responses have been seen, promote to PromoteReasoningWhenEmpty.
        return ReasoningCompatibilityMode.PromoteReasoningWhenEmpty;
    }
}
