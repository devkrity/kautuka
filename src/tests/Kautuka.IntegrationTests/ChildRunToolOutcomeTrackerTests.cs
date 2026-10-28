using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class ChildRunToolOutcomeTrackerTests
{
    [Fact]
    public void ConcurrentSameToolCompletionDoesNotRecoverFailure()
    {
        var tracker = new ChildRunToolOutcomeTracker();

        tracker.Observe(Start("call-1", "run_skill_script"));
        tracker.Observe(Start("call-2", "run_skill_script"));
        tracker.Observe(Failed("call-1", "run_skill_script"));
        tracker.Observe(Completed("call-2"));

        Assert.True(tracker.HasUnresolvedFailures);
    }

    [Fact]
    public void SuccessfulAttemptStartedAfterFailureRecoversIt()
    {
        var tracker = new ChildRunToolOutcomeTracker();

        tracker.Observe(Start("call-1", "run_skill_script"));
        tracker.Observe(Failed("call-1", "run_skill_script"));
        tracker.Observe(Start("call-2", "run_skill_script"));
        tracker.Observe(Completed("call-2"));

        Assert.False(tracker.HasUnresolvedFailures);
    }

    private static NaradPersistedEvent Start(string callId, string toolName)
        => ToolEvent(callId, NaradEventTypes.ToolCallStarted, new JsonObject { ["toolCallId"] = callId, ["toolName"] = toolName });

    private static NaradPersistedEvent Completed(string callId)
        => ToolEvent(callId, NaradEventTypes.ToolCallFinished, new JsonObject { ["toolCallId"] = callId, ["outcome"] = "success" });

    private static NaradPersistedEvent Failed(string callId, string toolName)
        => ToolEvent(callId, NaradEventTypes.ToolCallFinished, new JsonObject
        {
            ["toolCallId"] = callId,
            ["toolName"] = toolName,
            ["outcome"] = "error",
            ["code"] = "tool_execution_failed",
        });

    private static NaradPersistedEvent ToolEvent(string callId, string eventType, JsonObject body)
        => NaradTestSupport.AuthorityEvent(
            $"evt-{callId}-{eventType}",
            eventType,
            "session-1",
            1,
            "run-1",
            "run-1",
            1,
            body);
}
