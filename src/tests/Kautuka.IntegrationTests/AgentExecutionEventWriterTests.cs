using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using System.Threading.Channels;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class AgentExecutionEventWriterTests
{
    [Fact]
    public async Task ResumedWriterDoesNotPublishSecondStartForAlreadyOpenToolCall()
    {
        var publisher = new TestNaradEventPublisher();
        var context = new NaradEventContext("agent-1", "session-1", "user-1", "root-1", "root-1");
        var lifecycle = new AssistantMessageLifecycle("message-1");
        var tool = new GatewayChatToolCall("call-1", "invoke_agent_roller", "{\"message\":\"roll 44\"}", "agent");
        IReadOnlyList<NaradPersistedEvent> history = await new AgentExecutionEventWriter(publisher).MapAsync(tool, context, lifecycle, TestContext.Current.CancellationToken);
        var resumed = new AgentExecutionEventWriter(publisher, new WriterHistory(history));
        Assert.Empty(await resumed.MapAsync(tool, context, lifecycle, TestContext.Current.CancellationToken));
        Assert.Single(await resumed.MapAsync(tool with { ToolCallId = "call-2" }, context, lifecycle, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.MapAsync(tool with { ArgumentsJson = "{\"message\":\"roll 777\"}" }, context, lifecycle, TestContext.Current.CancellationToken).AsTask());
    }

    private sealed class WriterHistory(IReadOnlyList<NaradPersistedEvent> events) : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => events;
        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => events;
    }

    [Fact]
    public async Task WriterPreservesReasoningToolAndTerminalEventOrder()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-1");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-1", "run-1");

        await eventWriter.WriteAsync(channel.Writer, new GatewayChatTokenDelta("thinking", true), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(channel.Writer, new GatewayChatTokenDelta("answer", false), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(channel.Writer, new GatewayChatToolCall("call-1", "run_skill", "{}", "skill"), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(channel.Writer, new GatewayChatToolResult("call-1", "73"), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent", "agent-1", "session-1", "answer", false,
                new GatewayMetrics(0, 0, 0), [])),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        Assert.Equal(
        [
            NaradEventTypes.ReasoningStarted,
            NaradEventTypes.ReasoningDelta,
            NaradEventTypes.ReasoningCompleted,
            NaradEventTypes.MessageTextStarted,
            NaradEventTypes.MessageTextDelta,
            NaradEventTypes.ToolCallStarted,
            NaradEventTypes.ToolCallFinished,
            NaradEventTypes.MessageTextCompleted,
            NaradEventTypes.RunFinished,
        ],
            events.Select(static persistedEvent => persistedEvent.EventType));
        Assert.False(lifecycle.MessageOpen);
        Assert.False(lifecycle.ReasoningOpen);

        JsonElement reasoningStarted = NaradPersistedEventReader.ParseBody(events[0]);
        Assert.Equal("reasoning:message-1", reasoningStarted.GetProperty("reasoningId").GetString());
        Assert.Equal("message-1", reasoningStarted.GetProperty("messageId").GetString());
        Assert.False(reasoningStarted.TryGetProperty("role", out _));

        JsonElement reasoningDelta = NaradPersistedEventReader.ParseBody(events[1]);
        Assert.Equal("reasoning:message-1", reasoningDelta.GetProperty("reasoningId").GetString());
        Assert.Equal("message-1", reasoningDelta.GetProperty("messageId").GetString());
        Assert.Equal("thinking", reasoningDelta.GetProperty("delta").GetString());
        Assert.False(reasoningDelta.TryGetProperty("isThinking", out _));

        JsonElement reasoningCompleted = NaradPersistedEventReader.ParseBody(events[2]);
        Assert.Equal("reasoning:message-1", reasoningCompleted.GetProperty("reasoningId").GetString());
        Assert.Equal("message-1", reasoningCompleted.GetProperty("messageId").GetString());

        JsonElement textDelta = NaradPersistedEventReader.ParseBody(events[4]);
        Assert.Equal("message-1", textDelta.GetProperty("messageId").GetString());
        Assert.Equal("answer", textDelta.GetProperty("delta").GetString());
    }

    [Fact]
    public async Task WriterEmitsCanonicalReasoningIdsForUiCorrelation()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("assistant:root:run");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-1", "run-1");

        await eventWriter.WriteAsync(channel.Writer, new GatewayChatTokenDelta("step 1", true), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(channel.Writer, new GatewayChatTokenDelta("final", false), context, lifecycle, TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent", "agent-1", "session-1", "final", false,
                new GatewayMetrics(0, 0, 0))),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        NaradPersistedEvent started = Assert.Single(events, static item => item.EventType == NaradEventTypes.ReasoningStarted);
        JsonElement body = NaradPersistedEventReader.ParseBody(started);
        Assert.Equal("reasoning:assistant:root:run", body.GetProperty("reasoningId").GetString());
        Assert.Equal("assistant:root:run", body.GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task WriterPersistsNonJsonToolTextAsAStringResponse()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-1");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-1", "run-1");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatToolResult("call-photo", "[28216.jpg]"),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent));
        Assert.Equal(NaradEventTypes.ToolCallFinished, persistedEvent.EventType);
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        Assert.Equal("success", body.GetProperty("outcome").GetString());
        Assert.Equal("[28216.jpg]", body.GetProperty("response").GetString());
    }

    [Fact]
    public async Task WriterPersistsSanitizedToolFailureAsCanonicalFinished()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-1");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-1", "run-1");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatToolFailure("call-1", "run_skill_script", "tool_execution_failed", "Tool execution failed."),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent));
        Assert.Equal(NaradEventTypes.ToolCallFinished, persistedEvent.EventType);
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        Assert.Equal("error", body.GetProperty("outcome").GetString());
        Assert.Equal("tool_execution_failed", body.GetProperty("code").GetString());
        Assert.Equal("Tool execution failed.", body.GetProperty("message").GetString());
        Assert.DoesNotContain("exception", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriterPersistsDeniedToolAsCancelledFinished()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-1");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-1", "run-1");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatToolFailure(
                "call-deny",
                "invoke_agent_roller",
                "denied",
                "Tool 'invoke_agent_roller' was denied by the user."),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent));
        Assert.Equal(NaradEventTypes.ToolCallFinished, persistedEvent.EventType);
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        Assert.Equal("cancelled", body.GetProperty("outcome").GetString());
        Assert.Equal("denied", body.GetProperty("code").GetString());
        Assert.Equal("Tool 'invoke_agent_roller' was denied by the user.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WriterPersistsLiveUserStopAsRunCancelledTerminal()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-stop");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-stop", "run-stop");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                "This run was stopped by the user.",
                false,
                new GatewayMetrics(0, 0, 0),
                Run: new GatewayRunState(GatewayRunPhases.Stopped))),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var captured = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            captured.Add(persistedEvent);
        }

        NaradPersistedEvent terminal = Assert.Single(
            captured,
            static item => item.EventType == NaradEventTypes.RunCancelled);
        JsonElement body = NaradPersistedEventReader.ParseBody(terminal);
        Assert.Equal("user_stopped", body.GetProperty("code").GetString());
        Assert.DoesNotContain(
            captured,
            static item => item.EventType == NaradEventTypes.RunFinished);
    }

    [Fact]
    public async Task WriterEmitsInterruptAdjacencyForApprovalWithMatchingIdentity()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-approval");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-approval", "run-approval");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatApprovalRequest("approval-1", "call-1", "run_skill", "{}", RunId: "run-approval"),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        Assert.Equal(
            [NaradEventTypes.InterruptRequested, NaradEventTypes.RunPaused],
            events.Select(static item => item.EventType));

        JsonElement interruptBody = NaradPersistedEventReader.ParseBody(events[0]);
        JsonElement pauseBody = NaradPersistedEventReader.ParseBody(events[1]);
        Assert.Equal("approval-1", interruptBody.GetProperty("interruptId").GetString());
        Assert.Equal("wait:approval-1", interruptBody.GetProperty("waitId").GetString());
        Assert.Equal("call-1", interruptBody.GetProperty("toolCallId").GetString());
        Assert.Equal("wait:approval-1", pauseBody.GetProperty("waitId").GetString());
        Assert.Equal("approval-1", pauseBody.GetProperty("interruptId").GetString());
    }

    [Fact]
    public async Task PackedApprovalEmitsOneInterruptThenPause()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-packed-approval");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-approval", "run-approval");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatApprovalRequest(
                "ficc_1",
                "call-1",
                "invoke_agent_roller",
                "{}",
                RunId: "run-approval",
                ToolType: "agent",
                PauseRun: true),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        Assert.Equal(
            [NaradEventTypes.InterruptRequested, NaradEventTypes.RunPaused],
            events.Select(static item => item.EventType));
        JsonElement interruptBody = NaradPersistedEventReader.ParseBody(events[0]);
        JsonElement pauseBody = NaradPersistedEventReader.ParseBody(events[1]);
        Assert.Equal("ficc_1", interruptBody.GetProperty("interruptId").GetString());
        Assert.Equal("wait:ficc_1", interruptBody.GetProperty("waitId").GetString());
        Assert.Equal("call-1", interruptBody.GetProperty("input").GetProperty("toolCallId").GetString());
        Assert.False(interruptBody.GetProperty("input").TryGetProperty("packedApprovals", out _));
        Assert.Equal("wait:ficc_1", pauseBody.GetProperty("waitId").GetString());
        Assert.Equal("ficc_1", pauseBody.GetProperty("interruptId").GetString());
    }

    [Fact]
    public async Task WaitingOnChildBoundaryUsesSuspendNotStreamDone()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-aaat-wait")
        {
            MessageOpen = true,
        };
        var context = new NaradEventContext(
            "agent-1", "session-parent", "user-1", "run-parent", "run-parent");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatToolCall("invoke-1", "invoke_agent_roller", """{"message":"d44"}""", "agent"),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatProduceBoundarySuspended(RunWaitReasons.Child),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        Assert.Equal(
            [NaradEventTypes.ToolCallStarted, NaradEventTypes.MessageTextCompleted],
            events.Select(static item => item.EventType));
        Assert.DoesNotContain(events, static item => item.EventType == NaradEventTypes.RunFinished);
        Assert.DoesNotContain(events, static item => item.EventType == NaradEventTypes.RunPaused);
        Assert.DoesNotContain(events, static item => item.EventType == NaradEventTypes.RunResumed);
        Assert.Throws<InvalidOperationException>(() => AgentExecutionTerminalEvent.From(
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-parent",
                string.Empty,
                false,
                new GatewayMetrics(0, 0, 0),
                Run: new GatewayRunState(GatewayRunPhases.Executing, WaitStateReason: RunWaitReasons.Child)))));
        Assert.Throws<InvalidOperationException>(() => AgentExecutionTerminalEvent.From(
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                string.Empty,
                false,
                new GatewayMetrics(0, 0, 0),
                Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval)))));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await eventWriter.WriteAsync(
                channel.Writer,
                new GatewayChatStreamDone(new GatewayChatResult(
                    "agent",
                    "agent-1",
                    "session-parent",
                    string.Empty,
                    false,
                    new GatewayMetrics(0, 0, 0),
                    Run: new GatewayRunState(GatewayRunPhases.Executing, WaitStateReason: RunWaitReasons.Child))),
                context,
                lifecycle,
                TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public void TerminalFinishedPayloadUsesNaradUsageEnvelope()
    {
        AgentExecutionTerminalEvent terminal = AgentExecutionTerminalEvent.From(
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                "ok",
                false,
                new GatewayMetrics(120, 45, 1500),
                Run: new GatewayRunState(GatewayRunPhases.Completed))));

        Assert.Equal(NaradEventTypes.RunFinished, terminal.EventType);
        using JsonDocument document = JsonDocument.Parse(terminal.Payload.GetRawText());
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("result", out _), "run.finished must not dump GatewayChatResult.");
        Assert.Equal("ok", root.GetProperty("response").GetString());
        Assert.Equal(120, root.GetProperty("usage").GetProperty("promptTokens").GetInt64());
        Assert.Equal(45, root.GetProperty("usage").GetProperty("completionTokens").GetInt64());
        Assert.Equal(165, root.GetProperty("usage").GetProperty("totalTokens").GetInt64());
        Assert.Equal(1500, root.GetProperty("usage").GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public void TerminalErrorAndCancelCarryOptionalUsage()
    {
        AgentExecutionTerminalEvent failed = AgentExecutionTerminalEvent.From(
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                string.Empty,
                false,
                new GatewayMetrics(10, 0, 100),
                Run: new GatewayRunState(GatewayRunPhases.Failed, ErrorCode: "provider", ErrorMessage: "boom"))));
        Assert.Equal(NaradEventTypes.RunError, failed.EventType);
        using (JsonDocument document = JsonDocument.Parse(failed.Payload.GetRawText()))
        {
            Assert.Equal("provider", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(10, document.RootElement.GetProperty("usage").GetProperty("promptTokens").GetInt64());
        }

        AgentExecutionTerminalEvent stopped = AgentExecutionTerminalEvent.From(
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                string.Empty,
                false,
                new GatewayMetrics(0, 0, 50),
                Run: new GatewayRunState(GatewayRunPhases.Stopped))));
        Assert.Equal(NaradEventTypes.RunCancelled, stopped.EventType);
        using JsonDocument cancelled = JsonDocument.Parse(stopped.Payload.GetRawText());
        Assert.Equal("user_stopped", cancelled.RootElement.GetProperty("code").GetString());
        Assert.Equal(50, cancelled.RootElement.GetProperty("usage").GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public async Task ApprovalThenPendingStreamDoneEmitsExactlyOnePausePair()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-approval-stream-done");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-approval", "run-approval");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatApprovalRequest("approval-1", "call-1", "run_skill", "{}", RunId: "run-approval"),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);
        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                "agent-1",
                "session-1",
                string.Empty,
                false,
                new GatewayMetrics(0, 0, 0),
                Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval))),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        var events = new List<NaradPersistedEvent>();
        while (channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent))
        {
            events.Add(persistedEvent);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(1, events.Count(static item => item.EventType == NaradEventTypes.RunPaused));
        Assert.Equal(
            [NaradEventTypes.InterruptRequested, NaradEventTypes.RunPaused],
            events.Select(static item => item.EventType));
    }

    [Fact]
    public async Task WriterMapsWorkflowEventToRuntimeProgressCategory()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-workflow");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-workflow", "run-workflow");

        await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatWorkflowEvent("executor.ready", "executor-1"),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent));
        Assert.Equal(NaradEventTypes.ProgressRecorded, persistedEvent.EventType);
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        Assert.Equal("runtime", body.GetProperty("category").GetString());
        Assert.Equal("executor.ready", body.GetProperty("label").GetString());
    }

    [Fact]
    public async Task WriterEmitsNativeNaradTypesWithoutTranslationLayer()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-native");
        var context = new NaradEventContext("agent-1", "session-1", "user-1", "run-native", "run-native");

        await eventWriter.WriteRuntimeAsync(
            channel.Writer,
            NaradEventTypes.ToolCallStarted,
            JsonSerializer.SerializeToElement(new JsonObject { ["toolCallId"] = "call-native", ["toolName"] = "probe" }),
            context,
            TestContext.Current.CancellationToken);

        Assert.True(channel.Reader.TryRead(out NaradPersistedEvent? persistedEvent));
        Assert.Equal(NaradEventTypes.ToolCallStarted, persistedEvent.EventType);
        Assert.DoesNotContain("NaradLegacyEventTypeMapping", typeof(AgentExecutionEventWriter).Assembly.GetTypes().Select(static type => type.Name));
    }

    [Fact]
    public async Task WriterDoesNotPublishCheckpointTraceAuthority()
    {
        var publisher = new TestNaradEventPublisher();
        var eventWriter = new AgentExecutionEventWriter(publisher);
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var lifecycle = new AssistantMessageLifecycle("message-checkpoint");
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-checkpoint", "run-checkpoint");

        NaradPersistedEvent? result = await eventWriter.WriteAsync(
            channel.Writer,
            new GatewayChatContinuationCheckpoint(
                "checkpoint-1",
                "run-checkpoint",
                1,
                "approval",
                "resume-token-1",
                []),
            context,
            lifecycle,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(channel.Reader.TryRead(out _));
    }
}

internal sealed class TestNaradEventPublisher : IRuntimeEventPublisher
{
    private readonly ConcurrentDictionary<string, long> sequences = new(StringComparer.Ordinal);

    public ValueTask<NaradPersistedEvent> PublishAsync(
        NaradAuthorityEventDraft draft,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long sessionSequence = sequences.AddOrUpdate(context.SessionId, 1, static (_, value) => value + 1);
        long rootSequence = sequences.AddOrUpdate(context.RootRunId, 1, static (_, value) => value + 1);
        NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
            draft.EventId,
            draft.EventType,
            context.SessionId,
            context.RunId,
            context.RootRunId,
            draft.Body,
            sessionSequence,
            rootSequence);
        return ValueTask.FromResult(persistedEvent);
    }

    public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
        string eventId,
        string eventType,
        JsonElement body,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
        => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
}
