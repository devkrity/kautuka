using System.Text.Json;
using System.Threading.Channels;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Domain → Narad pure-map journal tests. Domain stream is driven explicitly (and LLM turns
/// are scripted from real AAAT parent-session shapes). Assertions are event types and
/// open/close rules from agent-interaction-protocol-kautuka-adoption.md — not model sampling.
/// </summary>
public sealed class NaradDomainEventMapJournalTests
{
    [Fact]
    public async Task ApprovalDomainPairMapsToInterruptAndPauseOnly()
    {
        JournalHarness harness = new();
        await harness.WriteAsync(new GatewayChatApprovalRequest(
            RequestId: "ficc_call_s1toof1p",
            ToolCallId: "call_s1toof1p",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: """{"message":"d44"}""",
            RunId: harness.RunId));

        // Pending approval StreamDone is not a second lifecycle — map silences it.
        await harness.WriteAsync(new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            harness.AgentId,
            harness.SessionId,
            string.Empty,
            false,
            new GatewayMetrics(0, 0, 0),
            Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval))));

        Assert.Equal(
            [NaradEventTypes.InterruptRequested, NaradEventTypes.RunPaused],
            harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunFinished, harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunResumed, harness.EventTypes);
    }

    [Fact]
    public async Task PackedApprovalDomainMapsToOneInterruptThenPause()
    {
        JournalHarness harness = new();
        await harness.WriteAsync(new GatewayChatApprovalRequest(
            RequestId: "ficc_16533c9b",
            ToolCallId: "call_16533c9b",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: """{"message":"d20"}""",
            RunId: harness.RunId,
            ToolType: "agent",
            PauseRun: true));

        Assert.Equal(
            [NaradEventTypes.InterruptRequested, NaradEventTypes.RunPaused],
            harness.EventTypes);
        JsonElement interruptBody = NaradPersistedEventReader.ParseBody(
            harness.Events.Single(static e => e.EventType == NaradEventTypes.InterruptRequested));
        JsonElement pauseBody = NaradPersistedEventReader.ParseBody(
            harness.Events.Single(static e => e.EventType == NaradEventTypes.RunPaused));
        Assert.Equal("wait:ficc_16533c9b", interruptBody.GetProperty("waitId").GetString());
        Assert.Equal("ficc_16533c9b", interruptBody.GetProperty("interruptId").GetString());
        Assert.Equal("call_16533c9b", interruptBody.GetProperty("input").GetProperty("toolCallId").GetString());
        Assert.False(interruptBody.GetProperty("input").TryGetProperty("packedApprovals", out _));
        Assert.Equal("wait:ficc_16533c9b", pauseBody.GetProperty("waitId").GetString());
        Assert.Equal("ficc_16533c9b", pauseBody.GetProperty("interruptId").GetString());
    }

    [Fact]
    public async Task ApprovalResumeMapsToRunResumedThenWorkContinues()
    {
        JournalHarness harness = new();
        await harness.WriteAsync(new GatewayChatApprovalRequest(
            RequestId: "ficc_call_s1toof1p",
            ToolCallId: "call_s1toof1p",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: """{"message":"d44"}""",
            RunId: harness.RunId));

        await harness.WriteAsync(new GatewayChatContinuationResumed(
            CheckpointId: "checkpoint-1",
            ContinuationRunId: harness.RunId,
            CheckpointIndex: 1,
            BoundaryType: RunWaitReasons.Approval,
            WaitId: "wait:ficc_call_s1toof1p"));

        await harness.WriteAsync(new GatewayChatToolCall(
            "call_s1toof1p",
            "invoke_agent_roller",
            """{"message":"d44"}""",
            "agent"));

        Assert.Equal(
            [
                NaradEventTypes.InterruptRequested,
                NaradEventTypes.RunPaused,
                NaradEventTypes.RunResumed,
                NaradEventTypes.ToolCallStarted,
            ],
            harness.EventTypes);

        JsonElement resumeBody = NaradPersistedEventReader.ParseBody(
            harness.Events.Single(static e => e.EventType == NaradEventTypes.RunResumed));
        Assert.Equal("wait:ficc_call_s1toof1p", resumeBody.GetProperty("waitId").GetString());
    }

    [Fact]
    public async Task NestedChildWaitDoesNotPublishParentRunLifecycle()
    {
        JournalHarness harness = new();

        // Parent tool open — domain truth while child does HITL.
        await harness.WriteAsync(new GatewayChatToolCall(
            "call_s1toof1p",
            "invoke_agent_roller",
            """{"message":"d44"}""",
            "agent"));

        // Produce ends on child wait: durable wait/invocation only — no Narad run.*.
        await harness.WriteAsync(new GatewayChatProduceBoundarySuspended(RunWaitReasons.Child));

        Assert.Equal([NaradEventTypes.ToolCallStarted], harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunPaused, harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunResumed, harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunFinished, harness.EventTypes);
        Assert.DoesNotContain(NaradEventTypes.RunError, harness.EventTypes);
    }

    [Fact]
    public async Task ForbiddenWaitingOnChildStreamDoneThrowsInsteadOfMappingToRunFinished()
    {
        JournalHarness harness = new();
        await harness.WriteAsync(new GatewayChatToolCall(
            "call_s1toof1p",
            "invoke_agent_roller",
            """{"message":"d44"}""",
            "agent"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await harness.WriteAsync(new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                harness.AgentId,
                harness.SessionId,
                string.Empty,
                false,
                new GatewayMetrics(0, 0, 0),
                ToolCalls:
                [
                    new GatewayToolCall(
                        "call_s1toof1p",
                        "invoke_agent_roller",
                        harness.AgentId,
                        """{"message":"d44"}""",
                        ResultJson: null,
                        Status: "calling",
                        ActionKind: "agent"),
                ],
                Run: new GatewayRunState(GatewayRunPhases.Executing, WaitStateReason: RunWaitReasons.Child)))));

        Assert.Equal([NaradEventTypes.ToolCallStarted], harness.EventTypes);
    }

    [Fact]
    public async Task ParentAaatDomainScriptMapsToCorrectedParentJournalShape()
    {
        // Corrected parent-lane shape from session-b002 (roll a d44): outer approve,
        // invoke_agent open, child wait without parent terminal/resume invent,
        // tool finish, assistant text, run.finished.
        JournalHarness harness = new();
        ScriptedOllamaChatClient ollama = ScriptedOllamaChatClient.CreateRollerParentScript();

        IReadOnlyList<ChatResponseUpdate> firstModelTurn = await DrainTurnAsync(ollama).ConfigureAwait(true);
        FunctionCallContent functionCall = Assert.IsType<FunctionCallContent>(
            Assert.Single(firstModelTurn[0].Contents));
        Assert.Equal("invoke_agent_roller", functionCall.Name);
        Assert.Equal("d44", functionCall.Arguments?["message"]?.ToString());

        await harness.WriteAsync(new GatewayChatApprovalRequest(
            RequestId: $"ficc_{functionCall.CallId}",
            ToolCallId: functionCall.CallId,
            ToolName: functionCall.Name,
            ArgumentsJson: JsonSerializer.Serialize(functionCall.Arguments),
            RunId: harness.RunId));
        await harness.WriteAsync(new GatewayChatStreamDone(new GatewayChatResult(
            "agent", harness.AgentId, harness.SessionId, string.Empty, false,
            new GatewayMetrics(0, 0, 0),
            Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval))));

        await harness.WriteAsync(new GatewayChatContinuationResumed(
            "checkpoint-outer",
            harness.RunId,
            1,
            RunWaitReasons.Approval,
            $"wait:ficc_{functionCall.CallId}"));

        await harness.WriteAsync(new GatewayChatToolCall(
            functionCall.CallId,
            functionCall.Name!,
            JsonSerializer.Serialize(functionCall.Arguments),
            "agent"));

        // Nested HITL: parent produce suspends — durable child wait only.
        await harness.WriteAsync(new GatewayChatProduceBoundarySuspended(RunWaitReasons.Child));

        // After child completes, continue: tool result then model answer (scripted).
        await harness.WriteAsync(new GatewayChatToolResult(functionCall.CallId, """{"result":"17"}"""));

        IReadOnlyList<ChatResponseUpdate> answerTurn = await DrainTurnAsync(ollama).ConfigureAwait(true);
        string answerText = string.Concat(
            answerTurn.SelectMany(static u => u.Contents.OfType<TextContent>().Select(static t => t.Text)));
        Assert.Contains("17", answerText, StringComparison.Ordinal);
        Assert.Contains("d44", answerText, StringComparison.Ordinal);

        foreach (char ch in answerText)
        {
            await harness.WriteAsync(new GatewayChatTokenDelta(ch.ToString(), IsThinking: false));
        }

        await harness.WriteAsync(new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            harness.AgentId,
            harness.SessionId,
            answerText,
            false,
            new GatewayMetrics(1, 1, 1),
            ToolCalls:
            [
                new GatewayToolCall(
                    functionCall.CallId,
                    functionCall.Name!,
                    harness.AgentId,
                    JsonSerializer.Serialize(functionCall.Arguments),
                    """{"result":"17"}""",
                    "success",
                    "agent"),
            ],
            Run: new GatewayRunState(GatewayRunPhases.Completed))));

        Assert.Equal(0, ollama.RemainingTurns);
        Assert.Equal(2, ollama.CompletedTurns);

        // Core parent map invariants (AAAT matrix).
        Assert.Equal(
            [
                NaradEventTypes.InterruptRequested,
                NaradEventTypes.RunPaused,
                NaradEventTypes.RunResumed,
                NaradEventTypes.ToolCallStarted,
                NaradEventTypes.ToolCallFinished,
                NaradEventTypes.MessageTextStarted,
            ],
            harness.EventTypes.Take(6).ToArray());

        Assert.Contains(NaradEventTypes.RunFinished, harness.EventTypes);
        Assert.Equal(1, harness.EventTypes.Count(static t => t == NaradEventTypes.RunFinished));
        Assert.Equal(1, harness.EventTypes.Count(static t => t == NaradEventTypes.ToolCallStarted));
        Assert.Equal(1, harness.EventTypes.Count(static t => t == NaradEventTypes.ToolCallFinished));

        // Parent never got a second invent after child wait (no extra resume/pause without domain pair).
        int pauseCount = harness.EventTypes.Count(static t => t == NaradEventTypes.RunPaused);
        int resumeCount = harness.EventTypes.Count(static t => t == NaradEventTypes.RunResumed);
        Assert.Equal(1, pauseCount);
        Assert.Equal(1, resumeCount);

        // Terminal finish must not encode a child wait — waitStateReason null/absent is fine.
        foreach (NaradPersistedEvent finished in harness.Events.Where(static e => e.EventType == NaradEventTypes.RunFinished))
        {
            JsonElement body = NaradPersistedEventReader.ParseBody(finished);
            if (body.TryGetProperty("run", out JsonElement run) &&
                run.TryGetProperty("waitStateReason", out JsonElement reason))
            {
                Assert.True(
                    reason.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
                    $"run.finished must not carry a wait reason (got {reason}).");
            }
        }

        Assert.Equal(NaradEventTypes.RunFinished, harness.EventTypes[^1]);
        Assert.Equal(NaradEventTypes.MessageTextCompleted, harness.EventTypes[^2]);
    }

    [Fact]
    public void ScriptedOllamaSeedMatchesRollerToolContractFromPastRuns()
    {
        ScriptedOllamaChatClient parent = ScriptedOllamaChatClient.CreateRollerParentScript("d44", "call_s1toof1p");
        ScriptedOllamaChatClient child = ScriptedOllamaChatClient.CreateRollerChildScript();

        Assert.Equal(2, parent.RemainingTurns);
        Assert.Equal(3, child.RemainingTurns);
        Assert.Equal("ollama-scripted", parent.Metadata.ProviderName);
    }

    private static async Task<IReadOnlyList<ChatResponseUpdate>> DrainTurnAsync(ScriptedOllamaChatClient client)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([], cancellationToken: CancellationToken.None)
            .ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates;
    }

    private sealed class JournalHarness
    {
        private readonly TestNaradEventPublisher publisher = new();
        private readonly AgentExecutionEventWriter eventWriter;
        private readonly Channel<NaradPersistedEvent> channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        private readonly AssistantMessageLifecycle lifecycle;
        private readonly NaradEventContext context;

        public JournalHarness()
        {
            AgentId = "agent-parent";
            SessionId = "session-parent";
            RunId = "run-parent";
            eventWriter = new AgentExecutionEventWriter(publisher);
            lifecycle = new AssistantMessageLifecycle($"assistant:{RunId}:{RunId}:{RunId}");
            context = new NaradEventContext(AgentId, SessionId, "user-1", RunId, RunId);
        }

        public string AgentId { get; }
        public string SessionId { get; }
        public string RunId { get; }
        public List<NaradPersistedEvent> Events { get; } = [];
        public IReadOnlyList<string> EventTypes => Events.Select(static e => e.EventType).ToArray();

        public async Task WriteAsync(GatewayChatStreamEvent domainEvent)
        {
            await eventWriter.WriteAsync(
                channel.Writer,
                domainEvent,
                context,
                lifecycle,
                CancellationToken.None).ConfigureAwait(false);
            while (channel.Reader.TryRead(out NaradPersistedEvent? persisted))
            {
                Events.Add(persisted);
            }
        }
    }
}
