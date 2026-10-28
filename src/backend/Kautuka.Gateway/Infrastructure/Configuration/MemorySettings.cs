namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Thresholds and limits governing memory fact persistence and injection.
/// Stored as individual key-value entries under the "memory.*" prefix in system_settings.
/// </summary>
internal sealed class MemorySettings
{
    /// <summary>
    /// Enables experimental embedding generation for persisted memory facts.
    /// When false, facts are still extracted and stored, but vectors are not generated.
    /// </summary>
    public bool EmbeddingsEnabled { get; init; }

    /// <summary>
    /// Minimum confidence score required for a memory fact to be persisted.
    /// Range 0-1. Default 0.7.
    /// </summary>
    public double FactPersistThreshold { get; init; } = 0.70;

    /// <summary>
    /// Minimum confidence score required for a persisted memory fact to be injected
    /// into the next request's context.
    /// Range 0-1. Default 0.6.
    /// </summary>
    public double FactInjectThreshold { get; init; } = 0.60;

    /// <summary>Maximum number of memory facts injected per request.</summary>
    public int MaxInjectedFacts { get; init; } = 10;

    /// <summary>
    /// Approximate token budget allocated to injected memory facts.
    /// Facts are trimmed to fit when the budget is exceeded.
    /// </summary>
    public int InjectionTokenBudget { get; init; } = 1024;

    /// <summary>
    /// Optional model id used for summarization-based session compaction.
    /// When unset, only non-LLM compaction strategies are available.
    /// </summary>
    public string? SessionCompactionModelId { get; init; }
}
