using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class SamplingPolicyResolverTests
{
    private readonly SamplingPolicyResolver resolver = new();

    [Theory]
    [InlineData(0.3f, 0.3f, 0.95f)]
    [InlineData(0.5f, 0.5f, 0.95f)]
    [InlineData(0.7f, 0.7f, 0.95f)]
    [InlineData(1.0f, 1.0f, 0.95f)]
    public void ResolveReturnsExpectedPolicyForExplicitTemperature(float inputTemperature, float temperature, float topP)
    {
        EffectiveSamplingPolicy policy = resolver.Resolve(inputTemperature);

        Assert.Equal(temperature, policy.Temperature);
        Assert.Equal(topP, policy.TopP);
    }

    [Fact]
    public void ResolveFallsBackToDefaultPolicyForMissingTemperature()
    {
        EffectiveSamplingPolicy policy = resolver.Resolve(null);

        Assert.Equal(0.7f, policy.Temperature);
        Assert.Equal(0.95f, policy.TopP);
    }
}
