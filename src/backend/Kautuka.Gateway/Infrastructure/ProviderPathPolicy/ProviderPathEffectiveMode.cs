namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>
/// Snapshot of the effective compatibility modes resolved for a provider path,
/// derived from observations + policy thresholds. Cached alongside observations.
/// </summary>
internal sealed record ProviderPathEffectiveMode
{
    public required ProviderPathKey Key { get; init; }

    public ToolCompatibilityMode ToolMode { get; init; } = ToolCompatibilityMode.Native;

    public JsonCompatibilityMode JsonMode { get; init; } = JsonCompatibilityMode.Relaxed;

    public VisionCompatibilityMode VisionMode { get; init; } = VisionCompatibilityMode.Disabled;

    public ReasoningCompatibilityMode ReasoningMode { get; init; } = ReasoningCompatibilityMode.TextFirst;

    public DateTimeOffset ResolvedAt { get; init; }
}
