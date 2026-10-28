namespace Kautuka.Gateway.Infrastructure.Agents;

using Kautuka.Gateway.Domain.Execution;

internal interface ISamplingPolicyResolver
{
    EffectiveSamplingPolicy Resolve(float? temperature);
}

internal sealed class SamplingPolicyResolver : ISamplingPolicyResolver
{
    private const float defaultTemperature = 0.7f;
    private const float defaultTopP = 0.95f;

    public EffectiveSamplingPolicy Resolve(float? temperature)
    {
        float resolvedTemperature = temperature is null || float.IsNaN(temperature.Value)
            ? defaultTemperature
            : Math.Clamp(temperature.Value, 0.0f, 1.0f);

        return new EffectiveSamplingPolicy(resolvedTemperature, defaultTopP);
    }
}
