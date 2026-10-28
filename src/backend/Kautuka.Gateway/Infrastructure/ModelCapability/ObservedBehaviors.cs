namespace Kautuka.Gateway.Infrastructure.ModelCapability;

/// <summary>
/// Runtime observations accumulated from actual agent runs.
/// Used to adapt effective capabilities based on observed behavior.
/// </summary>
internal sealed record ObservedBehaviors
{
    /// <summary>Total number of completed agent runs observed.</summary>
    public int RunCount { get; init; }

    /// <summary>Runs where reasoning content was emitted but text response was empty.</summary>
    public int ReasoningWithEmptyTextCount { get; init; }

    /// <summary>Runs where streaming was attempted and succeeded.</summary>
    public int StreamingSuccessCount { get; init; }

    /// <summary>Runs where function calling was attempted and succeeded.</summary>
    public int FunctionCallingSuccessCount { get; init; }

    public double ReasoningEmptyTextRatio =>
        RunCount > 0 ? (double)ReasoningWithEmptyTextCount / RunCount : 0.0;

    public ObservedBehaviors WithReasoningEmptyText() => this with
    {
        RunCount = RunCount + 1,
        ReasoningWithEmptyTextCount = ReasoningWithEmptyTextCount + 1
    };

    public ObservedBehaviors WithNormalRun() => this with
    {
        RunCount = RunCount + 1
    };

    public ObservedBehaviors WithStreamingSuccess() => this with
    {
        StreamingSuccessCount = StreamingSuccessCount + 1
    };
}
