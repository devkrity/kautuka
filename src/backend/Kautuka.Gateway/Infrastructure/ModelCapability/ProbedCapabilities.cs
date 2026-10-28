namespace Kautuka.Gateway.Infrastructure.ModelCapability;

/// <summary>
/// Capabilities determined by bounded active probes against the model.
/// More reliable than declared; trumps declared when present.
/// </summary>
internal sealed class ProbedCapabilities
{
    public bool? SupportsStreaming { get; init; }
    public bool? SupportsFunctionCalling { get; init; }
    public bool? SupportsStructuredOutput { get; init; }
    public bool? SupportsReasoningChannel { get; init; }
    public bool? ReasoningMayBeOnlyOutput { get; init; }
    public DateTimeOffset? ProbedAt { get; init; }
}
