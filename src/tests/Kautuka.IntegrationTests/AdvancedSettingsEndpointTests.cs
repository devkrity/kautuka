using System.Text.Json;

namespace Kautuka.IntegrationTests;

public sealed class AdvancedSettingsEndpointTests
{
    [Fact]
    public async Task ProviderPathPolicyEndpointsRoundTrip()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument initial = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/settings/provider-path-policy",
            null,
            accessToken);

        Assert.True(initial.RootElement.TryGetProperty("toolNativePromotionThreshold", out _));
        Assert.True(initial.RootElement.TryGetProperty("providerPathFallbackModeEnabled", out _));

        using JsonDocument updated = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/provider-path-policy",
            new
            {
                tool_native_promotion_threshold = 0.91,
                tool_min_observations = 9,
                tool_native_demotion_threshold = 0.33,
                structured_json_strict_promotion_threshold = 0.97,
                structured_json_min_observations = 7,
                vision_enable_threshold = 0.72,
                observation_decay_window_days = 45,
                provider_path_fallback_mode_enabled = false,
            },
            accessToken);

        Assert.Equal(0.91, updated.RootElement.GetProperty("toolNativePromotionThreshold").GetDouble(), 3);
        Assert.Equal(9, updated.RootElement.GetProperty("toolMinObservations").GetInt32());
        Assert.Equal(0.33, updated.RootElement.GetProperty("toolNativeDemotionThreshold").GetDouble(), 3);
        Assert.Equal(0.97, updated.RootElement.GetProperty("structuredJsonStrictPromotionThreshold").GetDouble(), 3);
        Assert.Equal(7, updated.RootElement.GetProperty("structuredJsonMinObservations").GetInt32());
        Assert.Equal(0.72, updated.RootElement.GetProperty("visionEnableThreshold").GetDouble(), 3);
        Assert.Equal(45, updated.RootElement.GetProperty("observationDecayWindowDays").GetInt32());
        Assert.False(updated.RootElement.GetProperty("providerPathFallbackModeEnabled").GetBoolean());
    }

    [Fact]
    public async Task MemorySettingsEndpointsRoundTrip()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument initial = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/settings/memory",
            null,
            accessToken);

        Assert.True(initial.RootElement.TryGetProperty("embeddingsEnabled", out _));
        Assert.True(initial.RootElement.TryGetProperty("factPersistThreshold", out _));
        Assert.True(initial.RootElement.TryGetProperty("maxInjectedFacts", out _));

        using JsonDocument updated = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/memory",
            new
            {
                embeddings_enabled = true,
                fact_persist_threshold = 0.83,
                fact_inject_threshold = 0.64,
                max_injected_facts = 14,
                injection_token_budget = 1536,
            },
            accessToken);

        Assert.True(updated.RootElement.GetProperty("embeddingsEnabled").GetBoolean());
        Assert.Equal(0.83, updated.RootElement.GetProperty("factPersistThreshold").GetDouble(), 3);
        Assert.Equal(0.64, updated.RootElement.GetProperty("factInjectThreshold").GetDouble(), 3);
        Assert.Equal(14, updated.RootElement.GetProperty("maxInjectedFacts").GetInt32());
        Assert.Equal(1536, updated.RootElement.GetProperty("injectionTokenBudget").GetInt32());
    }
}
