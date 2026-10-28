using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ChatExecutionHelpersSkillAdvertisingTests
{
    [Fact]
    public void BuildSkillAdvertisingContextReturnsEmptyWhenNoSkillsAreAvailable()
    {
        Assert.Equal(string.Empty, ChatExecutionHelpers.BuildSkillAdvertisingContext([]));
    }

    [Fact]
    public void BuildSkillAdvertisingContextListsSkillNamesDescriptionsAndProgressiveDisclosureGuidance()
    {
        IReadOnlyList<EffectiveSkillManifestEntry> manifest =
        [
            new EffectiveSkillManifestEntry
            {
                SkillId = "roll-dice",
                Name = "Roll Dice",
                Description = "Roll dice when the user asks for d6, d20, or other die results.",
                SourceScope = EffectiveSkillSourceScope.Agent,
                Mode = SkillAttachmentMode.Trusted,
                HasScripts = true,
            },
            new EffectiveSkillManifestEntry
            {
                SkillId = "expense-policy",
                Name = "Expense Policy",
                Description = "Answer expense policy questions and validate reimbursement workflows.",
                SourceScope = EffectiveSkillSourceScope.Agent,
                Mode = SkillAttachmentMode.Trusted,
                HasResources = true,
            },
        ];

        string context = ChatExecutionHelpers.BuildSkillAdvertisingContext(manifest);

        Assert.Contains("Skills (NOT directly callable", context, StringComparison.Ordinal);
        Assert.Contains("- Expense Policy (skillId: \"expense-policy\"): Answer expense policy questions and validate reimbursement workflows.", context, StringComparison.Ordinal);
        Assert.Contains("- Roll Dice (skillId: \"roll-dice\"): Roll dice when the user asks for d6, d20, or other die results.", context, StringComparison.Ordinal);
        Assert.Contains("load_skill", context, StringComparison.Ordinal);
        Assert.Contains("Use read_skill_resource only when you need supplementary files from a skill.", context, StringComparison.Ordinal);
        Assert.Contains("run_skill_script", context, StringComparison.Ordinal);
    }

    [Fact]
    public void CombineSystemContextPlacesSkillAdvertisingAheadOfModelVisibleContextAndMemory()
    {
        string combined = ChatExecutionHelpers.CombineSystemContext(
            "Base prompt",
            "Memory facts",
            "Model visible context",
            "Available skills:\n- Roll Dice (skillId: roll-dice): Roll dice.");

        Assert.Equal(
            "Base prompt\n\nAvailable skills:\n- Roll Dice (skillId: roll-dice): Roll dice.\n\nModel visible context\n\nMemory facts",
            combined);
    }

    [Fact]
    public void BuildScopeContractContextTellsAgentToDeclineOutOfScopeRequests()
    {
        string contract = ChatExecutionHelpers.BuildScopeContractContext("office_worker", "Office Agent");

        Assert.Contains("Operate only within the responsibilities implied by persona category office_worker", contract, StringComparison.Ordinal);
        Assert.Contains("decline instead of improvising or pretending completion", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExecutedActionsFromToolCallsPreservesAtomicToolIdentity()
    {
        GatewayToolCall[] toolCalls =
        [
            new GatewayToolCall("call-1", "invoke_agent_home_assistant", null, "{}", "{}", "done", "agent"),
            new GatewayToolCall("call-2", "kautuka_web_fetch", null, "{}", "{}", "done", "connector"),
        ];

        IReadOnlyList<GatewayExecutedAction> executedActions = ChatExecutionHelpers.BuildExecutedActionsFromToolCalls(toolCalls);

        Assert.Collection(
            executedActions,
            step => Assert.Equal(["call-1"], step.ToolCallIds),
            step => Assert.Equal(["call-2"], step.ToolCallIds));
    }

    [Fact]
    public void RecordedToolCallStartAndResultBuildCompletedActivityWithoutParentTimestampLeak()
    {
        var toolCalls = new List<GatewayToolCall>();

        ChatExecutionHelpers.RecordToolCallStart(
            toolCalls,
            "call-roller",
            "invoke_agent_roller",
            "agent-a",
            """{"message":"roll a d100"}""");
        ChatExecutionHelpers.RecordToolCallResult(
            toolCalls,
            "call-roller",
            """{"result":"14"}""");

        GatewayToolCall toolCall = Assert.Single(toolCalls);
        Assert.Equal("done", toolCall.Status);
        Assert.Null(toolCall.ParentToolCallId);
        Assert.False(string.IsNullOrWhiteSpace(toolCall.StartedAt));
        Assert.False(string.IsNullOrWhiteSpace(toolCall.CompletedAt));

        GatewayExecutedAction action = Assert.Single(ChatExecutionHelpers.BuildExecutedActionsFromToolCalls(toolCalls));
        Assert.Equal("completed", action.Status);
        Assert.Equal("invoke_agent_roller", action.Name);

        GatewayActivityEntry activity = Assert.Single(ChatExecutionHelpers.BuildActivityEntries([action]));
        Assert.Equal("completed", activity.Status);
        Assert.Equal("invoke_agent_roller", activity.Name);
        Assert.Equal("""{"result":"14"}""", activity.ResultJson);
    }

    [Fact]
    public void RecordToolCallResultRejectsResultWithoutCanonicalStart()
    {
        var toolCalls = new List<GatewayToolCall>();

        bool recorded = ChatExecutionHelpers.RecordToolCallResult(
            toolCalls,
            "call-orphaned-by-framework",
            "Error: Function failed.");

        Assert.False(recorded);
        Assert.Empty(toolCalls);
    }

    [Fact]
    public void RecordToolCallResultRejectsDuplicateTerminalResult()
    {
        var toolCalls = new List<GatewayToolCall>();
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-1", "tool-1", "agent-1", "{}");
        Assert.True(ChatExecutionHelpers.RecordToolCallResult(toolCalls, "call-1", "first"));

        bool recorded = ChatExecutionHelpers.RecordToolCallResult(toolCalls, "call-1", "second");

        Assert.False(recorded);
        Assert.Equal("first", Assert.Single(toolCalls).ResultJson);
    }

    [Fact]
    public void RecordToolCallFailureCreatesFailedActionAndRejectsLaterCompletion()
    {
        var toolCalls = new List<GatewayToolCall>();
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-1", "run_skill_script", "agent-1", "{}");

        Assert.True(ChatExecutionHelpers.RecordToolCallFailure(
            toolCalls,
            "call-1",
            "{\"code\":\"tool_execution_failed\",\"message\":\"Tool execution failed.\"}"));
        Assert.False(ChatExecutionHelpers.RecordToolCallResult(toolCalls, "call-1", "late-success"));

        GatewayToolCall failedCall = Assert.Single(toolCalls);
        Assert.Equal("failed", failedCall.Status);
        Assert.False(string.IsNullOrWhiteSpace(failedCall.CompletedAt));
        GatewayExecutedAction action = Assert.Single(ChatExecutionHelpers.BuildExecutedActionsFromToolCalls(toolCalls));
        Assert.Equal("failed", action.Status);
    }

    [Fact]
    public void FunctionResultFailureClassificationUsesExceptionInsteadOfResultText()
    {
        var plainResult = new FunctionResultContent("call-1", "Error: Function failed.");
        var failure = new InvalidOperationException("private execution detail");
        var failedResult = new FunctionResultContent("call-2", "Error: Function failed.")
        {
            Exception = failure,
        };

        Assert.False(ChatExecutionHelpers.TryGetFunctionFailure(plainResult, out Exception? plainException));
        Assert.Null(plainException);
        Assert.True(ChatExecutionHelpers.TryGetFunctionFailure(failedResult, out Exception? captured));
        Assert.Same(failure, captured);
    }

    [Fact]
    public void UnrecoveredToolFailureAllowsSuccessfulAttemptStartedAfterFailure()
    {
        var toolCalls = new List<GatewayToolCall>();
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-1", "run_skill_script", "agent-1", "{}");
        ChatExecutionHelpers.RecordToolCallFailure(toolCalls, "call-1", "failure");
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-2", "run_skill_script", "agent-1", "{}");
        ChatExecutionHelpers.RecordToolCallResult(toolCalls, "call-2", "success");

        Assert.False(ChatExecutionHelpers.HasUnrecoveredToolFailure(toolCalls));
        Assert.True(ChatExecutionHelpers.CanEmitAssistantOutput(toolCalls));
    }

    [Fact]
    public void ConcurrentSameToolCompletionDoesNotMaskFailure()
    {
        var toolCalls = new List<GatewayToolCall>();
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-1", "run_skill_script", "agent-1", "{}");
        ChatExecutionHelpers.RecordToolCallStart(toolCalls, "call-2", "run_skill_script", "agent-1", "{}");
        ChatExecutionHelpers.RecordToolCallFailure(toolCalls, "call-1", "failure");
        ChatExecutionHelpers.RecordToolCallResult(toolCalls, "call-2", "success");

        Assert.True(ChatExecutionHelpers.HasUnrecoveredToolFailure(toolCalls));
        Assert.False(ChatExecutionHelpers.CanEmitAssistantOutput(toolCalls));
    }

    [Fact]
    public void PersistedToolAttemptsRecoverOnlyWithStrictlyLaterStart()
    {
        GatewayToolCall failed = new(
            "call-1", "run_skill_script", "agent-1", "{}", "failure", "failed", "skill",
            StartedAt: "2026-07-10T10:00:00.0000000Z",
            CompletedAt: "2026-07-10T10:00:02.0000000Z");
        GatewayToolCall concurrentSuccess = new(
            "call-2", "run_skill_script", "agent-1", "{}", "success", "done", "skill",
            StartedAt: "2026-07-10T10:00:01.0000000Z",
            CompletedAt: "2026-07-10T10:00:03.0000000Z");
        GatewayToolCall retrySuccess = concurrentSuccess with
        {
            ToolCallId = "call-3",
            StartedAt = "2026-07-10T10:00:03.0000000Z",
            CompletedAt = "2026-07-10T10:00:04.0000000Z",
        };

        Assert.True(ChatExecutionHelpers.HasUnrecoveredToolFailure([RoundTrip(failed), RoundTrip(concurrentSuccess)]));
        Assert.False(ChatExecutionHelpers.HasUnrecoveredToolFailure([RoundTrip(failed), RoundTrip(retrySuccess)]));
    }

    [Fact]
    public void ToolAttemptOrdinalsAreNotSerialized()
    {
        var call = new GatewayToolCall(
            "call-1", "run_skill_script", "agent-1", "{}", "success", "done", "skill",
            AttemptOrdinal: 4,
            TerminalOrdinal: 5);

        string json = JsonSerializer.Serialize(call, KautukaJsonContext.Default.GatewayToolCall);

        Assert.DoesNotContain("attemptOrdinal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("terminalOrdinal", json, StringComparison.Ordinal);
        GatewayToolCall restored = Assert.IsType<GatewayToolCall>(
            JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayToolCall));
        Assert.Equal(0, restored.AttemptOrdinal);
        Assert.Null(restored.TerminalOrdinal);
    }

    [Fact]
    public void BuildActivityEntriesPreservesExecutedActionOrderInsteadOfTimestampOrder()
    {
        GatewayExecutedAction[] actions =
        [
            new GatewayExecutedAction(
                "action-second-by-time",
                "agent",
                "invoke_agent_roller",
                "completed",
                ToolCallIds: ["call-roller"],
                StartedAt: "2026-07-10T10:00:02.0000000Z",
                CompletedAt: "2026-07-10T10:00:03.0000000Z"),
            new GatewayExecutedAction(
                "action-first-by-time",
                "skill",
                "run_skill_script",
                "completed",
                ToolCallIds: ["call-skill"],
                StartedAt: "2026-07-10T10:00:00.0000000Z",
                CompletedAt: "2026-07-10T10:00:01.0000000Z"),
        ];

        IReadOnlyList<GatewayActivityEntry> activity = ChatExecutionHelpers.BuildActivityEntries(actions);

        Assert.Collection(
            activity,
            entry =>
            {
                Assert.Equal("invoke_agent_roller", entry.Name);
                Assert.Equal(10, entry.Sequence);
            },
            entry =>
            {
                Assert.Equal("run_skill_script", entry.Name);
                Assert.Equal(20, entry.Sequence);
            });
    }

    [Fact]
    public void BuildActivityEntriesPlacesApprovalBeforeGatedAction()
    {
        GatewayExecutedAction[] actions =
        [
            new GatewayExecutedAction(
                "action-roller",
                "agent",
                "invoke_agent_roller",
                "completed",
                ToolCallIds: ["call-roller"]),
        ];
        GatewayToolApprovalRequest[] approvals =
        [
            new GatewayToolApprovalRequest(
                "approval-roller",
                "session-1",
                "run-1",
                null,
                "agent-1",
                "user-1",
                "call-roller",
                "invoke_agent_roller",
                "agent",
                null,
                """{"message":"roll a d100"}""",
                "2026-07-10T10:00:00.0000000Z",
                "approved",
                "user-1",
                "2026-07-10T10:00:01.0000000Z",
                "chat-ui",
                null),
        ];

        IReadOnlyList<GatewayActivityEntry> activity = ChatExecutionHelpers.BuildActivityEntries(actions, approvals);

        Assert.Collection(
            activity,
            entry =>
            {
                Assert.Equal("approval", entry.Kind);
                Assert.Equal("approval-roller", entry.ApprovalRequestId);
                Assert.Equal(10, entry.Sequence);
                Assert.Equal("call-roller", entry.ToolCallId);
            },
            entry =>
            {
                Assert.Equal("action", entry.Kind);
                Assert.Equal("invoke_agent_roller", entry.Name);
                Assert.Equal(20, entry.Sequence);
            });
    }

    [Fact]
    public void CombineSystemContextIsStableAcrossRepeatedCallsForSameInputs()
    {
        string[] results = Enumerable.Range(0, 5)
            .Select(_ => ChatExecutionHelpers.CombineSystemContext(
                "Base prompt",
                "Memory facts",
                "Model visible context",
                "Available skills",
                "Scope contract"))
            .ToArray();

        Assert.All(results, item => Assert.Equal(results[0], item));
    }

    [Fact]
    public void SummarizeToolResultForWireReplacesOversizedSkillLoadPayload()
    {
        string hugeResult = $$"""{"skill_id":"roll-dice","instructions_markdown":"{{new string('x', 2000)}}"}""";

        string summarized = ChatExecutionHelpers.SummarizeToolResultForWire(
            "load_skill",
            hugeResult,
            """{"skillId":"roll-dice"}""");

        Assert.Equal("""{"skill_id":"roll-dice","status":"loaded"}""", summarized);
    }

    [Fact]
    public void SummarizeToolResultForWireDetectsSkillPayloadWithoutToolName()
    {
        string hugeResult = $$"""{"skill_id":"roll-dice","package_hash":"abc","instructions_markdown":"{{new string('x', 2000)}}"}""";

        string summarized = ChatExecutionHelpers.SummarizeToolResultForWire(null, hugeResult);

        Assert.Equal("""{"skill_id":"roll-dice","status":"loaded"}""", summarized);
    }

    private static GatewayToolCall RoundTrip(GatewayToolCall call)
        => JsonSerializer.Deserialize(
            JsonSerializer.Serialize(call, KautukaJsonContext.Default.GatewayToolCall),
            KautukaJsonContext.Default.GatewayToolCall)!;
}
