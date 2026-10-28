namespace Kautuka.Gateway.Infrastructure.ModelCapability;

/// <summary>
/// Merged runtime decisions derived from declared + probed + observed sources.
/// This is what the runtime actually uses — never the raw layers directly.
/// </summary>
internal sealed class EffectiveCapabilities
{
    public bool SupportsStreaming { get; init; } = true;
    public bool SupportsFunctionCalling { get; init; } = true;
    public bool SupportsStructuredOutput { get; init; }
    public bool SupportsReasoningChannel { get; init; }

    /// <summary>
    /// When true: reasoning tokens may constitute the entire model output,
    /// with only an empty string as the final text response.
    /// The runtime must promote buffered reasoning to the response in this case.
    /// </summary>
    public bool ReasoningMayBeOnlyOutput { get; init; }

    public static EffectiveCapabilities Merge(
        DeclaredCapabilities declared,
        ProbedCapabilities probed,
        ObservedBehaviors observed)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(probed);
        ArgumentNullException.ThrowIfNull(observed);

        // Probed beats declared; observed adapts the remaining unknowns.
        // If we have confirmed streaming successes at runtime, trust that over any uncertain probe result.
        bool supportsStreaming = observed.StreamingSuccessCount > 0
            ? observed.StreamingSuccessCount > 0
            : (probed.SupportsStreaming ?? declared.SupportsStreaming ?? true);

        bool supportsFunctionCalling = probed.SupportsFunctionCalling
            ?? declared.SupportsFunctionCalling
            ?? true;

        bool supportsStructuredOutput = probed.SupportsStructuredOutput
            ?? declared.SupportsStructuredOutput
            ?? false;

        bool supportsReasoningChannel = probed.SupportsReasoningChannel
            ?? declared.SupportsReasoningChannel
            ?? (declared.IsThinkingModel == true);

        // ReasoningMayBeOnlyOutput: set if probed, declared as thinking model,
        // or observed ratio >= 0.5 with minimum 2 runs.
        bool reasoningMayBeOnlyOutput = probed.ReasoningMayBeOnlyOutput
            ?? (declared.IsThinkingModel == true)
            || (observed.RunCount >= 2 && observed.ReasoningEmptyTextRatio >= 0.5);

        return new EffectiveCapabilities
        {
            SupportsStreaming = supportsStreaming,
            SupportsFunctionCalling = supportsFunctionCalling,
            SupportsStructuredOutput = supportsStructuredOutput,
            SupportsReasoningChannel = supportsReasoningChannel,
            ReasoningMayBeOnlyOutput = reasoningMayBeOnlyOutput
        };
    }
}
