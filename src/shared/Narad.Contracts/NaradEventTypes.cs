namespace Narad.Contracts;

/// <summary>Canonical Narad authority event type strings persisted in the journal.</summary>
public static class NaradEventTypes
{
    public const string RunStarted = "run.started";
    public const string RunPaused = "run.paused";
    public const string RunResumed = "run.resumed";
    public const string RunStopRequested = "run.stop.requested";
    public const string RunFinished = "run.finished";
    public const string RunError = "run.error";
    public const string RunCancelled = "run.cancelled";

    public const string ToolCallStarted = "tool.call.started";
    public const string ToolCallFinished = "tool.call.finished";

    public const string InterruptRequested = "interrupt.requested";
    public const string InterruptResolved = "interrupt.resolved";

    public const string MessageUserRecorded = "message.user.recorded";
    public const string MessageTextStarted = "message.text.started";
    public const string MessageTextDelta = "message.text.delta";
    public const string MessageTextCompleted = "message.text.completed";

    public const string ReasoningStarted = "reasoning.started";
    public const string ReasoningDelta = "reasoning.delta";
    public const string ReasoningCompleted = "reasoning.completed";

    public const string ProgressRecorded = "progress.recorded";
    public const string ViewOpened = "view.opened";
    public const string TraceEvent = "trace.event";

    public const string SessionRevision = "session.revision";

    public const string GraphSnapshot = "graph.snapshot";
    public const string GraphDelta = "graph.delta";

    public const string WorkflowStarted = "workflow.started";
    public const string WorkflowPaused = "workflow.paused";
    public const string WorkflowResumed = "workflow.resumed";
    public const string WorkflowStopRequested = "workflow.stop.requested";
    public const string WorkflowFinished = "workflow.finished";
    public const string WorkflowError = "workflow.error";
    public const string WorkflowCancelled = "workflow.cancelled";
    public const string WorkflowStepStarted = "workflow.step.started";
    public const string WorkflowStepFinished = "workflow.step.finished";
    public const string WorkflowStepFailed = "workflow.step.failed";
    public const string WorkflowEdgeTaken = "workflow.edge.taken";
    public const string StructuredOutputExtracted = "structured_output.extracted";

    public static bool IsHydrateDerivedGraphEventType(string eventType)
        => eventType is GraphSnapshot or GraphDelta;

    public static bool IsRootRunTerminalEventType(string eventType)
        => eventType is RunFinished or RunError or RunCancelled;

    public static bool IsRunLifecycleEventType(string eventType)
        => eventType is RunStarted or RunPaused or RunResumed or RunStopRequested
            or RunFinished or RunError or RunCancelled;

    public static bool RequiresRunTreeScope(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType.StartsWith("run.", StringComparison.Ordinal)
            || eventType.StartsWith("tool.", StringComparison.Ordinal)
            || eventType.StartsWith("interrupt.", StringComparison.Ordinal)
            || eventType == ProgressRecorded
            || eventType == TraceEvent;
    }

    /// <summary>
    /// Child AAAT events that should be projected onto the parent conversation session.
    /// Excludes child transcript/reasoning so the parent chat does not grow a second user/assistant turn.
    /// </summary>
    public static bool ShouldProjectToParentConversation(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (eventType.StartsWith("message.", StringComparison.Ordinal)
            || eventType.StartsWith("reasoning.", StringComparison.Ordinal)
            || eventType == SessionRevision)
        {
            return false;
        }

        return eventType.StartsWith("run.", StringComparison.Ordinal)
            || eventType.StartsWith("tool.", StringComparison.Ordinal)
            || eventType.StartsWith("interrupt.", StringComparison.Ordinal)
            || eventType == ProgressRecorded
            || eventType == TraceEvent;
    }
}
