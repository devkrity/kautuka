namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>
/// Accumulated runtime observations for a specific provider path.
/// Counters are incremented atomically in the store; this view is read-only.
/// </summary>
internal sealed record ProviderPathObservation
{
    public required ProviderPathKey Key { get; init; }

    // --- Tool calling ---

    /// <summary>Successful native tool calls (function was invoked correctly).</summary>
    public int NativeToolSuccessCount { get; init; }

    /// <summary>Failed native tool calls (no function call returned or malformed).</summary>
    public int NativeToolFailureCount { get; init; }

    // --- Structured JSON ---

    /// <summary>Responses where strict JSON mode succeeded (valid object returned).</summary>
    public int StrictJsonSuccessCount { get; init; }

    /// <summary>Responses where relaxed JSON mode succeeded (parseable object returned).</summary>
    public int RelaxedJsonSuccessCount { get; init; }

    /// <summary>Responses where JSON parsing failed entirely.</summary>
    public int JsonFailureCount { get; init; }

    // --- Reasoning ---

    /// <summary>Responses where reasoning tokens were emitted but text content was empty.</summary>
    public int ReasoningOnlyCount { get; init; }

    // --- Vision ---

    /// <summary>Vision requests that were processed successfully.</summary>
    public int VisionSuccessCount { get; init; }

    /// <summary>Vision requests that produced an error or empty response.</summary>
    public int VisionFailureCount { get; init; }

    // --- Metadata ---

    public DateTimeOffset? FirstObservedAt { get; init; }

    public DateTimeOffset LastObservedAt { get; init; }

    // --- Computed ratios ---

    public int TotalToolAttempts => NativeToolSuccessCount + NativeToolFailureCount;

    public double NativeToolSuccessRate =>
        TotalToolAttempts > 0 ? (double)NativeToolSuccessCount / TotalToolAttempts : 0.0;

    public int TotalJsonAttempts => StrictJsonSuccessCount + RelaxedJsonSuccessCount + JsonFailureCount;

    public double StrictJsonSuccessRate =>
        TotalJsonAttempts > 0 ? (double)StrictJsonSuccessCount / TotalJsonAttempts : 0.0;

    public int TotalVisionAttempts => VisionSuccessCount + VisionFailureCount;

    public double VisionSuccessRate =>
        TotalVisionAttempts > 0 ? (double)VisionSuccessCount / TotalVisionAttempts : 0.0;
}
