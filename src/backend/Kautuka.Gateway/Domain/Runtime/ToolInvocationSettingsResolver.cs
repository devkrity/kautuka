namespace Kautuka.Gateway.Domain.Runtime;

/// <summary>
/// Effective tool-invocation policy for a single agent run after platform defaults
/// and per-agent overrides have been resolved and normalized.
/// </summary>
/// <param name="AllowMultipleToolCalls">
/// When true, the model may emit several tool calls in one turn.
/// </param>
/// <param name="AllowConcurrentInvocation">
/// When true, those calls are started together. Forced false when multiple tool
/// calls are off, or when <paramref name="MaxParallelTools"/> is 1.
/// </param>
/// <param name="MaxParallelTools">
/// In-flight cap. 0 means unlimited, 1 is serial. MAF uses this only when
/// concurrent invocation is on; packed HITL WhenAll uses it even when concurrent
/// invocation is off.
/// </param>
internal sealed record ResolvedToolInvocationSettings(
    bool AllowMultipleToolCalls,
    bool AllowConcurrentInvocation,
    int MaxParallelTools)
{
    /// <summary>
    /// Finite in-flight cap to apply around tool bodies. Null means do not throttle
    /// (serial execution, or unlimited parallel).
    /// </summary>
    public int? ConcurrencyLimit =>
        AllowConcurrentInvocation && MaxParallelTools > 1 ? MaxParallelTools : null;
}

internal interface IToolInvocationSettingsResolver
{
    ResolvedToolInvocationSettings ResolveForAgent(string? agentId);

    ResolvedToolInvocationSettings Resolve(AgentRuntimeDefinition? agentRuntime);
}

internal static class ToolInvocationSettings
{
    public static ResolvedToolInvocationSettings Combine(
        bool systemAllowMultipleToolCalls,
        bool systemAllowConcurrentInvocation,
        int systemMaxParallelTools,
        bool? agentAllowMultipleToolCalls,
        bool? agentAllowConcurrentInvocation,
        int? agentMaxParallelTools)
        => Normalize(
            agentAllowMultipleToolCalls ?? systemAllowMultipleToolCalls,
            agentAllowConcurrentInvocation ?? systemAllowConcurrentInvocation,
            agentMaxParallelTools ?? systemMaxParallelTools);

    public static ResolvedToolInvocationSettings Normalize(
        bool allowMultipleToolCalls,
        bool allowConcurrentInvocation,
        int maxParallelTools)
    {
        int capped = RuntimeSettingKeys.ClampMaxParallelTools(maxParallelTools);
        bool multiple = allowMultipleToolCalls;
        bool concurrent = multiple && allowConcurrentInvocation && capped != 1;
        return new ResolvedToolInvocationSettings(multiple, concurrent, capped);
    }

    public static ResolvedToolInvocationSettings FromSnapshot(
        bool allowMultipleToolCalls,
        bool allowConcurrentInvocation,
        int maxParallelTools)
        => Normalize(allowMultipleToolCalls, allowConcurrentInvocation, maxParallelTools);
}

internal sealed class ToolInvocationSettingsResolver(
    GatewayState state,
    IPlatformConfigurationProvider configurationProvider) : IToolInvocationSettingsResolver
{
    public ResolvedToolInvocationSettings ResolveForAgent(string? agentId)
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

    public ResolvedToolInvocationSettings Resolve(AgentRuntimeDefinition? agentRuntime)
    {
        GatewaySystemSettings system = state.SystemSettings;
        return ToolInvocationSettings.Combine(
            system.AllowMultipleToolCalls,
            system.AllowConcurrentInvocation,
            system.MaxParallelTools,
            agentRuntime?.AllowMultipleToolCalls,
            agentRuntime?.AllowConcurrentInvocation,
            agentRuntime?.MaxParallelTools);
    }
}
