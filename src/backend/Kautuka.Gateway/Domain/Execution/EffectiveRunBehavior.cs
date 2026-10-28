namespace Kautuka.Gateway.Domain.Execution;

internal sealed record EffectiveSamplingPolicy(
    float Temperature,
    float TopP);

internal sealed record EffectivePersonaProfile(
    string Style,
    string Tone,
    string Verbosity);

internal sealed record EffectiveRuntimeGrounding(
    string CurrentUtc,
    string CurrentLocal,
    string Locale,
    string TimeZone);

internal sealed record EffectiveModelVisibleContext(
    EffectivePersonaProfile Persona,
    EffectiveRuntimeGrounding RuntimeGrounding);

internal sealed record EffectiveSystemContextSection(
    string Name,
    string Content);

internal sealed record EffectiveToolingSnapshot(
    string[] ToolNames,
    string ToolMode,
    bool AllowMultipleToolCalls,
    bool AllowConcurrentInvocation = false,
    int MaxParallelTools = 0);

internal sealed record EffectiveRunBehavior(
    string AgentId,
    string AgentName,
    string ProviderId,
    string ProviderType,
    string ProviderBaseUrl,
    string ModelId,
    string ModelName,
    EffectivePersonaProfile Persona,
    EffectiveRuntimeGrounding RuntimeGrounding,
    EffectiveSamplingPolicy SamplingPolicy,
    EffectiveSystemContextSection[] SystemContextSections,
    string CombinedSystemContext,
    string SerializedModelVisibleContext,
    string SkillAdvertisingContext,
    string ScopeContractContext,
    string MemoryContext,
    int MemoryFactCount,
    EffectiveToolingSnapshot Tooling);
