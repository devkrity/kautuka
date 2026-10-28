namespace Kautuka.Gateway.Domain.Execution;

internal sealed record NestedAgentExecutionContext(
    string TargetAgentId,
    string ChildSessionId,
    string CallerToolCallId,
    string CallerToolName,
    string CallerSessionId,
    string? CallerContinuationRunId,
    string UserId,
    string CallerRunId,
    string RootRunId);
