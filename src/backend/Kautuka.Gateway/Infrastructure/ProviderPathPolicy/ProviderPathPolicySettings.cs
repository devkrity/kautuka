namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>
/// Policy thresholds and limits governing provider-path adaptation behaviour.
/// Stored as individual key-value entries under the "provider_path.*" prefix in system_settings.
/// </summary>
internal sealed class ProviderPathPolicySettings
{
    // --- Tool execution ---

    /// <summary>
    /// Native tool success rate required to promote a path from PromptedJson → Native.
    /// Range 0–1. Default 0.8 (80 % success over min observations).
    /// </summary>
    public double ToolNativePromotionThreshold { get; init; } = 0.80;

    /// <summary>
    /// Minimum tool-call observations before promotion/demotion decisions are made.
    /// </summary>
    public int ToolMinObservations { get; init; } = 5;

    /// <summary>
    /// Native tool success rate at which a path is demoted from Native → PromptedJson.
    /// Range 0–1. Default 0.4.
    /// </summary>
    public double ToolNativeDemotionThreshold { get; init; } = 0.40;

    // --- Structured JSON ---

    /// <summary>
    /// Strict JSON success rate required to promote a path from Relaxed → Strict.
    /// Range 0–1. Default 0.9.
    /// </summary>
    public double StructuredJsonStrictPromotionThreshold { get; init; } = 0.90;

    /// <summary>
    /// Minimum JSON observations before promotion decisions are made.
    /// </summary>
    public int StructuredJsonMinObservations { get; init; } = 5;

    // --- Vision ---

    /// <summary>
    /// Vision success rate threshold required to enable vision on a path.
    /// Range 0–1. Default 0.8.
    /// </summary>
    public double VisionEnableThreshold { get; init; } = 0.80;

    // --- Observation window ---

    /// <summary>
    /// Sliding window in days over which observations are considered fresh.
    /// Observations older than this are excluded from rate calculations.
    /// Default 30 days.
    /// </summary>
    public int ObservationDecayWindowDays { get; init; } = 30;

    // --- Global kill-switch ---

    /// <summary>
    /// When false, all provider-path adaptation is disabled and every path
    /// uses its default modes (Native/Relaxed/Disabled/TextFirst).
    /// </summary>
    public bool ProviderPathFallbackModeEnabled { get; init; } = true;
}
