using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChatGatewayServiceUsageTests
{
    [Fact]
    public async Task StreamChatAsyncPersistsPausedAssistantMessageWhenApprovalPausesAfterToolCall()
    {
        var usageQueue = new CapturingUsageQueue();
        var sessionStore = new FakeSessionStore();
        var service = CreateService(
            usageQueue,
            sessionStore,
            new FakeSessionLifecycleService(),
            new FakeAgentChatService(ApprovalPausedEvents()));

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "session_id": "session-1",
              "message": "roll a d90",
              "source": "chat"
            }
            """);

        await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Empty(sessionStore.UserMessages);

        (GatewayMessage userMessage, GatewayMessage assistantMessage) = Assert.Single(sessionStore.UserAssistantPairs);
        Assert.Equal("user", userMessage.Role);
        Assert.Equal("roll a d90", userMessage.Content);
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal("invoke_agent_roller", Assert.Single(assistantMessage.ToolCalls!).ToolName);
        Assert.Equal("calling", Assert.Single(assistantMessage.ToolCalls!).Status);
        Assert.Empty(sessionStore.AssistantMessages);

        GatewayRun run = Assert.Single(sessionStore.Runs);
        Assert.Equal("roll a d90", Assert.IsType<string>(run.Input["message"]));
        Assert.Equal(string.Empty, Assert.IsType<string>(run.Output["response"]));

        GatewayUsageFact fact = Assert.Single(usageQueue.Facts);
        Assert.Equal("chat", fact.SourceChannel);
    }

    [Fact]
    public async Task StreamChatAsyncRejectsAnAgentOwnedBySomeoneElse()
    {
        var usageQueue = new CapturingUsageQueue();
        var chat = new FakeAgentChatService(AsyncEnumerable.Empty<GatewayChatStreamEvent>());
        var service = CreateService(
            usageQueue,
            agentChatService: chat,
            catalog: new EmptyCatalogStore(new GatewayAgent(
                "agent-a",
                "Private",
                "model-a",
                "secret prompt",
                "disabled",
                new JsonObject(),
                new JsonObject(),
                new JsonObject(),
                [],
                "user-b",
                "2026-10-11T00:00:00Z")));

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "message": "read the workspace",
              "user_id": "user-a",
              "source": "workflow"
            }
            """);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal("Agent is not available to this caller.", exception.Message);
        Assert.Empty(chat.Invocations);
    }

    [Fact]
    public async Task StreamChatAsyncPersistsPausedAssistantMessageBeforeCallerStopsOnApproval()
    {
        var usageQueue = new CapturingUsageQueue();
        var sessionStore = new FakeSessionStore();
        var lifecycleService = new FakeSessionLifecycleService();
        var service = CreateService(
            usageQueue,
            sessionStore,
            lifecycleService,
            new FakeAgentChatService(ApprovalPausedEvents()));

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "session_id": "session-1",
              "message": "roll a d100",
              "source": "autopilot"
            }
            """);

        var observed = new List<NaradPersistedEvent>();
        await foreach (NaradPersistedEvent evt in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
        {
            observed.Add(evt);
            if (string.Equals(evt.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Equal(
            [NaradEventTypes.ToolCallStarted, NaradEventTypes.InterruptRequested],
            observed.Select(static runtimeEvent => runtimeEvent.EventType));
        Assert.Empty(sessionStore.UserAssistantPairs);
        Assert.Empty(sessionStore.Runs);
        Assert.Equal("autopilot", lifecycleService.LastEnsureOriginator);
    }

    [Fact]
    public async Task StreamChatAsyncDoesNotPersistSyntheticToolCallWhenApprovalPausesBeforeToolStart()
    {
        var usageQueue = new CapturingUsageQueue();
        var sessionStore = new FakeSessionStore();
        var service = CreateService(
            usageQueue,
            sessionStore,
            new FakeSessionLifecycleService(),
            new FakeAgentChatService(GateBeforeStartApprovalPausedEvents()));

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "session_id": "session-1",
              "message": "roll a d100",
              "source": "chat"
            }
            """);

        await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        (GatewayMessage userMessage, GatewayMessage assistantMessage) = Assert.Single(sessionStore.UserAssistantPairs);
        Assert.Equal("roll a d100", userMessage.Content);
        Assert.Null(assistantMessage.ToolCalls);
        Assert.Empty(sessionStore.AssistantMessages);

        GatewayRun run = Assert.Single(sessionStore.Runs);
        Assert.Equal("roll a d100", Assert.IsType<string>(run.Input["message"]));
    }

    [Fact]
    public async Task StreamChatAsyncResolvesWorkspaceFilesBeforeAgentExecution()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"kautuka-chat-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "attachments"));
        string csvPath = Path.Combine(workspaceRoot, "attachments", "sample-data.csv");
        await File.WriteAllTextAsync(csvPath, "name,value\nalpha,1\nbeta,2\n", TestContext.Current.CancellationToken);

        try
        {
            var usageQueue = new CapturingUsageQueue();
            var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
            var service = CreateService(
                usageQueue,
                agentChatService: agentChatService,
                userStore: new FakeUserStore(new GatewayUser("user-1", "autopilot-user", "pw", "user", true, null, null)),
                agentFilePathAuthorizationService: new StubAgentFilePathAuthorizationService(workspaceRoot));

            JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "target_type": "agent",
                  "target_id": "agent-a",
                  "session_id": "session-1",
                                    "message": "summarize #attachments/sample-data.csv",
                                    "workspaceFiles": ["attachments/sample-data.csv"],
                  "user_id": "user-1",
                  "source": "autopilot"
                }
                """);

            await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            AgentWorkspaceFileReference workspaceFile = Assert.Single(agentChatService.LastWorkspaceFiles);
            Assert.Equal("attachments/sample-data.csv", workspaceFile.RelativePath);
            Assert.Equal(csvPath, workspaceFile.ResolvedPath);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StreamChatAsyncAutoCompactsWhenThresholdPercentIsUnset()
    {
        var usageQueue = new CapturingUsageQueue();
        var lifecycleService = new FixedSessionLifecycleService(CreateSession("session-auto-compact", estimatedContextTokens: 26_000));
        var compactionService = new RecordingSessionCompactionService();

        var service = CreateService(
            usageQueue,
            sessionLifecycleService: lifecycleService,
            agentChatService: new FakeAgentChatService(AsyncEnumerable.Empty<GatewayChatStreamEvent>()),
            configurationProvider: new StaticPlatformConfigurationProvider(CreateAutoCompactConfiguration()),
            sessionCompactionService: compactionService);

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "session_id": "session-auto-compact",
              "message": "please continue",
              "source": "chat"
            }
            """);

        await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(1, compactionService.CallCount);
        Assert.Equal("auto-threshold", compactionService.LastReason);
        Assert.Equal("session-auto-compact", compactionService.LastSessionId);
    }

    [Fact]
    public async Task AutopilotAndChatWithSameWorkspaceFileProduceEquivalentRuntimeInputsAndOutput()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"kautuka-chat-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "attachments"));
        string csvPath = Path.Combine(workspaceRoot, "attachments", "synthetic-report.csv");
        await File.WriteAllTextAsync(csvPath, "item,count\nwidgets,7\ngadgets,3\n", TestContext.Current.CancellationToken);

        try
        {
            var usageQueue = new CapturingUsageQueue();
            var sessionStore = new FakeSessionStore();
            var agentChatService = new FakeAgentChatService();
            var chatService = CreateService(
                usageQueue,
                sessionStore,
                new FakeSessionLifecycleService(),
                agentChatService,
                userStore: new FakeUserStore(new GatewayUser("user-1", "autopilot-user", "pw", "user", true, null, null)),
                agentFilePathAuthorizationService: new StubAgentFilePathAuthorizationService(workspaceRoot));

            JsonElement chatBody = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "target_type": "agent",
                  "target_id": "agent-a",
                  "session_id": "session-chat",
                                    "message": "summarize #attachments/synthetic-report.csv",
                                    "workspaceFiles": ["attachments/synthetic-report.csv"],
                  "user_id": "user-1",
                  "source": "chat"
                }
                """);

            await foreach (NaradPersistedEvent _ in chatService.StreamChatAsync(chatBody, cancellationToken: TestContext.Current.CancellationToken))
            {
            }

            AgentInvocation chatInvocation = Assert.Single(agentChatService.Invocations);
            (GatewayMessage chatUserMessage, GatewayMessage chatAssistantMessage) = Assert.Single(sessionStore.UserAssistantPairs);
            Assert.Equal("analysis: summarize #attachments/synthetic-report.csv | files=attachments/synthetic-report.csv", chatAssistantMessage.Content);

            var autopilotStore = new RecordingAutopilotStore();
            var dispatcher = new RecordingNotificationDispatcher();
            var autopilotService = new AutopilotRunExecutionService(
                autopilotStore,
                chatService,
                null!,
                null!,
                dispatcher,
                NullLogger<AutopilotRunExecutionService>.Instance);

            GatewayAutopilotSchedule schedule = new(
                Id: "schedule-1",
                OwnerUserId: "user-1",
                TargetType: "agent",
                TargetId: "agent-a",
                CronExpression: "*/5 * * * *",
                TimeZoneId: "UTC",
                Mode: AutopilotScheduleModes.On,
                RequiresPreRunApproval: false,
                SchedulePromptContext: "summarize #attachments/synthetic-report.csv",
                WorkspaceFilePaths: ["attachments/synthetic-report.csv"],
                NotifyUiInbox: true,
                NotifyChannels: false,
                PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.NextOccurrence,
                PreRunApprovalTimeoutSeconds: null,
                OverlapPolicy: AutopilotOverlapPolicies.SkipAndNotify,
                IsPaused: false,
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                NextDueAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
                LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
                LeaseOwner: null,
                LeaseExpiresAt: null);

            GatewayScheduledRun run = new(
                Id: "run-1",
                ScheduleId: schedule.Id,
                OwnerUserId: schedule.OwnerUserId,
                TargetType: schedule.TargetType,
                TargetId: schedule.TargetId,
                Status: AutopilotRunStatuses.Scheduled,
                TriggerType: "cron",
                ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
                SessionId: null,
                RuntimeRunId: null,
                PreRunApprovalRequestId: null,
                InputJson: null,
                OutputJson: null,
                Error: null,
                StateCategory: AutopilotRunStateCategories.Executing,
                SkipReason: null,
                BlockingRunId: null,
                BlockingRunStatusSnapshot: null,
                BlockingRunStateCategory: null,
                BlockedAt: null,
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                StartedAt: null,
                EndedAt: null);

            await autopilotService.StartAndExecuteAsync(schedule, run, TestContext.Current.CancellationToken);

            Assert.Equal(2, agentChatService.Invocations.Count);
            AgentInvocation autopilotInvocation = agentChatService.Invocations[1];
            Assert.Equal(chatInvocation.AgentId, autopilotInvocation.AgentId);
            Assert.Equal(chatInvocation.Message, autopilotInvocation.Message);
            Assert.Equal(chatInvocation.WorkspaceFiles.Select(static file => file.RelativePath), autopilotInvocation.WorkspaceFiles.Select(static file => file.RelativePath));
            Assert.Equal(chatInvocation.WorkspaceFiles.Select(static file => file.ResolvedPath), autopilotInvocation.WorkspaceFiles.Select(static file => file.ResolvedPath));

            RunTransition completed = Assert.Single(autopilotStore.StateTransitions, static transition => transition.NextStatus == AutopilotRunStatuses.Completed);
            Assert.Equal(chatAssistantMessage.Content, completed.OutputJson);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void PersistRunDerivesSingleModelTurnWhenProviderTelemetryIsUnavailable()
    {
        var usageQueue = new CapturingUsageQueue();
        var service = CreateService(usageQueue);
        var result = new GatewayChatResult(
            "agent",
            "agent-a",
            "session-1",
            "Hello back",
            false,
            new GatewayMetrics(120, 45, 900),
            null,
            null,
            [],
            Usage: new GatewayCapturedUsageMetrics(null, null));

        service.PersistRun("session-1", "Hello", result, userId: null, sourceChannel: "chat");

        GatewayUsageFact fact = Assert.Single(usageQueue.Facts);
        Assert.Null(fact.ProviderRequestCount);
        Assert.Equal(1, fact.ModelTurnCount);
        Assert.Equal(1, fact.AssistantTurnCount);
        Assert.Equal("chat", fact.SourceChannel);
    }

    [Fact]
    public void PersistRunPreservesObservedProviderRequestCountAsModelTurnCount()
    {
        var usageQueue = new CapturingUsageQueue();
        var service = CreateService(usageQueue);
        var result = new GatewayChatResult(
            "agent",
            "agent-a",
            "session-1",
            "Handled",
            false,
            new GatewayMetrics(10, 20, 250),
            null,
            null,
            [],
            Usage: new GatewayCapturedUsageMetrics(3, DateTimeOffset.UtcNow.ToString("O")));

        service.PersistRun("session-1", "Ping", result, userId: null, sourceChannel: "autopilot");

        GatewayUsageFact fact = Assert.Single(usageQueue.Facts);
        Assert.Equal(3, fact.ProviderRequestCount);
        Assert.Equal(3, fact.ModelTurnCount);
        Assert.Equal("autopilot", fact.SourceChannel);
    }

    [Fact]
    public void PersistRunWritesOneFactPerUsageTurnWithUniqueRunIds()
    {
        var usageQueue = new CapturingUsageQueue();
        var service = CreateService(usageQueue);
        var result = new GatewayChatResult(
            "agent",
            "agent-a",
            "session-1",
            "done",
            false,
            new GatewayMetrics(2616, 132, 400),
            UsageTurns:
            [
                new GatewayUsageTurn(1230, 70),
                new GatewayUsageTurn(1386, 62),
            ]);

        service.PersistRun("session-1", "Hello", result, userId: null, sourceChannel: "chat");

        Assert.Equal(2, usageQueue.Facts.Count);
        Assert.Equal(1230, usageQueue.Facts[0].TokenIn);
        Assert.Equal(70, usageQueue.Facts[0].TokenOut);
        Assert.Equal(1, usageQueue.Facts[0].ModelTurnCount);
        Assert.Equal(1386, usageQueue.Facts[1].TokenIn);
        Assert.Equal(62, usageQueue.Facts[1].TokenOut);
        Assert.Equal(1, usageQueue.Facts[1].ModelTurnCount);
        Assert.NotEqual(usageQueue.Facts[0].RunId, usageQueue.Facts[1].RunId);
        Assert.All(usageQueue.Facts, static fact => Assert.Equal("session-1", fact.SessionId));
        Assert.Equal(2616, usageQueue.Facts.Sum(static fact => fact.TokenIn ?? 0));
        Assert.Equal(132, usageQueue.Facts.Sum(static fact => fact.TokenOut ?? 0));
    }

    [Fact]
    public async Task StreamChatAsyncPersistsTurnFactsFromAgentTurnProgressAndSkipsTerminalAggregate()
    {
        var usageQueue = new CapturingUsageQueue();
        var sessionStore = new FakeSessionStore();
        var service = CreateService(
            usageQueue,
            sessionStore,
            new FakeSessionLifecycleService(),
            new ScriptedNaradAgentChatService(TurnProgressThenFinishedEvents()));

        JsonElement body = JsonSerializer.Deserialize<JsonElement>("""
            {
              "target_type": "agent",
              "target_id": "agent-a",
              "session_id": "session-1",
              "message": "hello",
              "source": "chat"
            }
            """);

        await foreach (NaradPersistedEvent _ in service.StreamChatAsync(body, cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(2, usageQueue.Facts.Count);
        Assert.Equal([1230, 1386], usageQueue.Facts.Select(static fact => fact.TokenIn));
        Assert.Equal([70, 62], usageQueue.Facts.Select(static fact => fact.TokenOut));
        Assert.All(usageQueue.Facts, static fact => Assert.Equal(1, fact.ModelTurnCount));
        Assert.All(usageQueue.Facts, static fact => Assert.Equal("chat", fact.SourceChannel));
        Assert.Equal(2616, usageQueue.Facts.Sum(static fact => fact.TokenIn ?? 0));
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncPassesOriginalUserRequestToApprovalResume()
    {
        const string originalRequest = "roll a d100 then set patio lights to the result";
        var usageQueue = new CapturingUsageQueue();
        var sessionStore = new FakeSessionStore();
        sessionStore.StoredSession = new GatewaySession(
            Id: "session-approval",
            TargetType: "agent",
            TargetId: "agent-a",
            OwnerUserId: "user-1",
            Title: "Session",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            Messages:
            [
                new GatewayMessage(
                    "msg-user",
                    "user",
                    originalRequest,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    null,
                    null),
                new GatewayMessage(
                    "msg-assistant-paused",
                    "assistant",
                    string.Empty,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    null,
                    null,
                    ToolCalls: [new GatewayToolCall("tool-call-1", "run_skill_script", "agent-a", "{}", null, "calling", "skill")]),
            ],
            Compacted: false,
            CompactSummary: null);
        // A later channel turn must not replace the original approval's prompt.
        sessionStore.StoredSession = sessionStore.StoredSession with
        {
            Messages = [.. sessionStore.StoredSession.Messages,
                new GatewayMessage("later-user-777", "user", "roll d777", DateTimeOffset.UtcNow.ToString("O"), false, null, null)],
        };
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var service = CreateService(
            usageQueue,
            sessionStore,
            new FakeSessionLifecycleService(),
            agentChatService);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "run_skill_script",
            "{}",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(originalRequest, agentChatService.LastMessage);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncResolvesWaitStateFromRequestIdWhenContinuationRunIdMissing()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? pauseActivity = source.StartActivity("agent (agent-a) run [chat]", ActivityKind.Internal);
        Assert.NotNull(pauseActivity);

        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: pauseActivity.Id!,
            traceState: pauseActivity.TraceStateString));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "invoke_agent_roller",
            "{}",
            Approved: true,
            Reason: "approval-api",
            RunId: "run-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal("continuation-approval-1", agentChatService.LastContinuationRequest?.ContinuationRunId);
        Assert.Equal("approval", agentChatService.LastContinuationRequest?.BoundaryType);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncRestoresActiveElapsedFromWaitStateClockCheckpoint()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        TimeSpan parkedElapsed = TimeSpan.FromSeconds(37);
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-clock",
            traceParent: null,
            traceState: null,
            behaviorJson: RunClockEnvelope.Wrap("""{"providerId":"ollama"}""", parkedElapsed)));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "invoke_agent_roller",
            "{}",
            Approved: true,
            Reason: "approval-api",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-clock");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.Equal(parkedElapsed, agentChatService.LastContinuationRequest?.ActiveElapsed);
        Assert.Contains("ollama", agentChatService.LastContinuationRequest?.EffectiveBehaviorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("kautukaActiveElapsedMs", agentChatService.LastContinuationRequest?.EffectiveBehaviorJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncPassesWaitStateTraceContextToApprovalResume()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? pauseActivity = source.StartActivity("agent (agent-a) run [chat]", ActivityKind.Internal);
        Assert.NotNull(pauseActivity);

        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: pauseActivity.Id!,
            traceState: pauseActivity.TraceStateString));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "run_skill_script",
            "{}",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        AgentRequestTraceContext traceContext = Assert.IsType<AgentRequestTraceContext>(agentChatService.LastRequestContext?.Trace);
        Assert.Equal(pauseActivity.Id, traceContext.TraceParent);
        Assert.Equal(pauseActivity.TraceId.ToString(), traceContext.TraceId);
        Assert.Equal(pauseActivity.SpanId.ToString(), traceContext.ParentSpanId);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncRecordsDurableHandoffBeforeClosingWait()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: null,
            traceState: null));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);
        bool handoffRecorded = false;

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            new ToolApprovalResponseInfo(
                "approval-1",
                "tool-call-1",
                "run_skill_script",
                "{}",
                Approved: true,
                Reason: "chat-ui",
                RunId: "run-1",
                ContinuationRunId: "continuation-approval-1"),
            "user-1",
            (terminalEvent, _) =>
            {
                Assert.Equal(NaradEventTypes.RunFinished, terminalEvent.EventType);
                Assert.Null(runWaitStateStore.ResumedWaitId);
                handoffRecorded = true;
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken))
        {
        }

        Assert.True(handoffRecorded);
        Assert.Equal("wait-approval-1", runWaitStateStore.ResumedWaitId);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncIgnoresEmptyWaitSnapshotForApprovalResume()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: null,
            traceState: null,
            sessionStateJson: emptySessionStateJson));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "run_skill_script",
            "{}",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(agentChatService.LastContinuationRequest);
        Assert.Null(agentChatService.LastContinuationRequest.SessionStateJson);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncLoadsLiveSessionForInvokeAgentGateBeforeStartResume()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: null,
            traceState: null,
            sessionStateJson: nonEmptySessionStateJson,
            toolName: "invoke_agent_roller"));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "invoke_agent_roller",
            "{}",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(agentChatService.LastContinuationRequest);
        // Same as ordinary tools: null override loads the live MAF session.
        Assert.Null(agentChatService.LastContinuationRequest.SessionStateJson);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncReopensEmptyGateBeforeStartInvokeAgentWithoutHitlReentry()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(GateBeforeStartApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: null,
            traceState: null,
            sessionStateJson: emptySessionStateJson,
            toolName: "invoke_agent_roller"));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "invoke_agent_roller",
            """{"message":"roll a d100"}""",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-1");

        var observedEvents = new List<NaradPersistedEvent>();
        await foreach (NaradPersistedEvent runtimeEvent in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
            observedEvents.Add(runtimeEvent);
        }

        Assert.NotNull(agentChatService.LastContinuationRequest);
        Assert.Null(agentChatService.LastContinuationRequest.SessionStateJson);
        Assert.DoesNotContain(
            observedEvents,
            runtimeEvent => string.Equals(runtimeEvent.EventType, NaradEventTypes.InterruptRequested, StringComparison.Ordinal));

        // Golden path: parent journal must carry tool.call.started for the approved toolCallId
        // after gate-before-start resume (browser must never invent tool rows).
        NaradPersistedEvent? toolStarted = observedEvents.FirstOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.ToolCallStarted, StringComparison.Ordinal)
            && runtimeEvent.EventJson.Contains("tool-call-1", StringComparison.Ordinal));
        Assert.NotNull(toolStarted);
        int toolStartIndex = observedEvents.IndexOf(toolStarted);
        int runFinishedIndex = observedEvents.FindIndex(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.RunFinished, StringComparison.Ordinal));
        if (runFinishedIndex >= 0)
        {
            Assert.True(toolStartIndex < runFinishedIndex, "tool.call.started must precede run.finished on gate resume.");
        }
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncIgnoresDirectToolWaitSnapshotForApprovalResume()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(CreateWaitState(
            continuationRunId: "continuation-approval-1",
            traceParent: null,
            traceState: null,
            sessionStateJson: nonEmptySessionStateJson,
            toolName: "run_skill_script"));
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "run_skill_script",
            "{}",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ContinuationRunId: "continuation-approval-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(agentChatService.LastContinuationRequest);
        Assert.Null(agentChatService.LastContinuationRequest.SessionStateJson);
    }

    [Fact]
    public async Task StreamChatWithApprovalAsyncLoadsLiveSessionForAgentToolChildResultResume()
    {
        var usageQueue = new CapturingUsageQueue();
        var agentChatService = new FakeAgentChatService(ApprovalCompletedEvents());
        var runWaitStateStore = new FakeRunWaitStateStore(
            CreateWaitState(
                continuationRunId: "continuation-child-1",
                traceParent: null,
                traceState: null,
                sessionStateJson: nonEmptySessionStateJson,
                toolName: "invoke_agent_roller") with
            {
                WaitReason = RunWaitReasons.Child,
            });
        var service = CreateService(
            usageQueue,
            new FakeSessionStore(),
            new FakeSessionLifecycleService(),
            agentChatService,
            runWaitStateStore);

        var approvalResponse = new ToolApprovalResponseInfo(
            "approval-child-1",
            "tool-call-child-1",
            "invoke_agent_roller",
            """{"message":"roll a d100"}""",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-child-1",
            ResultJson: """{"result":"78"}""",
            ContinuationRunId: "continuation-child-1");

        await foreach (NaradPersistedEvent _ in service.StreamChatWithApprovalAsync(
            "agent",
            "agent-a",
            "session-approval",
            approvalResponse,
            "user-1",
            cancellationToken: TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(agentChatService.LastContinuationRequest);
        Assert.Null(agentChatService.LastContinuationRequest.SessionStateJson);
    }

    private static ChatGatewayService CreateService(
        CapturingUsageQueue usageQueue,
        FakeSessionStore? sessionStore = null,
        ISessionLifecycleService? sessionLifecycleService = null,
        IAgentChatService? agentChatService = null,
        IRunWaitStateStore? runWaitStateStore = null,
        IGatewayUserStore? userStore = null,
        IAgentFilePathAuthorizationService? agentFilePathAuthorizationService = null,
        IPlatformConfigurationProvider? configurationProvider = null,
        ISessionCompactionService? sessionCompactionService = null,
        IGatewayCatalogStore? catalog = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.Combine(Path.GetTempPath(), $"kautuka-chat-usage-{Guid.NewGuid():N}"),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        var agentStoreDatabase = new SqliteAgentStoreDatabase(configuration);
        GatewayState state = GatewayState.Create(
            database,
            NullLogger<GatewayState>.Instance,
            new SqliteAgentDefinitionStore(configuration),
            new SqliteAgentRuntimeSessionRepository(agentStoreDatabase),
            new SqliteAgentRuntimeRunRepository(agentStoreDatabase),
            new SqliteAgentMemoryFactRepository(agentStoreDatabase),
            new GatewayMemoryStore());

        return new ChatGatewayService(
            sessionStore ?? new FakeSessionStore(),
            sessionLifecycleService ?? new FakeSessionLifecycleService(),
            agentChatService ?? new FakeAgentChatService(AsyncEnumerable.Empty<GatewayChatStreamEvent>()),
            NullLogger<ChatGatewayService>.Instance,
            new NoOpFactExtractor(),
            new NoOpChatMemoryAssembler(),
            new NoOpAgentMemoryEmbeddingService(),
            new PassthroughMemoryScopePolicy(),
            new KautukaAiClientFactory([], null!),
            state,
            configurationProvider ?? new StaticPlatformConfigurationProvider(),
            new NoOpConversationAssetStore(),
            catalog ?? new EmptyCatalogStore(),
            userStore ?? new EmptyUserStore(),
            agentFilePathAuthorizationService ?? new NoOpAgentFilePathAuthorizationService(),
            usageQueue,
            runWaitStateStore ?? new NoOpRunWaitStateStore(),
            sessionCompactionService ?? new NoOpSessionCompactionService(),
            new TestRuntimeEventPublisher(),
            new NoOpApprovalStore());
    }

    private static PlatformConfiguration CreateAutoCompactConfiguration()
        => new()
        {
            Providers =
            [
                new ProviderDefinition
                {
                    Id = "provider-test",
                    Type = "test",
                },
            ],
            Models =
            [
                new ModelDefinition
                {
                    Id = "model-auto-compact",
                    ProviderId = "provider-test",
                    ModelName = "test-model",
                },
            ],
            Agents =
            [
                new AgentDefinition
                {
                    Id = "agent-a",
                    ModelId = "model-auto-compact",
                    Name = "Agent A",
                },
            ],
        };

    private static GatewaySession CreateSession(string sessionId, int estimatedContextTokens)
        => new(
            sessionId,
            "agent",
            "agent-a",
            "user-1",
            "Session",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null,
            EstimatedContextTokens: estimatedContextTokens,
            LastEstimatedAt: DateTimeOffset.UtcNow.ToString("O"));

    private static ActivityListener CreateGatewayAgentRunListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, KautukaActivitySources.GatewayAgentRunSource, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private const string emptySessionStateJson = """
        {"stateBag":{"InMemoryChatHistoryProvider":{"messages":[]}}}
        """;

    private const string nonEmptySessionStateJson = """
        {"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[{"$type":"text","text":"original instruction"}]}]}}}
        """;

    private static RunWaitState CreateWaitState(
        string continuationRunId,
        string? traceParent,
        string? traceState,
        string sessionStateJson = "{}",
        string toolName = "run_skill_script",
        string behaviorJson = "{}")
        => new(
            "wait-approval-1",
            continuationRunId,
            "run-1",
            "agent",
            "agent-a",
            "session-approval",
            "agent-a",
            "user-1",
            RunWaitReasons.Approval,
            "approval-1",
            sessionStateJson,
            behaviorJson,
            "[]",
            "tool-call-1",
            toolName,
            "approval-1",
            null,
            null,
            null,
            traceParent,
            traceState,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-approval-1");

    private static async IAsyncEnumerable<GatewayChatStreamEvent> ApprovalPausedEvents()
    {
        yield return new GatewayChatToolCall("tool-call-1", "invoke_agent_roller", "{}", "agent");
        yield return new GatewayChatApprovalRequest("req-1", "tool-call-1", "roll_die", "{}", RunId: "run-1", CheckpointId: null);
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            "agent-a",
            "session-1",
            string.Empty,
            false,
            new GatewayMetrics(10, 0, 150),
            ToolCalls: [new GatewayToolCall("tool-call-1", "invoke_agent_roller", "agent-a", "{}", null, "calling", "agent")],
            Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval)));

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> GateBeforeStartApprovalPausedEvents()
    {
        yield return new GatewayChatApprovalRequest(
            "req-1",
            "tool-call-1",
            "invoke_agent_roller",
            """{"message":"roll a d100"}""",
            RunId: "run-1",
            CheckpointId: null);
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            "agent-a",
            "session-1",
            string.Empty,
            false,
            new GatewayMetrics(10, 0, 150),
            Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval)));

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> ApprovalPausedAfterPlannedWorkEvents()
    {
        yield return new GatewayChatToolCall("tool-call-1", "invoke_agent_home_assistant", "{}", "agent");
        yield return new GatewayChatToolResult("tool-call-1", "{}");
        yield return new GatewayChatToolCall("tool-call-2", "invoke_agent_roller", "{}", "agent");
        yield return new GatewayChatToolResult("tool-call-2", "{}");
        yield return new GatewayChatToolCall("tool-call-3", "invoke_agent_home_assistant", "{}", "agent");
        yield return new GatewayChatToolResult("tool-call-3", "{}");
        yield return new GatewayChatToolCall("tool-call-4", "kautuka_web_fetch", "{}", "connector");
        yield return new GatewayChatToolResult("tool-call-4", "{}");
        yield return new GatewayChatApprovalRequest("req-1", "tool-call-4", "kautuka_web_fetch", "{}", RunId: "run-1", CheckpointId: null, ToolType: "connector");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> GateBeforeStartApprovalCompletedEvents()
    {
        yield return new GatewayChatToolCall("tool-call-1", "invoke_agent_roller", """{"message":"roll a d100"}""", "agent");
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            "agent-a",
            "session-approval",
            "rolled",
            false,
            new GatewayMetrics(10, 0, 150),
            null,
            null,
            []));

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<GatewayChatStreamEvent> ApprovalCompletedEvents()
    {
        yield return new GatewayChatStreamDone(new GatewayChatResult(
            "agent",
            "agent-a",
            "session-approval",
            "completed",
            false,
            new GatewayMetrics(10, 0, 150),
            null,
            null,
            []));

        await Task.CompletedTask;
    }

    private static IEnumerable<NaradPersistedEvent> TurnProgressThenFinishedEvents()
    {
        yield return NaradTestEventFactory.FromBody(
            NaradEventTypes.ProgressRecorded,
            NaradEventBodies.ProgressRecorded(
                "agent-turn:run-1:1",
                NaradProgressCategories.AgentTurn,
                "Agent turn 1",
                "completed",
                "public",
                "no tool calls",
                NaradEventBodies.Usage(1230, 70)),
            "session-1",
            "run-1",
            "run-1",
            1,
            1);
        yield return NaradTestEventFactory.FromBody(
            NaradEventTypes.ProgressRecorded,
            NaradEventBodies.ProgressRecorded(
                "agent-turn:run-1:2",
                NaradProgressCategories.AgentTurn,
                "Agent turn 2",
                "completed",
                "public",
                "no tool calls",
                NaradEventBodies.Usage(1386, 62)),
            "session-1",
            "run-1",
            "run-1",
            2,
            2);
        yield return NaradTestEventFactory.FromBody(
            NaradEventTypes.RunFinished,
            NaradEventBodies.RunFinished(NaradEventBodies.Usage(2616, 132, 400), "done"),
            "session-1",
            "run-1",
            "run-1",
            3,
            3);
    }

    private sealed class ScriptedNaradAgentChatService(IEnumerable<NaradPersistedEvent> events) : IAgentChatService
    {
        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            string agentId,
            string sessionId,
            string? userId,
            string runTrigger,
            string message,
            string? stageTransitionId,
            IReadOnlyList<GatewayConversationAsset> inputAssets,
            IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
            IReadOnlyList<InjectedMemoryFact> memoryFacts,
            ToolApprovalResponseInfo? approvalResponse,
            AgentContinuationRequest? continuationRequest,
            AgentRequestContext? requestContext = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = (agentId, sessionId, userId, runTrigger, message, stageTransitionId, inputAssets, workspaceFiles, memoryFacts, approvalResponse, continuationRequest, requestContext);
            foreach (NaradPersistedEvent persistedEvent in events)
            {
                ct.ThrowIfCancellationRequested();
                yield return persistedEvent;
                await Task.Yield();
            }
        }
    }

    private sealed class FakeAgentChatService(IAsyncEnumerable<GatewayChatStreamEvent>? events = null) : IAgentChatService
    {
        public string? LastMessage { get; private set; }
        public AgentRequestContext? LastRequestContext { get; private set; }
        public AgentContinuationRequest? LastContinuationRequest { get; private set; }
        public IReadOnlyList<AgentWorkspaceFileReference> LastWorkspaceFiles { get; private set; } = [];
        public List<AgentInvocation> Invocations { get; } = [];

        public IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            string agentId,
            string sessionId,
            string? userId,
            string runTrigger,
            string message,
            string? stageTransitionId,
            IReadOnlyList<GatewayConversationAsset> inputAssets,
            IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
            IReadOnlyList<InjectedMemoryFact> memoryFacts,
            ToolApprovalResponseInfo? approvalResponse,
            AgentContinuationRequest? continuationRequest,
            AgentRequestContext? requestContext = null,
            CancellationToken ct = default)
        {
            LastMessage = message;
            LastRequestContext = requestContext;
            LastContinuationRequest = continuationRequest;
            LastWorkspaceFiles = workspaceFiles;
            Invocations.Add(new AgentInvocation(
                agentId,
                sessionId,
                runTrigger,
                message,
                [.. workspaceFiles]));

            if (events is not null)
            {
                return NaradTestEventFactory.ToNaradEvents(events, ct);
            }

            string response = $"analysis: {message} | files={string.Join(',', workspaceFiles.Select(static file => file.RelativePath))}";
            return NaradTestEventFactory.ToNaradEvents(DefaultEvents(response, agentId, sessionId), ct);
        }

        private static async IAsyncEnumerable<GatewayChatStreamEvent> DefaultEvents(string response, string agentId, string sessionId)
        {
            yield return new GatewayChatSessionReady(sessionId);
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                "agent",
                agentId,
                sessionId,
                response,
                false,
                new GatewayMetrics(0, 0, 0),
                null,
                null,
                []));

            await Task.CompletedTask;
        }
    }

    private sealed class FixedSessionLifecycleService(GatewaySession session) : ISessionLifecycleService
    {
        public ValueTask<GatewaySession> CreateSessionAsync(string targetType, string targetId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
            => ValueTask.FromResult(session);

        public ValueTask<GatewaySession> EnsureSessionAsync(string targetType, string targetId, string sessionId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
            => ValueTask.FromResult(session);
    }

    private sealed record AgentInvocation(
        string AgentId,
        string SessionId,
        string RunTrigger,
        string Message,
        IReadOnlyList<AgentWorkspaceFileReference> WorkspaceFiles);

    private sealed class RecordingNotificationDispatcher : IAutopilotNotificationDispatcher
    {
        public List<string?> Messages { get; } = [];

        public Task DispatchNotificationsAsync(
            GatewayScheduledRun run,
            string scheduleId,
            string? startedMessage,
            string? finalMessage,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            Messages.Add(startedMessage ?? finalMessage);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAutopilotStore : IGatewayAutopilotStore
    {
        public List<RunTransition> StateTransitions { get; } = [];
        public List<RunMetadataUpdate> MetadataUpdates { get; } = [];

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetRunById(string runId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit) => throw new NotSupportedException();

        public bool TransitionRunState(
            string runId,
            string expectedCurrentStatus,
            string nextStatus,
            string? error,
            string? outputJson,
            string? sessionId,
            string? runtimeRunId,
            string? preRunApprovalRequestId,
            DateTimeOffset nowUtc,
            bool markStarted,
            bool markEnded)
        {
            StateTransitions.Add(new RunTransition(runId, expectedCurrentStatus, nextStatus, sessionId, outputJson, error));
            return true;
        }

        public bool UpdateRunMetadata(
            string runId,
            string? sessionId,
            string? runtimeRunId,
            string? preRunApprovalRequestId,
            string? outputJson,
            string? error,
            DateTimeOffset nowUtc)
        {
            MetadataUpdates.Add(new RunMetadataUpdate(runId, sessionId, runtimeRunId));
            return true;
        }

        public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc) => false;

        public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset) => throw new NotSupportedException();
        public void RecordSuppression(GatewayAutopilotSuppression suppression) => throw new NotSupportedException();
        public void RecordNotification(GatewayAutopilotNotification notification) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly) => throw new NotSupportedException();
        public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc) => throw new NotSupportedException();
    }

    private sealed record RunTransition(
        string RunId,
        string ExpectedCurrentStatus,
        string NextStatus,
        string? SessionId,
        string? OutputJson,
        string? Error);

    private sealed record RunMetadataUpdate(
        string RunId,
        string? SessionId,
        string? RuntimeRunId);

    private sealed class FakeUserStore(GatewayUser user) : IGatewayUserStore
    {
        public bool TryGetUserById(string userId, out GatewayUser? found)
        {
            found = string.Equals(user.Id, userId, StringComparison.Ordinal) ? user : null;
            return found is not null;
        }

        public bool TryGetUserByUsername(string username, out GatewayUser? found)
        {
            found = string.Equals(user.Username, username, StringComparison.Ordinal) ? user : null;
            return found is not null;
        }

        public IReadOnlyList<GatewayUserListItem> ListUsers() => [];
        public bool CanSetup => false;
        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) => throw new NotSupportedException();
        public GatewayUserListItem? InviteUser(JsonElement body, out string? error) => throw new NotSupportedException();
        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) => throw new NotSupportedException();
        public bool UpdateIsActive(string userId, bool isActive, out string? error) => throw new NotSupportedException();
        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => throw new NotSupportedException();
        public bool UpdateProfile(string userId, string? email, string? displayName) => throw new NotSupportedException();
        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) => throw new NotSupportedException();


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    private sealed class StubAgentFilePathAuthorizationService(string workspaceRoot) : IAgentFilePathAuthorizationService
    {
        public AgentFilePathAuthorizationResult Authorize(string agentId, GatewayUser user, string? relativePath)
        {
            string normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            string resolvedPath = Path.Combine(workspaceRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            return new AgentFilePathAuthorizationResult(
                AgentFileOperationStatus.Success,
                null,
                workspaceRoot,
                resolvedPath,
                normalized,
                null);
        }
    }

    private sealed class NoOpAgentFilePathAuthorizationService : IAgentFilePathAuthorizationService
    {
        public AgentFilePathAuthorizationResult Authorize(string agentId, GatewayUser user, string? relativePath)
            => new(
                AgentFileOperationStatus.InvalidPath,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                "Workspace file resolution not configured in this test.");
    }

    private sealed class FakeRunWaitStateStore(RunWaitState state) : IRunWaitStateStore
    {
        public string? ResumedWaitId { get; private set; }

        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId) => string.Equals(state.WaitId, waitId, StringComparison.Ordinal) ? state : null;
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId) => string.Equals(state.ContinuationRunId, continuationRunId, StringComparison.Ordinal) && IsActive ? state : null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId) => string.Equals(state.CorrelationId, correlationId, StringComparison.Ordinal) && IsActive ? state : null;
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId)
            => string.Equals(state.SessionId, callerSessionId, StringComparison.Ordinal)
                && string.Equals(state.ToolCallId, callerToolCallId, StringComparison.Ordinal)
                && string.Equals(state.WaitReason, RunWaitReasons.Child, StringComparison.Ordinal)
                && IsActive
                ? state
                : null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => IsActive;
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }
        public void ReleaseForRetry(string waitId, string leaseOwner) { }
        public void MarkPausedAgain(string waitId) => ResumedWaitId = waitId;

        public void ReleaseExecutingWait(string waitId) => ResumedWaitId = waitId;
        public void MarkCompleted(string waitId) => ResumedWaitId = waitId;
        public void MarkFailed(string waitId, string failureCode) => ResumedWaitId = waitId;
        public IReadOnlyList<RunWaitState> ListRecoverable() => IsActive ? [state] : [];
        public void DeleteBySession(string sessionId) { }
        private bool IsActive => state.Status is RunWaitStatuses.Ready or RunWaitStatuses.Executing;
    }

    private sealed class NoOpContinuationStore : IGatewayContinuationStore
    {
        public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint)
            => throw new NotSupportedException();

        public int GetNextCheckpointIndex(string continuationRunId) => 0;

        public GatewayContinuationCheckpoint? GetById(string checkpointId)
            => null;

        public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash)
            => null;

        public void DeleteBySessions(IReadOnlyCollection<string> sessionIds)
        {
        }
    }

    private sealed class NoOpRunWaitStateStore : IRunWaitStateStore
    {
        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId) => null;
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId) => null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId) => null;
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId) => null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => false;
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }
        public void ReleaseForRetry(string waitId, string leaseOwner) { }
        public void MarkPausedAgain(string waitId) { }
        public void ReleaseExecutingWait(string waitId) { }
        public void MarkCompleted(string waitId) { }
        public void MarkFailed(string waitId, string failureCode) { }
        public IReadOnlyList<RunWaitState> ListRecoverable() => [];
        public void DeleteBySession(string sessionId) { }
    }

    private sealed class NoOpSessionCompactionService : ISessionCompactionService
    {
        public ValueTask<GatewaySession?> CompactAsync(string sessionId, string reason, CancellationToken cancellationToken)
            => ValueTask.FromResult<GatewaySession?>(null);
    }

    private sealed class RecordingSessionCompactionService : ISessionCompactionService
    {
        public int CallCount { get; private set; }
        public string? LastSessionId { get; private set; }
        public string? LastReason { get; private set; }

        public ValueTask<GatewaySession?> CompactAsync(string sessionId, string reason, CancellationToken cancellationToken)
        {
            CallCount++;
            LastSessionId = sessionId;
            LastReason = reason;

            GatewaySession compacted = new(
                sessionId,
                "agent",
                "agent-a",
                "user-1",
                "Session",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                true,
                "auto compaction summary",
                EstimatedContextTokens: 10_000,
                LastCompactedAt: DateTimeOffset.UtcNow.ToString("O"),
                LastCompactionReason: reason);

            return ValueTask.FromResult<GatewaySession?>(compacted);
        }
    }

    private sealed class FakeSessionLifecycleService : ISessionLifecycleService
    {
        public string? LastCreateOriginator { get; private set; }
        public string? LastEnsureOriginator { get; private set; }

        public ValueTask<GatewaySession> CreateSessionAsync(string targetType, string targetId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
        {
            LastCreateOriginator = originator;
            return ValueTask.FromResult(new GatewaySession(
                Id: "session-1",
                TargetType: targetType,
                TargetId: targetId,
                OwnerUserId: ownerUserId,
                Title: "Session",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Messages: [],
                Compacted: false,
                CompactSummary: null,
                Originator: originator));
        }

        public ValueTask<GatewaySession> EnsureSessionAsync(string targetType, string targetId, string sessionId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
        {
            LastEnsureOriginator = originator;
            return ValueTask.FromResult(new GatewaySession(
                Id: sessionId,
                TargetType: targetType,
                TargetId: targetId,
                OwnerUserId: ownerUserId,
                Title: "Session",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                Messages: [],
                Compacted: false,
                CompactSummary: null,
                Originator: originator));
        }
    }

    private sealed class FakeSessionStore : IGatewaySessionStore
    {
        public List<GatewayMessage> UserMessages { get; } = [];
        public List<GatewayMessage> AssistantMessages { get; } = [];
        public List<(GatewayMessage UserMessage, GatewayMessage AssistantMessage)> UserAssistantPairs { get; } = [];
        public List<GatewayRun> Runs { get; } = [];
        public GatewaySession? StoredSession { get; set; }

        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => [];
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => null;
        public GatewaySession? ArchiveSession(string sessionId) => null;
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => [];
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => [];
        public bool TryGetSession(string sessionId, out GatewaySession? session)
        {
            session = StoredSession is not null && string.Equals(StoredSession.Id, sessionId, StringComparison.Ordinal)
                ? StoredSession
                : null;
            return session is not null;
        }
        public bool DeleteSession(string sessionId) => false;
        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => null;
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
        {
            UserMessages.Add(userMessage);
            return new(sessionId, "agent", "agent-a", userId ?? string.Empty, "Session", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), [userMessage], false, null);
        }
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null)
        {
            AssistantMessages.Add(assistantMessage);
            return new(sessionId, "agent", "agent-a", userId ?? string.Empty, "Session", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), [assistantMessage], false, null);
        }
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => new([], []);
        public bool DeleteMemoryFact(string factId) => false;
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => false;
        public IReadOnlyList<GatewayRun> ListRuns() => [];
        public bool TryGetRun(string runId, out GatewayRun? run) { run = null; return false; }

        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
        {
            UserAssistantPairs.Add((userMessage, assistantMessage));
            return new(sessionId, "agent", "agent-a", userId ?? string.Empty, "Session", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), [userMessage, assistantMessage], false, null);
        }

        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null)
        {
            GatewayRun run = new(
                runId ?? "run-1",
                userId,
                targetType,
                targetId,
                sessionId,
                null,
                "completed",
                new Dictionary<string, object?> { ["message"] = input },
                new Dictionary<string, object?> { ["response"] = output },
                effectiveBehavior,
                error,
                DateTimeOffset.UtcNow.AddMilliseconds(-(metrics?.ElapsedMs ?? 0)).ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                metrics?.ElapsedMs ?? 0,
                traceId);
            Runs.Add(run);
            return run;
        }

        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => throw new NotSupportedException();
    }

    private sealed class CapturingUsageQueue : IGatewayUsageQueue
    {
        public List<GatewayUsageFact> Facts { get; } = [];

        public bool TryEnqueue(GatewayUsageFact fact)
        {
            Facts.Add(fact);
            return true;
        }

        public bool TryDequeue(out GatewayUsageFact? fact)
        {
            fact = null;
            return false;
        }

        public async IAsyncEnumerable<GatewayUsageFact> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoOpFactExtractor : IMemoryFactExtractor
    {
        public Task<IReadOnlyList<GatewayMemoryFact>> ExtractAsync(
            Microsoft.Extensions.AI.IChatClient? client,
            string targetType,
            string targetId,
            string ownerUserId,
            string userMessage,
            string assistantMessage,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewayMemoryFact>>([]);
    }

    private sealed class PassthroughMemoryScopePolicy : IMemoryScopePolicy
    {
        public IReadOnlyList<GatewayMemoryScope> GetReadScopes(string targetType, string targetId)
            => [new GatewayMemoryScope(targetType, targetId)];

        public GatewayMemoryScope GetWriteScope(string targetType, string targetId)
            => new(targetType, targetId);
    }

    private sealed class NoOpChatMemoryAssembler : IChatMemoryAssembler
    {
        public Task<IReadOnlyList<InjectedMemoryFact>> AssembleAsync(string targetType, string targetId, string? queryText, string? actingUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InjectedMemoryFact>>([]);
    }

    private sealed class StaticPlatformConfigurationProvider : IPlatformConfigurationProvider
    {
        private readonly PlatformConfiguration configuration;

        public StaticPlatformConfigurationProvider()
            : this(new PlatformConfiguration())
        {
        }

        public StaticPlatformConfigurationProvider(PlatformConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public PlatformConfiguration GetCurrent() => configuration;
    }

    private sealed class EmptyCatalogStore : IGatewayCatalogStore
    {
        private readonly GatewayAgent? agent;

        public EmptyCatalogStore(GatewayAgent? agent = null)
        {
            this.agent = agent;
        }

        public bool TryGetProvider(string id, out GatewayProvider? provider) { provider = null; return false; }
        public IReadOnlyList<GatewayProvider> ListProviders() => [];
        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();
        public bool DeleteProvider(string id) => false;
        public bool TryGetModel(string id, out GatewayModel? model) { model = null; return false; }
        public IReadOnlyList<GatewayModel> ListModels() => [];
        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();
        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) => throw new NotSupportedException();
        public bool DeleteModel(string id) => false;
        public bool TryGetAgent(string id, out GatewayAgent? resolved)
        {
            resolved = agent is not null && string.Equals(id, agent.Id, StringComparison.Ordinal) ? agent : null;
            return resolved is not null;
        }
        public IReadOnlyList<GatewayAgent> ListAgents() => [];
        public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteAgent(string id) => false;
        public IReadOnlyList<GatewayMcpServer> ListMcpServers() => [];
        public bool TryGetMcpServer(string id, out GatewayMcpServer? server) { server = null; return false; }
        public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId) => throw new NotSupportedException();
        public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames) => throw new NotSupportedException();
        public bool DeleteMcpServer(string id) => false;
        public IReadOnlyList<GatewaySkill> ListSkills() => [];
        public bool TryGetSkill(string id, out GatewaySkill? skill) { skill = null; return false; }
        public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId) => throw new NotSupportedException();
        public bool DeleteSkill(string id) => false;
    }

    private sealed class EmptyUserStore : IGatewayUserStore
    {
        public bool TryGetUserById(string userId, out GatewayUser? user) { user = null; return false; }
        public bool TryGetUserByUsername(string username, out GatewayUser? user) { user = null; return false; }
        public IReadOnlyList<GatewayUserListItem> ListUsers() => [];
        public bool CanSetup => false;
        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) { error = null; return null; }
        public GatewayUserListItem? InviteUser(JsonElement body, out string? error) { error = null; return null; }
        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) { error = null; return false; }
        public bool UpdateIsActive(string userId, bool isActive, out string? error) { error = null; return false; }
        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => false;
        public bool UpdateProfile(string userId, string? email, string? displayName) => false;
        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) { error = null; return false; }


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    private sealed class NoOpApprovalStore : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) { }
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }
}
