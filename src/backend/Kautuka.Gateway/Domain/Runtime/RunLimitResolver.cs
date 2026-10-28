namespace Kautuka.Gateway.Domain.Runtime;

internal sealed record ResolvedRunLimits(
    TimeSpan ModelTimeout,
    TimeSpan? MaxRunDuration,
    TimeSpan? HitlExpiry,
    HitlExpiryAction ExpiryAction);

internal interface IRunLimitResolver
{
    TimeSpan GetModelTimeout();

    ResolvedRunLimits ResolveForAgent(string? agentId);

    ResolvedRunLimits Resolve(AgentRuntimeDefinition? agentRuntime);
}

internal sealed class RunLimitResolver(
    GatewayState state,
    IPlatformConfigurationProvider configurationProvider) : IRunLimitResolver
{
    public TimeSpan GetModelTimeout()
    {
        int seconds = state.SystemSettings.ModelTimeoutSeconds;
        if (seconds <= 0)
        {
            seconds = RuntimeSettingKeys.DefaultModelTimeoutSeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public ResolvedRunLimits ResolveForAgent(string? agentId)
    {
        AgentRuntimeDefinition? runtime = null;
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            AgentDefinition? agent = configurationProvider.GetCurrent().Agents
                .FirstOrDefault(candidate => string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
            runtime = agent?.Runtime;
        }

        return Resolve(runtime);
    }

    public ResolvedRunLimits Resolve(AgentRuntimeDefinition? agentRuntime)
    {
        GatewaySystemSettings system = state.SystemSettings;
        TimeSpan modelTimeout = GetModelTimeout();
        TimeSpan? maxRun = ResolveClock(
            agentRuntime?.MaxRunDurationSeconds,
            system.MaxRunDurationSeconds);
        TimeSpan? hitlExpiry = ResolveClock(
            agentRuntime?.HitlExpirySeconds,
            system.HitlExpirySeconds);
        HitlExpiryAction action = ResolveAction(
            agentRuntime?.HitlExpiryAction,
            system.HitlExpiryAction);
        return new ResolvedRunLimits(modelTimeout, maxRun, hitlExpiry, action);
    }

    internal static TimeSpan? ResolveClock(int? agentSeconds, int systemSeconds)
    {
        int resolved = agentSeconds ?? systemSeconds;
        return resolved <= 0 ? null : TimeSpan.FromSeconds(resolved);
    }

    internal static HitlExpiryAction ResolveAction(string? agentAction, string systemAction)
    {
        if (!string.IsNullOrWhiteSpace(agentAction))
        {
            return HitlExpiryActions.Parse(agentAction);
        }

        return HitlExpiryActions.Parse(systemAction);
    }
}
