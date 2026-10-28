using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Deterministic vertical workflow scenario: root conductor, sequential phase, parallel fan-out,
/// nested AAAT child with approval pause, partial sibling completion, join, and terminal aggregation.
/// </summary>
internal static class WorkflowSliceFixture
{
    public const string RootRunId = "workflow-root-run";
    public const string ThreadId = "session-1";
    public const string ChildRunId = "workflow-child-run";
    public const string NestedInvocationId = "workflow-invocation-1";
    public const string ParallelToolA = "parallel-call-a";
    public const string ParallelToolB = "parallel-call-b";
    public const string AgentToolCallId = "call-agent-child";
    public const string ChildToolCallId = "call-child-shell";
    public const string ChildApprovalId = "approval-child-shell";

    public static IReadOnlyList<NaradPersistedEvent> BuildThroughChildPause()
    {
        List<NaradPersistedEvent> events =
        [
            RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" }),
            RuntimeEvent(2, NaradEventTypes.ProgressRecorded, new { phase = "plan" }),
            RuntimeEvent(3, NaradEventTypes.ToolCallStarted,
                new GatewayChatToolCall(ParallelToolA, "read_file", "{}", "connector", ParentMessageId: "assistant-1"),
                toolCallId: ParallelToolA),
            RuntimeEvent(4, NaradEventTypes.ToolCallStarted,
                new GatewayChatToolCall(ParallelToolB, "read_env", "{}", "connector", ParentMessageId: "assistant-1"),
                toolCallId: ParallelToolB),
            RuntimeEvent(5, NaradEventTypes.ToolCallFinished,
                new GatewayChatToolResult(ParallelToolA, "{}"),
                toolCallId: ParallelToolA),
            RuntimeEvent(6, NaradEventTypes.ToolCallStarted,
                new GatewayChatToolCall(AgentToolCallId, "invoke_agent_child", "{\"task\":\"inspect\"}", "agent",
                    NestedAgentId: "child-agent", NestedSessionId: "child-session", ParentMessageId: "assistant-1"),
                toolCallId: AgentToolCallId),
            RuntimeEvent(8, NaradEventTypes.RunStarted, new { trigger = "agent_tool" },
                runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(9, NaradEventTypes.ToolCallStarted,
                new GatewayChatToolCall(ChildToolCallId, "run_shell", "{\"command\":\"ls\"}", "skill",
                    ParentToolCallId: AgentToolCallId, ParentMessageId: "assistant-child"),
                runId: ChildRunId, toolCallId: ChildToolCallId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(10, NaradEventTypes.InterruptRequested,
                new GatewayChatApprovalRequest(ChildApprovalId, ChildToolCallId, "run_shell", "{\"command\":\"ls\"}", RunId: ChildRunId),
                runId: ChildRunId, toolCallId: ChildToolCallId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(11, NaradEventTypes.RunPaused, new { reason = "tool_approval", checkpointId = "checkpoint-child" },
                runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(12, NaradEventTypes.RunPaused, new { reason = "child_approval", checkpointId = "checkpoint-root" }),
        ];
        return events;
    }

    public static IReadOnlyList<NaradPersistedEvent> BuildThroughPartialJoin()
    {
        List<NaradPersistedEvent> events = [.. BuildThroughChildPause()];
        events.Add(RuntimeEvent(13, NaradEventTypes.ToolCallFinished,
            new JsonObject
            {
                ["toolCallId"] = ParallelToolB,
                ["toolName"] = "read_env",
                ["outcome"] = "error",
                ["message"] = "failed",
            },
            toolCallId: ParallelToolB));
        return events;
    }

    public static IReadOnlyList<NaradPersistedEvent> BuildThroughTerminalAggregation()
    {
        List<NaradPersistedEvent> events = [.. BuildThroughPartialJoin()];
        events.AddRange(
        [
            RuntimeEvent(14, NaradEventTypes.InterruptResolved, new
            {
                approvalRequestId = ChildApprovalId,
                toolCallId = ChildToolCallId,
                decision = "approved",
            }, runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(15, NaradEventTypes.RunResumed, NaradEventBodies.RunResumed($"wait:{ChildApprovalId}"),
                runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(16, NaradEventTypes.ToolCallFinished,
                new GatewayChatToolResult(ChildToolCallId, "{}"),
                runId: ChildRunId, toolCallId: ChildToolCallId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(17, NaradEventTypes.RunFinished, new { result = "done" },
                runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId),
            RuntimeEvent(18, NaradEventTypes.ToolCallFinished,
                new GatewayChatToolResult(AgentToolCallId, "{\"result\":\"done\"}"),
                toolCallId: AgentToolCallId),
            RuntimeEvent(19, NaradEventTypes.RunFinished, new { result = "done" }),
        ]);
        return events;
    }

    public static IReadOnlyList<NaradPersistedEvent> BuildThroughFailedChildInvocation()
    {
        List<NaradPersistedEvent> events = [.. BuildThroughPartialJoin()];
        events.Add(RuntimeEvent(14, NaradEventTypes.RunError, new { code = "failed", message = "Child agent failed." },
            runId: ChildRunId, rootRunId: RootRunId, parentToolCallId: AgentToolCallId));
        events.Add(RuntimeEvent(15, NaradEventTypes.ToolCallFinished,
            new JsonObject
            {
                ["toolCallId"] = AgentToolCallId,
                ["toolName"] = "invoke_agent_child",
                ["outcome"] = "error",
                ["message"] = "Child agent failed.",
            },
            toolCallId: AgentToolCallId));
        return events;
    }

    private static NaradPersistedEvent RuntimeEvent(
        long sequence,
        string eventType,
        object payload,
        string runId = RootRunId,
        string? rootRunId = null,
        string? parentToolCallId = null,
        string? toolCallId = null)
    {
        string effectiveRootRunId = rootRunId ?? RootRunId;
        JsonElement body = payload switch
        {
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, KautukaJsonContext.Default.JsonObject),
            _ => JsonSerializer.SerializeToElement(payload),
        };

        if (toolCallId is not null && body.ValueKind == JsonValueKind.Object)
        {
            var enriched = JsonNode.Parse(body.GetRawText())!.AsObject();
            enriched["toolCallId"] ??= toolCallId;
            body = JsonSerializer.SerializeToElement(enriched, KautukaJsonContext.Default.JsonObject);
        }

        if (parentToolCallId is not null && body.ValueKind == JsonValueKind.Object)
        {
            var enriched = JsonNode.Parse(body.GetRawText())!.AsObject();
            enriched["parentToolCallId"] ??= parentToolCallId;
            body = JsonSerializer.SerializeToElement(enriched, KautukaJsonContext.Default.JsonObject);
        }

        return NaradTestEventFactory.FromBody(
            eventType,
            body,
            ThreadId,
            runId,
            effectiveRootRunId,
            sequence,
            sequence);
    }
}
