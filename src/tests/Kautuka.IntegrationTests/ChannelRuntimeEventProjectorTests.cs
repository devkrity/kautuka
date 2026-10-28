using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelRuntimeEventProjectorTests
{
    private static readonly string[] responseEventTypes = [NaradEventTypes.MessageTextDelta, NaradEventTypes.MessageTextCompleted, NaradEventTypes.RunFinished];
    [Theory]
    [InlineData("autopilot", false)]
    [InlineData("chat", true)]
    [InlineData("hitl", true)]
    public async Task ResponseDeliveryUsesTheCurrentRunSourceRatherThanSessionOrigin(string source, bool mirrored)
    {
        var outbox = new CapturingChannelOutboxStore();
        var sessions = new FakeSessionStore(new GatewaySession("session-1", "agent", "agent-1", "user-1", "Autopilot session",
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), [], false, null, Originator: "autopilot"));
        var projector = CreateProjector(outbox, new FakeChannelAttachmentRegistry(), new FakeConversationMapStore(null), sessions);
        string runId = Guid.NewGuid().ToString("N");
        var context = new NaradEventContext("agent-1", "session-1", "user-1", runId, runId, SourceChannel: source);
        foreach (string eventType in responseEventTypes)
        {
            JsonElement body = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["messageId"] = runId,
                ["delta"] = "Final answer",
                ["response"] = "Final answer",
            }, KautukaJsonContext.Default.JsonObject);
            NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(eventType, body, context);
            Assert.Equal(source, draft.Body.GetProperty("sourceChannel").GetString());
            NaradPersistedEvent evt = CreateEvent(draft.EventId, eventType, "session-1", runId, draft.Body);
            await projector.OnPublishedAsync(evt, CancellationToken.None).ConfigureAwait(true);
            await projector.ProjectBoundAsync(evt, "reg-1", "chat-1", "101", cancellationToken: CancellationToken.None).ConfigureAwait(true);
        }
        if (mirrored) Assert.NotEmpty(outbox.Entries);
        else Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncMirrorsChannelPromptToLinkedOtherProvidersWithoutEchoingToOrigin()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry
        {
            Registrations =
            [
                CreateChatRegistration("reg-slack", "slack", "slack-chat"),
                CreateChatRegistration("reg-telegram", "telegram", "telegram-chat"),
            ],
        };
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(null));
        NaradPersistedEvent prompt = CreateEvent(
            "evt-slack-prompt",
            NaradEventTypes.MessageUserRecorded,
            "session-slack",
            "run-slack",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["sourceChannel"] = "slack",
                ["messageId"] = "prompt-1",
                ["content"] = "show me that attachment",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(prompt, CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry mirrored = Assert.Single(outbox.Entries);
        Assert.Equal("reg-telegram", mirrored.RegistrationId);
        Assert.Contains("show me that attachment", mirrored.PayloadJson, StringComparison.Ordinal);
    }

    private static GatewayChannelRegistration CreateChatRegistration(string id, string channelType, string conversationId)
        => new(id, channelType, "user-1", $"bot-{id}", true,
            new JsonObject
            {
                [channelType == "slack" ? "slack_dm_channel_id" : "channel_device_id"] = conversationId,
            }, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));

    [Theory]
    [InlineData("C123", false)]
    [InlineData("C123", true)]
    [InlineData("D-OTHER", false)]
    [InlineData("D-OTHER", true)]
    public async Task SlackProjectionPinsSessionMappedOutputToConfiguredOwnerDm(string mappedConversation, bool bound)
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry
        {
            Registrations = [CreateChatRegistration("reg-slack", "slack", "D-OWNER")],
        };
        var map = new ChannelConversationMapEntry("map", "slack", "reg-slack", "agent", "agent-1", mappedConversation,
            "thread-1", "transient_thread", "session-1", "", "", null);
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(map));
        NaradPersistedEvent evt = CreateEvent("slack-pinned-" + mappedConversation + bound, NaradEventTypes.RunFinished,
            "session-1", "run-slack", JsonSerializer.SerializeToElement(new JsonObject { ["agentId"] = "agent-1", ["response"] = "Reply" }, KautukaJsonContext.Default.JsonObject));

        if (bound)
            await projector.ProjectBoundAsync(evt, "reg-slack", mappedConversation, "thread-1", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        else
            await projector.ProjectAsync(evt, TestContext.Current.CancellationToken).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        ChannelRuntimeEventAction? action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        Assert.Equal("slack", entry.ChannelType);
        Assert.Equal("D-OWNER", action?.ConversationId);
        Assert.Null(action?.ConversationThreadId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlackProjectionDoesNotEnqueueWhenOwnerDmIsMissing(bool bound)
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry
        {
            Registrations = [new GatewayChannelRegistration("reg-slack", "slack", "user-1", "Slack", true, new JsonObject(), "", "")],
        };
        var map = new ChannelConversationMapEntry("map", "slack", "reg-slack", "agent", "agent-1", "D-OTHER",
            "thread-1", "transient_thread", "session-1", "", "", null);
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(map));
        NaradPersistedEvent evt = CreateEvent("slack-missing-dm" + bound, NaradEventTypes.RunFinished, "session-1", "run-slack",
            JsonSerializer.SerializeToElement(new JsonObject { ["agentId"] = "agent-1", ["response"] = "Reply" }, KautukaJsonContext.Default.JsonObject));

        if (bound)
            await projector.ProjectBoundAsync(evt, "reg-slack", "D-OTHER", "thread-1", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        else
            await projector.ProjectAsync(evt, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotProjectNestedChildAndKeepsParentMapSession()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var parentMap = new ChannelConversationMapEntry(
            "map-parent",
            "telegram",
            "reg-1",
            "agent",
            "agent-parent",
            "chat-1",
            "chat-1",
            "transient_thread",
            "session-parent",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var mapStore = new FakeConversationMapStore(parentMap);
        ChannelRuntimeEventProjector projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            agents: [("agent-parent", "My Assistant"), ("agent-child", "Roller")]);

        NaradPersistedEvent childFinished = CreateEvent(
            "child-run-1",
            NaradEventTypes.RunFinished,
            "session-child",
            "run-child",
            JsonSerializer.SerializeToElement(
                new JsonObject
                {
                    ["userId"] = "user-1",
                    ["agentId"] = "agent-child",
                    ["response"] = "I cannot perform that action.",
                },
                KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(childFinished, CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(outbox.Entries);
        ChannelConversationMapEntry? stillParent = mapStore.TryResolveBySession("session-parent", "telegram");
        Assert.NotNull(stillParent);
        Assert.Equal("session-parent", stillParent.SessionId);
        Assert.Equal("agent-parent", stillParent.TargetId);
    }

    [Fact]
    public async Task ProjectAsyncSkipsAaatParentSessionProjectionLane()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var parentMap = new ChannelConversationMapEntry(
            "map-parent",
            "telegram",
            "reg-1",
            "agent",
            "agent-parent",
            "chat-1",
            "chat-1",
            "transient_thread",
            "session-parent",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var mapStore = new FakeConversationMapStore(parentMap);
        ChannelRuntimeEventProjector projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            agents: [("agent-parent", "My Assistant"), ("agent-child", "Roller")]);

        // Parent conversation lane of a nested child terminal (sourceEventId set).
        NaradPersistedEvent projectedChild = CreateEvent(
            "aaatproj:child-evt",
            NaradEventTypes.RunFinished,
            "session-parent",
            "run-child",
            JsonSerializer.SerializeToElement(
                new JsonObject
                {
                    ["userId"] = "user-1",
                    ["agentId"] = "agent-child",
                    ["response"] = "The result of the d45 roll is 4.",
                    ["sourceEventId"] = "child-evt",
                    ["sourceSessionId"] = "session-child",
                },
                KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(projectedChild, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncSkipsNestedChildUserMessageWithParentLineage()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-parent",
                "telegram",
                "reg-1",
                "agent",
                "agent-parent",
                "chat-1",
                "chat-1",
                "transient_thread",
                "session-parent",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            agents: [("agent-parent", "My Assistant"), ("agent-child", "Roller")]);

        // Child roller prompt for AAAT: same text as surface, must not re-echo on Telegram.
        NaradPersistedEvent childUser = CreateEvent(
            "evt-child-user",
            NaradEventTypes.MessageUserRecorded,
            "session-child",
            "run-child",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["userId"] = "user-1",
                ["agentId"] = "agent-child",
                ["content"] = "Roll a d88",
                ["messageId"] = "u-child",
                ["parentSessionId"] = "session-parent",
                ["parentToolCallId"] = "tc-invoke",
                ["parentRunId"] = "run-parent",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(childUser, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncSkipsAgentToolOriginSessions()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var mapStore = new FakeConversationMapStore(null);
        var sessionStore = new FakeSessionStore(new GatewaySession(
            "session-child",
            "agent",
            "agent-child",
            "user-1",
            "title",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null,
            Originator: "agent_tool"));
        var projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            sessionStore,
            agents: [("agent-parent", "My Assistant"), ("agent-child", "Roller")]);

        NaradPersistedEvent childUser = CreateEvent(
            "evt-agent-tool-user",
            NaradEventTypes.MessageUserRecorded,
            "session-child",
            "run-child",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["userId"] = "user-1",
                ["content"] = "Roll a d88",
                ["messageId"] = "u-aaat",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(childUser, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotEchoUserMessageForTelegramOriginSession()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-tg",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var sessionStore = new FakeSessionStore(new GatewaySession(
            "session-tg",
            "agent",
            "agent-1",
            "user-1",
            "title",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null,
            Originator: "telegram"));
        var projector = CreateProjector(outbox, attachments, mapStore, sessionStore);
        NaradPersistedEvent userMessage = CreateEvent(
            "evt-user-tg",
            NaradEventTypes.MessageUserRecorded,
            "session-tg",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["content"] = "roll a d45",
                ["messageId"] = "u1",
                ["agentId"] = "agent-1",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(userMessage, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotEchoUserMessageWhenSourceChannelIsTelegram()
    {
        // Admittance stamp alone must suppress re-echo; session originator is not required.
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-chat",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent userMessage = CreateEvent(
            "evt-user-src",
            NaradEventTypes.MessageUserRecorded,
            "session-chat",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["content"] = "roll a d45",
                ["messageId"] = "u1",
                ["agentId"] = "agent-1",
                ["userId"] = "user-1",
                ["sourceChannel"] = "telegram",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(userMessage, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncProjectsBrowserUserMessageWithChatSourceChannel()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-chat",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent userMessage = CreateEvent(
            "evt-user-browser",
            NaradEventTypes.MessageUserRecorded,
            "session-chat",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["content"] = "roll a d45",
                ["messageId"] = "u1",
                ["agentId"] = "agent-1",
                ["userId"] = "user-1",
                ["sourceChannel"] = "chat",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(userMessage, CancellationToken.None).ConfigureAwait(true);
        Assert.Single(outbox.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("101")]
    public async Task ProjectAsyncBackgroundReplyBecomesSelectedSessionInItsActualTopic(string? topic)
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        if (topic is not null) attachments.Topics["agent:agent-parent"] = topic;
        var oldMap = new ChannelConversationMapEntry(
            "map-parent",
            "telegram",
            "reg-1",
            "agent",
            "agent-parent",
            "chat-1",
            topic ?? "chat-1",
            "permanent_topic",
            "session-old",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var mapStore = new FakeConversationMapStore(oldMap);
        ChannelRuntimeEventProjector projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            agents: [("agent-parent", "My Assistant")]);

        NaradPersistedEvent parentFinished = CreateEvent(
            "parent-run-new",
            NaradEventTypes.RunFinished,
            "session-new",
            "run-new",
            JsonSerializer.SerializeToElement(
                new JsonObject
                {
                    ["userId"] = "user-1",
                    ["agentId"] = "agent-parent",
                    ["response"] = "rolled 4",
                },
                KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(parentFinished, CancellationToken.None).ConfigureAwait(true);

        Assert.Single(outbox.Entries);
        var coordinator = new ChannelRoutingCoordinator(null!, attachments, mapStore);
        var envelope = new ChannelInboundEnvelope("reply", "user", "chat-1", topic ?? "chat-1", topic, "next message", false, null, null, null, null, []);
        ChannelConversationMapEntry rebound = coordinator.ResolveConversationMap("reg-1", attachments.GetBindings("reg-1")[0], envelope);
        Assert.NotNull(rebound);
        Assert.Equal("session-new", rebound.SessionId);
        Assert.Equal("agent-parent", rebound.TargetId);
    }

    [Fact]
    public async Task ProjectAsyncMirrorsAnotherOwnedAgentOntoTheUserChatWithoutRebindingIt()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var parentMap = new ChannelConversationMapEntry(
            "map-parent",
            "telegram",
            "reg-1",
            "agent",
            "agent-parent",
            "chat-1",
            "chat-1",
            "transient_thread",
            "session-parent",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var mapStore = new FakeConversationMapStore(parentMap);
        var sessionStore = new FakeSessionStore(new GatewaySession(
            "session-new",
            "agent",
            "agent-new",
            "user-1",
            "title",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null,
            Originator: "chat"));
        ChannelRuntimeEventProjector projector = CreateProjector(
            outbox,
            attachments,
            mapStore,
            sessionStore,
            agents: [("agent-parent", "My Assistant"), ("agent-new", "New Agent")]);

        NaradPersistedEvent finished = CreateEvent(
            "new-agent-run",
            NaradEventTypes.RunFinished,
            "session-new",
            "run-new",
            JsonSerializer.SerializeToElement(
                new JsonObject
                {
                    ["userId"] = "user-1",
                    ["agentId"] = "agent-new",
                    ["response"] = "hello from the new agent",
                },
                KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(finished, CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Contains("\"conversationId\":\"chat-1\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"agentDisplayName\":\"New Agent\"", entry.PayloadJson, StringComparison.Ordinal);
        ChannelConversationMapEntry? stillParent = mapStore.TryResolveBySession("session-parent", "telegram");
        Assert.NotNull(stillParent);
        Assert.Equal("session-parent", stillParent.SessionId);
        Assert.Equal("agent-new", mapStore.TryResolveBySession("session-new", "telegram")?.TargetId);
    }

    [Theory]
    [InlineData("chat-1", null, false)]
    [InlineData("target:AGENT:agent-child", null, false)]
    [InlineData("task:one", null, false)]
    [InlineData("topic-target:OTk5:AGENT:agent-child", null, false)]
    [InlineData("topic-target:OTk5:AGENT:agent-child", null, true)]
    public async Task ProjectorKeepsTelegramRuntimeEventsInTheRootChat(string internalKey, string? expectedThread, bool boundFirst)
    {
        var attachments = new FakeChannelAttachmentRegistry { Topics = new JsonObject { ["agent:agent-child"] = "202" } };
        var outbox = new CapturingChannelOutboxStore();
        var map = new ChannelConversationMapEntry("map", "telegram", "reg-1", "agent", "agent-child", "chat-1", internalKey, "transient_thread", "selected-session", "", "", null);
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(map));
        var evt = CreateEvent($"topic-{internalKey}", NaradEventTypes.RunFinished, "selected-session", $"run-{internalKey}", JsonSerializer.SerializeToElement(new JsonObject { ["agentId"] = "agent-child", ["response"] = "Reply" }, KautukaJsonContext.Default.JsonObject));
        if (boundFirst) await projector.ProjectBoundAsync(evt, "reg-1", "chat-1", null, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        await projector.ProjectAsync(evt, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await projector.ProjectBoundAsync(evt, "reg-1", "chat-1", null, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        ChannelRuntimeEventAction? action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        Assert.Equal(expectedThread, action?.ConversationThreadId);
        Assert.Equal("agent-child", action?.AgentId);
    }

    [Fact]
    public async Task BoundProjectionUsesSessionTargetRatherThanDefaultAgent()
    {
        var attachments = new FakeChannelAttachmentRegistry { Topics = new JsonObject { ["agent:agent-child"] = "202" } };
        var outbox = new CapturingChannelOutboxStore();
        var map = new ChannelConversationMapEntry("map", "telegram", "reg-1", "agent", "agent-child", "chat-1", "chat-1", "transient_thread", "selected-session", "", "", null);
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(map));
        var evt = CreateEvent("bound-target", NaradEventTypes.RunFinished, "selected-session", "bound-run", JsonSerializer.SerializeToElement(new JsonObject { ["agentId"] = "agent-child", ["response"] = "Reply" }, KautukaJsonContext.Default.JsonObject));
        await projector.ProjectBoundAsync(evt, "reg-1", "chat-1", null, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        ChannelRuntimeEventAction? action = JsonSerializer.Deserialize(Assert.Single(outbox.Entries).PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        Assert.Equal("agent-child", action?.AgentId);
        Assert.Null(action?.ConversationThreadId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("999")]
    public void TelegramRootMappingsRemainTargetScopedAndLegacyTopicsDoNotRoute(string? userTopic)
    {
        var attachments = new FakeChannelAttachmentRegistry { AgentIds = ["agent-1", "agent-child"] };
        Assert.True(attachments.TryGetRegistration("reg-1", out GatewayChannelRegistration? registration));
        var maps = new FakeConversationMapStore(null);
        var coordinator = new ChannelRoutingCoordinator(null!, attachments, maps);
        GatewayChannelTargetBinding[] bindings = attachments.GetBindings("reg-1");
        var root = new ChannelInboundEnvelope("evt", "user", "chat-1", userTopic ?? "chat-1", userTopic, "Hi", false, null, null, null, null, []);
        ChannelConversationMapEntry first = coordinator.ResolveConversationMap("reg-1", bindings[0], root);
        Assert.Equal("permanent_topic", first.ConversationKind);
        Assert.Null(first.ExpiresAt);
        maps.AttachSession(first.Id, "session-a");
        ChannelConversationMapEntry second = coordinator.ResolveConversationMap("reg-1", bindings[1], root);
        Assert.Null(second.SessionId);
        Assert.NotEqual(first.ProviderThreadId, second.ProviderThreadId);
        Assert.Null(ChannelThreadRouting.OutboundTopic(registration!, "chat-1", first.ProviderThreadId, bindings[0].TargetType, bindings[0].TargetId));
        if (userTopic is not null)
            Assert.Equal(first.ProviderThreadId, coordinator.ResolveConversationMap("reg-1", bindings[0], root with { ProviderThreadId = "998", MessageThreadId = "998" }).ProviderThreadId);
        maps.AttachSession(second.Id, "session-b");
        Assert.Equal("session-a", coordinator.ResolveConversationMap("reg-1", bindings[0], root).SessionId);
        ChannelConversationMapEntry task = coordinator.ResolveConversationMap("reg-1", bindings[0], root with { ProviderThreadId = "task:new" });
        Assert.Null(task.SessionId);
        Assert.Equal("transient_thread", task.ConversationKind);
        attachments.Topics["agent:agent-1"] = "101";
        Assert.True(attachments.TryGetRegistration("reg-1", out registration));
        Assert.Equal("agent-1", coordinator.ResolveTargetBinding(registration!, "user", "101")?.TargetId);
        Assert.Equal("agent-1", coordinator.ResolveTargetBinding(registration!, "user", null)?.TargetId);
        Assert.Null(coordinator.ResolveTargetBinding(registration!, "user", "999"));
    }

    [Fact]
    public void RetiredTopicNeverFallsBackToRemainingAgent()
    {
        var attachments = new FakeChannelAttachmentRegistry { RetiredTopics = new JsonObject { ["agent:old"] = "55" } };
        Assert.True(attachments.TryGetRegistration("reg-1", out GatewayChannelRegistration? registration));
        var coordinator = new ChannelRoutingCoordinator(null!, attachments, new FakeConversationMapStore(null));
        Assert.Null(coordinator.ResolveTargetBinding(registration!, "user", "55"));
        Assert.NotNull(coordinator.ResolveTargetBinding(registration!, "user", null));
    }

    private static ChannelRuntimeEventProjector CreateProjector(
        IChannelOutboxStore outbox,
        IChannelAttachmentRegistry attachments,
        IChannelConversationMapStore mapStore,
        IGatewaySessionStore? sessionStore = null,
        string? agentColor = null,
        params (string Id, string Name)[] agents)
    {
        GatewayState state = GatewayState.Create(
            null!,
            NullLogger<GatewayState>.Instance,
            new NaradTestSupport.NaradTestAgentStore(),
            null!,
            null!,
            null!,
            new GatewayMemoryStore());
        (string Id, string Name)[] seeded = agents.Length == 0
            ? [("agent-1", "Assistant"), ("agent-parent", "Assistant"), ("agent-child", "Child")]
            : agents;
        foreach ((string id, string name) in seeded)
        {
            state.Agents[id] = new GatewayAgent(
                id,
                name,
                "model-1",
                "system",
                "disabled",
                new JsonObject(),
                new JsonObject(),
                new JsonObject(),
                [],
                "user-1",
                DateTimeOffset.UtcNow.ToString("O"),
                Persona: id == "agent-1" && agentColor is not null
                    ? new JsonObject { ["icon"] = "🤖", ["color"] = agentColor }
                    : null);
        }

        return new ChannelRuntimeEventProjector(
            outbox,
            attachments,
            mapStore,
            state,
            sessionStore ?? new FakeSessionStore());
    }

    private static NaradPersistedEvent CreateEvent(
        string eventId,
        string eventType,
        string sessionId,
        string runId,
        JsonElement? body = null)
    {
        var envelope = new JsonObject
        {
            ["userId"] = "user-1",
            ["agentId"] = "agent-1",
        };
        if (body?.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in body.Value.EnumerateObject())
            {
                envelope[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return NaradStreamObservation.CreateSynthetic(
            eventId,
            eventType,
            sessionId,
            runId,
            runId,
            JsonSerializer.SerializeToElement(envelope, KautukaJsonContext.Default.JsonObject));
    }

    [Fact]
    public void ChannelProjectionKeepsFileOnlyUserMessageAttachmentIds()
    {
        NaradPersistedEvent persistedEvent = CreateEvent(
            "evt-file",
            NaradEventTypes.MessageUserRecorded,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["messageId"] = "user-file",
                ["content"] = "",
                ["attachmentIds"] = new JsonArray("asset-1"),
            }, KautukaJsonContext.Default.JsonObject));

        Assert.True(ChannelNaradProjectionPayload.TryBuild(persistedEvent, out string wireEventType, out string payloadJson));
        Assert.Equal(ChannelNaradProjectionPayload.WireEventTypes.UserMessageRecorded, wireEventType);
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        Assert.Equal("asset-1", payload.RootElement.GetProperty("attachmentIds")[0].GetString());
        Assert.Equal(string.Empty, payload.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ProjectAsyncEnqueuesRuntimeEventForRunFinishedWithActiveRegistration()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));

        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent persistedEvent = CreateEvent(
            "evt-run-finished-1",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["response"] = "done" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(persistedEvent, CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Equal(ChannelOutboundActionTypes.RuntimeEvent, entry.ActionType);
        Assert.Equal("reg-1", entry.RegistrationId);
        Assert.Equal("user-1", entry.OwnerUserId);
        Assert.Contains("\"eventType\":\"run.finished\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"conversationId\":\"chat-1\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"interactionId\":null", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"agentDisplayName\":\"Assistant\"", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAsyncCarriesConfiguredAgentIdentityColorToChannelHost()
    {
        var outbox = new CapturingChannelOutboxStore();
        ChannelRuntimeEventProjector projector = CreateProjector(
            outbox,
            new FakeChannelAttachmentRegistry(),
            new FakeConversationMapStore(null),
            agentColor: "#0F766E");
        NaradPersistedEvent persistedEvent = CreateEvent(
            "evt-agent-color",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-agent-color",
            JsonSerializer.SerializeToElement(new JsonObject { ["response"] = "done" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(persistedEvent, CancellationToken.None).ConfigureAwait(true);

        ChannelRuntimeEventAction action = JsonSerializer.Deserialize(
            Assert.Single(outbox.Entries).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal("🤖", action.AgentIcon);
        Assert.Equal("#0F766E", action.AgentColor);
    }

    [Fact]
    public async Task ProjectAsyncSkipsWhenUserHasNoActiveRegistration()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HasActiveRegistration = false };
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(null));
        NaradPersistedEvent persistedEvent = CreateEvent("evt-run-finished-2", NaradEventTypes.RunFinished, "session-1", "run-2");

        await projector.ProjectAsync(persistedEvent, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectBoundAsyncEnqueuesRuntimeEventForExplicitRegistration()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(null));
        NaradPersistedEvent persistedEvent = CreateEvent(
            "evt-bound-1",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-bound",
            JsonSerializer.SerializeToElement(new JsonObject { ["response"] = "hi" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectBoundAsync(
            persistedEvent,
            "reg-1",
            "chat-bound",
            "thread-bound",
            interactionId: null,
            CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Equal(ChannelOutboundActionTypes.RuntimeEvent, entry.ActionType);
        Assert.Contains("\"conversationId\":\"chat-bound\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"conversationThreadId\":null", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnPublishedAsyncProjectsChannelSourcedEvents()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "root",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var sessionStore = new FakeSessionStore(new GatewaySession(
            "session-1",
            "agent",
            "agent-1",
            "user-1",
            "title",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null,
            Originator: "telegram"));
        var projector = CreateProjector(outbox, attachments, mapStore, sessionStore);
        NaradPersistedEvent persistedEvent = CreateEvent(
            "evt-channel-source",
            NaradEventTypes.RunFinished,
            "session-1",
            "run-ch",
            JsonSerializer.SerializeToElement(new JsonObject { ["response"] = "channel final" }, KautukaJsonContext.Default.JsonObject));

        await projector.OnPublishedAsync(persistedEvent, CancellationToken.None).ConfigureAwait(true);
        Assert.Single(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncSkipsNestedChildAgentEvents()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { DefaultAgentId = "agent-parent" };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-parent",
                "telegram",
                "reg-1",
                "agent",
                "agent-parent",
                "chat-1",
                "chat-1",
                "transient_thread",
                "session-parent",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var sessionStore = new FakeSessionStore(new GatewaySession(
            "session-child",
            "agent",
            "agent-child",
            "user-1",
            "title",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            [],
            false,
            null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent childFinished = CreateEvent(
            "evt-child-finished",
            NaradEventTypes.RunFinished,
            "session-child",
            "run-child",
            JsonSerializer.SerializeToElement(new JsonObject { ["response"] = "rolled 20" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(childFinished, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncEnqueuesUserMessagesInHitlMode()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent userMessage = CreateEvent(
            "evt-user-hitl",
            NaradEventTypes.MessageUserRecorded,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["content"] = "roll a d995",
                ["messageId"] = "u1",
                ["executionToken"] = "secret-token",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(userMessage, CancellationToken.None).ConfigureAwait(true);
        ChannelOutboxEntry hitlEntry = Assert.Single(outbox.Entries);
        Assert.Contains("\"eventType\":\"user.message.recorded\"", hitlEntry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("roll a d995", hitlEntry.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("executionToken", hitlEntry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectBoundAsyncIncludesApprovalDetailsButRedactsRuntimeIdentifiers()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var projector = CreateProjector(outbox, attachments, new FakeConversationMapStore(null));
        NaradPersistedEvent approval = CreateEvent(
            "evt-approval",
            NaradEventTypes.InterruptRequested,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["interruptId"] = "req-1",
                ["toolName"] = "run_shell",
                ["prompt"] = "Allow shell?",
                ["argumentsJson"] = "{\"cmd\":\"rm -rf /\"}",
                ["toolCallId"] = "tc-secret",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectBoundAsync(
            approval,
            "reg-1",
            "chat-bound",
            null,
            interactionId: "hitl-1",
            CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Contains("\"eventType\":\"tool.approval.requested\"", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("Allow shell?", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("argumentsJson", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("rm -rf /", entry.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("toolCallId", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotEnqueueApprovalsHitlHandlerOwnsChannelCards()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent approval = CreateEvent(
            "evt-approval-unbound",
            NaradEventTypes.InterruptRequested,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["interruptId"] = "req-1",
                ["toolName"] = "invoke_agent_roller",
            }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(approval, CancellationToken.None).ConfigureAwait(true);
        await projector.OnPublishedAsync(approval, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncIgnoresTraceAndProgressEvents()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        await projector.ProjectAsync(
            CreateEvent("evt-trace", NaradEventTypes.TraceEvent, "session-1", "run-1"),
            CancellationToken.None).ConfigureAwait(true);
        await projector.ProjectAsync(
            CreateEvent("evt-progress", NaradEventTypes.ProgressRecorded, "session-1", "run-1"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncFailsClosedOnMalformedNaradWithoutEnqueueing()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        var malformed = new NaradPersistedEvent(
            "evt-bad-run",
            NaradProtocolVersions.V1,
            NaradEventTypes.RunFinished,
            "session-1",
            1,
            NaradRecordClasses.Authority,
            "{not-json",
            DateTimeOffset.UtcNow.ToString("O"),
            "run-1",
            "run-1",
            1);

        await projector.ProjectAsync(malformed, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncAccumulatesTextDeltasAndDoesNotOutboxThem()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent delta = CreateEvent(
            "evt-delta-hitl",
            NaradEventTypes.MessageTextDelta,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m1", ["delta"] = "word " }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(delta, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task OnPublishedProjectsChannelSourcedSessionsForPostHitlFinals()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-tg",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var sessionStore = new FakeSessionStore(
            new GatewaySession(
                "session-tg",
                "agent",
                "agent-1",
                "user-1",
                "title",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                [],
                false,
                null,
                Originator: "telegram"));
        var projector = CreateProjector(outbox, attachments, mapStore, sessionStore);

        NaradPersistedEvent delta = CreateEvent(
            "evt-post-hitl-delta",
            NaradEventTypes.MessageTextDelta,
            "session-tg",
            "run-post",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-final", ["delta"] = "Done." }, KautukaJsonContext.Default.JsonObject));
        NaradPersistedEvent completed = CreateEvent(
            "evt-post-hitl-completed",
            NaradEventTypes.MessageTextCompleted,
            "session-tg",
            "run-post",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-final" }, KautukaJsonContext.Default.JsonObject));

        await projector.OnPublishedAsync(delta, CancellationToken.None).ConfigureAwait(true);
        await projector.OnPublishedAsync(completed, CancellationToken.None).ConfigureAwait(true);

        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Contains("assistant.message.completed", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("Done.", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectAsyncResolvesOwnerFromSessionWhenEventLacksUserId()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent delta = CreateEvent(
            "evt-no-userid-delta",
            NaradEventTypes.MessageTextDelta,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-no-user", ["delta"] = "hi" }, KautukaJsonContext.Default.JsonObject));
        NaradPersistedEvent completed = CreateEvent(
            "evt-no-userid",
            NaradEventTypes.MessageTextCompleted,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-no-user" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(delta, CancellationToken.None).ConfigureAwait(true);
        await projector.ProjectAsync(completed, CancellationToken.None).ConfigureAwait(true);
        Assert.Single(outbox.Entries);
    }

    [Fact]
    public async Task ProjectBoundAndOnPublishedDedupesSameEventId()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent delta = CreateEvent(
            "evt-dedupe-delta",
            NaradEventTypes.MessageTextDelta,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-dedupe", ["delta"] = "one" }, KautukaJsonContext.Default.JsonObject));
        NaradPersistedEvent completed = CreateEvent(
            "evt-dedupe",
            NaradEventTypes.MessageTextCompleted,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m-dedupe" }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectBoundAsync(delta, "reg-1", "chat-1", "thread-1", cancellationToken: CancellationToken.None)
            .ConfigureAwait(true);
        await projector.ProjectBoundAsync(completed, "reg-1", "chat-1", "thread-1", cancellationToken: CancellationToken.None)
            .ConfigureAwait(true);
        await projector.OnPublishedAsync(completed, CancellationToken.None).ConfigureAwait(true);

        Assert.Single(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotOutboxTextDeltasInMirrorMode()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Mirror };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "thread-1",
                "permanent_topic",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent delta = CreateEvent(
            "evt-delta-mirror",
            NaradEventTypes.MessageTextDelta,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m1", ["delta"] = "word " }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(delta, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public async Task ProjectAsyncDoesNotOutboxLooseTextDeltasInHitlMode()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { HitlTetherMode = ChannelHitlTetherMode.Hitl };
        var mapStore = new FakeConversationMapStore(
            new ChannelConversationMapEntry(
                "map-1",
                "telegram",
                "reg-1",
                "agent",
                "agent-1",
                "chat-1",
                "chat-1",
                "dm",
                "session-1",
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                null));
        var projector = CreateProjector(outbox, attachments, mapStore);
        NaradPersistedEvent delta = CreateEvent(
            "evt-delta-hitl",
            NaradEventTypes.MessageTextDelta,
            "session-1",
            "run-1",
            JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = "m1", ["delta"] = "word " }, KautukaJsonContext.Default.JsonObject));

        await projector.ProjectAsync(delta, CancellationToken.None).ConfigureAwait(true);
        Assert.Empty(outbox.Entries);
    }

    [Fact]
    public void ValidateEventJsonRejectsMalformedAuthority()
    {
        var malformed = new NaradPersistedEvent(
            "evt-bad",
            NaradProtocolVersions.V1,
            NaradEventTypes.RunFinished,
            "session-1",
            1,
            NaradRecordClasses.Authority,
            "{not-json",
            DateTimeOffset.UtcNow.ToString("O"),
            "run-1",
            "run-1",
            1);

        Assert.ThrowsAny<Exception>(() => NaradStreamObservation.ValidateEventJson(malformed));
    }

    private sealed class FakeSessionStore : IGatewaySessionStore
    {
        private readonly Dictionary<string, GatewaySession> byId;

        public FakeSessionStore(params GatewaySession[] sessions)
        {
            byId = sessions.Length == 0
                ? new Dictionary<string, GatewaySession>(StringComparer.Ordinal)
                {
                    ["session-1"] = new GatewaySession(
                        "session-1",
                        "agent",
                        "agent-1",
                        "user-1",
                        "title",
                        DateTimeOffset.UtcNow.ToString("O"),
                        DateTimeOffset.UtcNow.ToString("O"),
                        [],
                        false,
                        null),
                }
                : sessions.ToDictionary(static session => session.Id, StringComparer.Ordinal);
        }

        public bool TryGetSession(string sessionId, out GatewaySession? session)
            => byId.TryGetValue(sessionId, out session);

        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => throw new NotSupportedException();
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => throw new NotSupportedException();
        public GatewaySession? ArchiveSession(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => throw new NotSupportedException();
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => throw new NotSupportedException();
        public bool DeleteSession(string sessionId) => throw new NotSupportedException();
        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => throw new NotSupportedException();
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null) => throw new NotSupportedException();
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteMemoryFact(string factId) => throw new NotSupportedException();
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => throw new NotSupportedException();
        public IReadOnlyList<GatewayRun> ListRuns() => throw new NotSupportedException();
        public bool TryGetRun(string runId, out GatewayRun? run) => throw new NotSupportedException();
        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null) => throw new NotSupportedException();
        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => throw new NotSupportedException();
    }

    private sealed class FakeChannelAttachmentRegistry : IChannelAttachmentRegistry
    {
        public bool HasActiveRegistration { get; set; } = true;
        public string DefaultAgentId { get; set; } = "agent-1";
        public GatewayChannelRegistration[]? Registrations { get; set; }
        public string[]? AgentIds { get; set; }
        public JsonObject Topics { get; set; } = [];
        public JsonObject RetiredTopics { get; set; } = [];
        public ChannelHitlTetherMode HitlTetherMode { get; set; } = ChannelHitlTetherMode.Hitl;

        public Task RefreshAttachmentAsync(string attachmentId, string ownerUserId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshUserAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshUsersAsync(IEnumerable<GatewayUser> users, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool TryGetRegistration(string attachmentId, out GatewayChannelRegistration? registration)
        {
            if (Registrations is not null)
            {
                registration = Registrations.FirstOrDefault(item => string.Equals(item.Id, attachmentId, StringComparison.Ordinal));
                return registration is not null;
            }

            registration = HasActiveRegistration
                ? new GatewayChannelRegistration(
                    attachmentId,
                    "telegram",
                    "user-1",
                    "bot-1",
                    true,
                    new JsonObject
                    {
                        ["channel_device_id"] = "chat-1",
                        ["target_threads"] = Topics.DeepClone(),
                        ["retired_target_threads"] = RetiredTopics.DeepClone(),
                        ["hitl_tether_mode"] = HitlTetherMode.ToStorageValue(),
                    },
                    DateTimeOffset.UtcNow.ToString("O"),
                    DateTimeOffset.UtcNow.ToString("O"))
                : null;
            return registration is not null;
        }

        public GatewayChannelRegistration[] GetActiveChatRegistrations(string userId)
            => Registrations is not null
                ? [.. Registrations.Where(item => item.OwnerUserId == userId && item.IsActive)]
                : HasActiveRegistration ? [CreateChatRegistration("reg-1", "telegram", "chat-1")] : [];

        public bool TryResolveIdentity(string attachmentId, string externalUserId, string chatId, out GatewayChannelIdentityLink? identity)
        {
            identity = null;
            return false;
        }

        public bool TryResolveActiveRegistration(string userId, out string? registrationId, out string? externalUserId)
        {
            if (!HasActiveRegistration)
            {
                registrationId = null;
                externalUserId = null;
                return false;
            }

            registrationId = "reg-1";
            externalUserId = "chat-1";
            return true;
        }

        public GatewayChannelTargetBinding[] GetBindings(string attachmentId)
            => HasActiveRegistration
                ? [.. (AgentIds ?? [DefaultAgentId]).Select(id => new GatewayChannelTargetBinding($"bind-{id}", attachmentId, "agent", id, id == DefaultAgentId, DateTimeOffset.UtcNow.ToString("O")))]
                : [];

        public GatewayChannelTargetBinding? GetDefaultBinding(string attachmentId)
            => GetBindings(attachmentId).FirstOrDefault(static item => item.IsDefault);

        public GatewayUserListItem BuildUserPayload(GatewayUser user) => throw new NotSupportedException();
        public Task<GatewayUserChannelsResponse> BuildUserChannelCatalogAsync(GatewayUser user, GatewayChannelSetupPayload? setup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ChannelSettingsUpdateResult?> UpdateUserChannelSettingsAsync(GatewayUser user, string providerKey, JsonElement body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetActiveTargetForUserAsync(string attachmentId, string externalUserId, string targetType, string targetId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> BindExternalIdentityAsync(string attachmentId, string ownerUserId, string externalUserId, string conversationId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string?> GetSlackPairingFailureReasonAsync(string ownerUserId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public ChannelHitlTetherMode GetChannelHitlTetherMode(string userId, string? sessionId) => HitlTetherMode;
    }

    private sealed class FakeConversationMapStore(ChannelConversationMapEntry? bySession) : IChannelConversationMapStore
    {
        private ChannelConversationMapEntry? bySessionEntry = bySession;
        private readonly List<ChannelConversationMapEntry> entries = bySession is null ? [] : [bySession];

        public ChannelConversationMapEntry GetOrCreatePermanentTopic(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerTopicId, string? sessionId)
        {
            ChannelConversationMapEntry entry = TryResolve(channelType, registrationId, providerConversationId, providerTopicId)
                ?? CreateTransientThread(channelType, registrationId, targetType, targetId, providerConversationId, providerTopicId, sessionId, DateTimeOffset.UtcNow);
            var permanent = entry with { ConversationKind = "permanent_topic", ExpiresAt = null };
            entries[entries.FindIndex(item => item.Id == entry.Id)] = permanent;
            return permanent;
        }

        public ChannelConversationMapEntry CreateTransientThread(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerThreadId, string? sessionId, DateTimeOffset expiresAtUtc)
        {
            var entry = new ChannelConversationMapEntry(
                Guid.NewGuid().ToString("N"),
                channelType,
                registrationId,
                targetType,
                targetId,
                providerConversationId,
                providerThreadId,
                "transient_thread",
                sessionId,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                expiresAtUtc.ToString("O"));
            entries.Add(entry);
            bySessionEntry = entry;
            return entry;
        }

        public ChannelConversationMapEntry? TryResolve(string channelType, string registrationId, string providerConversationId, string providerThreadId)
            => entries.LastOrDefault(entry =>
                string.Equals(entry.ChannelType, channelType, StringComparison.Ordinal)
                && string.Equals(entry.RegistrationId, registrationId, StringComparison.Ordinal)
                && string.Equals(entry.ProviderConversationId, providerConversationId, StringComparison.Ordinal)
                && string.Equals(entry.ProviderThreadId, providerThreadId, StringComparison.Ordinal));

        public ChannelConversationMapEntry? TryResolveBySession(string sessionId, string channelType)
            => entries.LastOrDefault(entry =>
                string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(entry.ChannelType, channelType, StringComparison.Ordinal))
                ?? (bySessionEntry is not null
                    && string.Equals(bySessionEntry.SessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(bySessionEntry.ChannelType, channelType, StringComparison.Ordinal)
                        ? bySessionEntry
                        : null);

        public ChannelConversationMapEntry? TryResolveLatestByRegistration(string channelType, string registrationId)
            => entries.LastOrDefault(entry =>
                string.Equals(entry.ChannelType, channelType, StringComparison.Ordinal)
                && string.Equals(entry.RegistrationId, registrationId, StringComparison.Ordinal));

        public ChannelConversationMapEntry? AttachSession(string entryId, string sessionId, DateTimeOffset? expiresAtUtc = null)
        {
            int index = entries.FindIndex(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal));
            if (index < 0)
            {
                return null;
            }

            ChannelConversationMapEntry updated = entries[index] with
            {
                SessionId = sessionId,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                ExpiresAt = expiresAtUtc?.ToString("O") ?? entries[index].ExpiresAt,
            };
            entries[index] = updated;
            bySessionEntry = updated;
            return updated;
        }

        public int CleanupExpiredTransientThreads(DateTimeOffset nowUtc) => 0;
    }

    private sealed class CapturingChannelOutboxStore : IChannelOutboxStore
    {
        public List<ChannelOutboxEntry> Entries { get; } = [];

        public ChannelOutboxEntry Enqueue(string channelType, string registrationId, string actionType, string payloadJson, DateTimeOffset? availableAtUtc = null, string? runId = null, string? traceParent = null, string? traceState = null, string? ownerUserId = null)
        {
            ChannelOutboxEntry entry = new(
                Id: $"out-{Entries.Count + 1}",
                ChannelType: channelType,
                RegistrationId: registrationId,
                ActionType: actionType,
                PayloadJson: payloadJson,
                Status: "pending",
                AttemptCount: 0,
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                AvailableAt: availableAtUtc?.ToString("O"),
                LeaseId: null,
                LeaseOwner: null,
                LeasedUntil: null,
                LastError: null,
                RunId: runId,
                TraceParent: traceParent,
                TraceState: traceState,
                OwnerUserId: ownerUserId);
            Entries.Add(entry);
            return entry;
        }

        public IReadOnlyList<ChannelOutboxEntry> LeasePending(string leaseOwner, int maxItems, TimeSpan leaseDuration, DateTimeOffset nowUtc) => throw new NotSupportedException();
        public void Acknowledge(string actionId, string leaseOwner, string leaseId, bool success, string? error, DateTimeOffset nowUtc, DateTimeOffset? retryAtUtc = null, int maxAttempts = 10) => throw new NotSupportedException();
    }
}
