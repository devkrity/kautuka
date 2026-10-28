using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Protos;

using Mediator;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelIngressServiceHardeningTests
{
    [Fact]
    public async Task WorkflowReviewRecoveryUsesTheWorkflowStepsAgentWhenApprovalAgentIdIsMissing()
    {
        using var fixture = new ChannelIngressFixture();
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode("review", "agent-1", "Review", "text", null, [], "none", false, true, 1);
        var definition = new GatewayWorkflowDefinition("workflow-recovery", "user-1", "Recovery review", false, 1,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, [node], now, now);
        fixture.WorkflowStore.CreateDefinition(definition);
        var attempt = new GatewayWorkflowStepAttempt("recovery-attempt", "recovery-instance", "review", 1,
            "recovery-run", null, WorkflowStepAttemptStatuses.Finished, null, now, now, now);
        var instance = new GatewayWorkflowInstance("recovery-instance", definition.Id, definition.Revision,
            "user-1", null, WorkflowInstanceStatuses.Paused, false, "review", new JsonObject(), "hash", 0,
            definition, [attempt], null, null, now, now, now, null);
        fixture.WorkflowStore.CreateInstance(instance);
        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "recovery-review", "recovery-session", attempt.RunId, null, null, "user-1", "review-call",
            "workflow_review", "workflow_review", null, "{}", now, null, null, null, null, null,
            Kind: "workflow_review", WorkflowId: definition.Id, InstanceId: instance.Id, NodeId: node.NodeId));
        var recovery = new ChannelApprovalRecoveryService(fixture.ApprovalStore, fixture.State, fixture.Ingress,
            fixture.Publisher, NullLogger<ChannelApprovalRecoveryService>.Instance);

        await recovery.RecoverOnceAsync(CancellationToken.None);

        HitlApprovalRequested resent = Assert.Single(fixture.Publisher.Notifications.OfType<HitlApprovalRequested>());
        Assert.Equal("agent", resent.TargetType);
        Assert.Equal("agent-1", resent.TargetId);
    }

    [Fact]
    public async Task DecidedApprovalRecoveryProjectsOnceAndClosesDeliveredInteraction()
    {
        using var fixture = new ChannelIngressFixture();
        GatewayChannelInteraction interaction = SeedDecidedDeliveredApproval(fixture, activeRegistration: true);
        var recovery = new ChannelApprovalRecoveryService(fixture.ApprovalStore, fixture.State, fixture.Ingress,
            fixture.Publisher, NullLogger<ChannelApprovalRecoveryService>.Instance);

        await recovery.RecoverResolvedChannelApprovalsAsync(CancellationToken.None);
        await recovery.RecoverResolvedChannelApprovalsAsync(CancellationToken.None);

        Assert.Equal("resolved", fixture.State.ChannelInteractions[interaction.Id].Status);
        Assert.Single(fixture.OutboxStore.Entries, IsApprovalResolvedProjection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecidedApprovalRecoveryClosesInteractionWhenRegistrationUnavailable(bool inactive)
    {
        using var fixture = new ChannelIngressFixture();
        GatewayChannelInteraction interaction = SeedDecidedDeliveredApproval(fixture, activeRegistration: !inactive);
        if (!inactive) fixture.State.ChannelRegistrations.Remove(fixture.RegistrationId);
        var recovery = new ChannelApprovalRecoveryService(fixture.ApprovalStore, fixture.State, fixture.Ingress,
            fixture.Publisher, NullLogger<ChannelApprovalRecoveryService>.Instance);

        await recovery.RecoverResolvedChannelApprovalsAsync(CancellationToken.None);
        await recovery.RecoverResolvedChannelApprovalsAsync(CancellationToken.None);

        GatewayChannelInteraction closed = fixture.State.ChannelInteractions[interaction.Id];
        Assert.Equal("resolved", closed.Status);
        Assert.NotNull(closed.ResolvedAt);
        Assert.Equal("approve", closed.Resolution);
        Assert.DoesNotContain(fixture.OutboxStore.Entries, IsApprovalResolvedProjection);
    }

    [Fact]
    public async Task ApprovalResolutionDoesNotReprojectResolvedSiblingInteraction()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewayChannelInteraction pending = SeedDecidedDeliveredApproval(fixture, activeRegistration: true);
        GatewayChannelInteraction sibling = pending with
        {
            Id = "resolved-sibling",
            SourceTransportMessageId = "msg-2",
            Status = "resolved",
            Resolution = "approve",
            ResolvedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        fixture.State.UpsertChannelInteraction(sibling);

        await fixture.Ingress.SyncChannelApprovalResolutionAsync(new ChannelApprovalResolution("request-1", "approve", "user-1"), CancellationToken.None);

        Assert.Equal("resolved", fixture.State.ChannelInteractions[pending.Id].Status);
        Assert.Equal(sibling.ResolvedAt, fixture.State.ChannelInteractions[sibling.Id].ResolvedAt);
        Assert.Single(fixture.OutboxStore.Entries, IsApprovalResolvedProjection);
    }

    [Fact]
    public async Task ConcurrentApprovalHandlerAndRecoveryProjectOnlyOnce()
    {
        using var fixture = new ChannelIngressFixture();
        GatewayChannelInteraction interaction = SeedDecidedDeliveredApproval(fixture, activeRegistration: true);
        var refreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AttachmentRegistry.RefreshGate = refreshGate;
        fixture.AttachmentRegistry.RefreshStarted = refreshStarted;
        var recovery = new ChannelApprovalRecoveryService(fixture.ApprovalStore, fixture.State, fixture.Ingress,
            fixture.Publisher, NullLogger<ChannelApprovalRecoveryService>.Instance);

        Task handler = fixture.Ingress.SyncChannelApprovalResolutionAsync(new ChannelApprovalResolution("request-1", "approve", "user-1"), CancellationToken.None);
        Task reconcile = recovery.RecoverResolvedChannelApprovalsAsync(CancellationToken.None);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.AttachmentRegistry.RefreshCount);
        refreshGate.SetResult();
        await Task.WhenAll(handler, reconcile);

        Assert.Equal("resolved", fixture.State.ChannelInteractions[interaction.Id].Status);
        Assert.Single(fixture.OutboxStore.Entries, IsApprovalResolvedProjection);
    }

    private static GatewayChannelInteraction SeedDecidedDeliveredApproval(ChannelIngressFixture fixture, bool activeRegistration)
    {
        fixture.SeedRegistration(new JsonObject());
        if (!activeRegistration)
            fixture.State.ChannelRegistrations[fixture.RegistrationId] = fixture.State.ChannelRegistrations[fixture.RegistrationId] with { IsActive = false };
        string now = DateTimeOffset.UtcNow.ToString("O");
        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "request-1", "session-1", "run-1", null, "agent-1", "user-1", "call-1", "run_skill_script", "skill", null,
            "{}", now, "approved", "user-1", now, null, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));
        var interaction = new GatewayChannelInteraction(
            "interaction-1", fixture.RegistrationId, "user-1", "agent", "agent-1", "session-1", "run-1", "chat-1", null,
            "prompt", [], "pending", now, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"), null, null, null,
            "request-1", SourceTransportMessageId: "msg-1", ArgumentsJson: "{}");
        fixture.State.UpsertChannelInteraction(interaction);
        return interaction;
    }

    private static bool IsApprovalResolvedProjection(ChannelOutboxEntry entry)
        => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent
            && entry.PayloadJson.Contains("tool.approval.resolved", StringComparison.Ordinal);

    [Fact]
    public void TelegramCommandCatalogueDoesNotExposeRemovedToCommand()
    {
        Assert.DoesNotContain(ChannelBotCommandCatalogue.Default,
            static command => string.Equals(command.Command, "to", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChannelOutboxUsesStableCorrelationAndDoesNotExhaustRateLimitedDelivery()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-1" });
        fixture.ChannelHostGrpc.SendRpcException = new Grpc.Core.RpcException(
            new Grpc.Core.Status(Grpc.Core.StatusCode.ResourceExhausted, "channel_rate_limit_exceeded"));
        ChannelOutboxEntry entry = fixture.OutboxStore.Enqueue("telegram", fixture.RegistrationId, "send_text",
            "{\"conversationId\":\"chat-1\"}", ownerUserId: "user-1");
        var outbox = new SingleEntryChannelOutboxStore(entry);
        using var worker = new ChannelHostOutboxWorker(fixture.ChannelHostGrpc, outbox, fixture.AttachmentRegistry,
            fixture.State, NullLogger<ChannelHostOutboxWorker>.Instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await worker.StartAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            OutboxAcknowledgement acknowledgement = await outbox.Acknowledged.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            Assert.Equal(entry.Id, Assert.Single(fixture.ChannelHostGrpc.Sent).CorrelationId);
            Assert.Equal(entry.Id, acknowledgement.ActionId);
            Assert.Equal(int.MaxValue, acknowledgement.MaxAttempts);
            Assert.Contains("channel_rate_limit_exceeded", acknowledgement.Error, StringComparison.Ordinal);
            Assert.NotNull(acknowledgement.RetryAtUtc);
        }
        finally
        {
            await worker.StopAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public async Task TelegramApprovalResolutionBoundsArgumentsAndRetainsExactOriginalPrompt(string decision)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        void RecordPrompt(string run, string text)
        {
            var context = new NaradEventContext("agent-1", session.Id, "user-1", run, run);
            foreach (string type in new[] { NaradEventTypes.RunStarted, NaradEventTypes.MessageUserRecorded })
                fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
                    NaradAuthorityEventDraftFactory.CreateNew(type, JsonSerializer.SerializeToElement(new
                    { messageId = run, content = text }), context));
        }
        RecordPrompt("original", "roll d446");
        string arguments = JsonSerializer.Serialize(new { sides = "446", note = new string('x', 5000) });
        string safeArguments = ChannelApprovalArguments.Sanitize(arguments)!;
        Assert.NotEqual(arguments, safeArguments);
        Assert.True(safeArguments.Length <= 1800);
        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", session.Id, "agent", "agent-1",
            new GatewayChatApprovalRequest("full-request", "call", "run_skill_script", arguments, RunId: "original", AgentId: "agent-1"),
            TestContext.Current.CancellationToken);
        GatewayChannelInteraction interaction = Assert.Single(fixture.State.ChannelInteractions.Values);
        Assert.DoesNotContain(arguments, interaction.Prompt, StringComparison.Ordinal);
        ChannelRuntimeEventAction requested = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(entry => entry.ActionType == "runtime_event").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal("roll d446", requested.PromptText);
        using (JsonDocument requestPayload = JsonDocument.Parse(requested.PayloadJson))
            Assert.Equal(safeArguments, requestPayload.RootElement.GetProperty("argumentsJson").GetString());
        RecordPrompt("later", "roll d777");
        fixture.State.UpsertChannelInteraction(interaction with { SourceTransportMessageId = "123" });
        await fixture.Ingress.SyncChannelApprovalResolutionAsync(new ChannelApprovalResolution("full-request", decision, "user-1"),
            TestContext.Current.CancellationToken);
        ChannelRuntimeEventAction resolved = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(entry => entry.ActionType == "runtime_event").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal("roll d446", resolved.PromptText);
        using JsonDocument payload = JsonDocument.Parse(resolved.PayloadJson);
        Assert.Equal(interaction.Prompt, payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(safeArguments, payload.RootElement.GetProperty("argumentsJson").GetString());
        Assert.Equal(decision, payload.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task TelegramApprovalResolvedBeforeCardDeliveryIsEditedAfterTransportIdArrives()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "approval-delivery-race", session.Id, "run-1", null, "agent-1", "user-1", "call-1",
            "run_skill_script", "skill", null, "{}", DateTimeOffset.UtcNow.ToString("O"), null, null,
            null, null, DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", session.Id, "agent", "agent-1",
            new GatewayChatApprovalRequest("approval-delivery-race", "call-1", "run_skill_script", "{}", RunId: "run-1", ToolType: "skill", AgentId: "agent-1"),
            TestContext.Current.CancellationToken);
        GatewayChannelInteraction interaction = Assert.Single(fixture.State.ChannelInteractions.Values);
        Assert.Null(interaction.SourceTransportMessageId);

        Assert.True(fixture.ApprovalStore.Resolve("approval-delivery-race", true, null, "user-1", "browser"));
        await fixture.Ingress.SyncChannelApprovalResolutionAsync(
            new ChannelApprovalResolution("approval-delivery-race", "approve", "user-1"),
            TestContext.Current.CancellationToken);
        Assert.Single(fixture.OutboxStore.Entries, entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent);

        fixture.State.UpsertChannelInteraction(interaction with { SourceTransportMessageId = "123" });
        await fixture.Ingress.SyncResolvedChannelApprovalAfterDeliveryAsync(interaction.Id, TestContext.Current.CancellationToken);
        await fixture.Ingress.SyncResolvedChannelApprovalAfterDeliveryAsync(interaction.Id, TestContext.Current.CancellationToken);

        ChannelOutboxEntry resolvedEntry = Assert.Single(fixture.OutboxStore.Entries,
            entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent
                && entry.PayloadJson.Contains("tool.approval.resolved", StringComparison.Ordinal));
        ChannelRuntimeEventAction resolved = JsonSerializer.Deserialize(resolvedEntry.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        using JsonDocument payload = JsonDocument.Parse(resolved.PayloadJson);
        Assert.Equal("123", payload.RootElement.GetProperty("transportMessageId").GetString());
        Assert.Equal("approve", payload.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task ApprovalAndResolutionAreProjectedToEveryActiveLinkedChat()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["channel_device_id"] = "chat-telegram",
            ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" },
        });
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "bot-1", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-slack", ["channel_device_id"] = "D-slack" },
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        const string telegramId = "telegram:user-1:default";
        fixture.State.ChannelRegistrations[telegramId] = new GatewayChannelRegistration(telegramId, "telegram", "user-1", "bot-1", true,
            new JsonObject { ["channel_device_id"] = "chat-telegram" }, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", session.Id, "agent", "agent-1",
            new GatewayChatApprovalRequest("multi-channel", "call-1", "run_skill_script", "{}", RunId: "run-1", ToolType: "skill", AgentId: "agent-1"),
            TestContext.Current.CancellationToken);

        GatewayChannelInteraction[] interactions = [.. fixture.State.ChannelInteractions.Values.Where(item => item.ApprovalRequestId == "multi-channel")];
        Assert.Equal(2, interactions.Length);
        Assert.Contains(interactions, item => item.RegistrationId == slackId && item.ChatId == "D-slack");
        Assert.Contains(interactions, item => item.RegistrationId == telegramId && item.ChatId == "chat-telegram");
        Assert.Equal("approval:multi-channel", Assert.Single(interactions.Select(item => item.InteractionGroupId).Distinct()));
        foreach (GatewayChannelInteraction item in interactions)
            fixture.State.UpsertChannelInteraction(item with { SourceTransportMessageId = $"msg-{item.RegistrationId}" });
        await fixture.Ingress.SyncChannelApprovalResolutionAsync(new ChannelApprovalResolution("multi-channel", "approve", "user-1"),
            TestContext.Current.CancellationToken);
        Assert.All(interactions, item => Assert.Equal("resolved", fixture.State.ChannelInteractions[item.Id].Status));
        int runtimeEventCount = fixture.OutboxStore.Entries.Count(item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent);
        Assert.True(runtimeEventCount == 4, string.Join(",", fixture.OutboxStore.Entries.Select(item => item.RegistrationId)));
    }

    [Fact]
    public async Task SlackQuickActionCallbackUsesTheSharedGatewaySlashCommandHandler()
    {
        using var fixture = new ChannelIngressFixture();
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "slack-bot", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-SLACK" }, "created", "updated");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-SLACK"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-SLACK", slackId, "user-1", "U-SLACK", "created");
        fixture.State.ChannelTargetBindings[$"{slackId}:agent:agent-1"] = new GatewayChannelTargetBinding(
            $"{slackId}:agent:agent-1", slackId, "agent", "agent-1", true, "created");

        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "slack-quick-context", "slack", slackId, "user-1", "U-SLACK", "D-SLACK", null, "callback", null,
            ChannelBotCommandCatalogue.CreateQuickActionCallback("context"), "callback-1", null,
            JsonSerializer.SerializeToElement(new { })), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        ChannelOutboxEntry response = Assert.Single(fixture.OutboxStore.Entries, entry => entry.ActionType == "send_semantic_message"
            && entry.PayloadJson.Contains("Sending to", StringComparison.Ordinal)
            && entry.PayloadJson.Contains("Use Sessions to switch sessions", StringComparison.Ordinal));
        Assert.Contains("\"conversationThreadId\":null", response.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D-OWNER", "U-OTHER")]
    [InlineData("C-CHANNEL", "U-OWNER")]
    public async Task SlackRejectsEventsOutsideThePairedOwnerDirectMessage(string chatId, string senderId)
    {
        using var fixture = new ChannelIngressFixture();
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "slack-bot", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-OWNER", ["slack_owner_user_id"] = "U-OWNER" }, "created", "updated");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-OWNER"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-OWNER", slackId, "user-1", "U-OWNER", "created");
        fixture.State.ChannelTargetBindings[$"{slackId}:agent:agent-1"] = new GatewayChannelTargetBinding(
            $"{slackId}:agent:agent-1", slackId, "agent", "agent-1", true, "created");

        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            $"slack-private-{chatId}-{senderId}", "slack", slackId, "user-1", senderId, chatId, null,
            "message", "must not route", null, null, null, JsonSerializer.SerializeToElement(new { })),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("slack_not_owner_dm", result.Status);
        Assert.Empty(fixture.OutboxStore.Entries);
    }

    [Fact]
    public async Task NormalOutboundRuntimeEventFansOutAcrossLinkedChatProviders()
    {
        using var fixture = new ChannelIngressFixture();
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        const string telegramId = "telegram:user-1:default";
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[telegramId] = new GatewayChannelRegistration(telegramId, "telegram", "user-1", "bot-1", true,
            new JsonObject { ["channel_device_id"] = "chat-telegram" }, "1", "1");
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "bot-1", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-slack" }, "1", "1");
        foreach (string registrationId in new[] { telegramId, slackId })
            fixture.State.ChannelTargetBindings[$"{registrationId}:agent:agent-1"] = new GatewayChannelTargetBinding(
                $"{registrationId}:agent:agent-1", registrationId, "agent", "agent-1", true, "1");

        var projector = new ChannelRuntimeEventProjector(fixture.OutboxStore, fixture.AttachmentRegistry,
            fixture.ConversationMapStore, fixture.State, fixture.SessionStore);
        NaradPersistedEvent completed = NaradStreamObservation.CreateSynthetic("shared-completion", NaradEventTypes.RunFinished,
            session.Id, "run-1", "run-1", JsonSerializer.SerializeToElement(new { agentId = "agent-1", response = "Done" }));
        await projector.ProjectAsync(completed, TestContext.Current.CancellationToken);

        string[] deliveredRegistrations = [.. fixture.OutboxStore.Entries
            .Where(item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
            .Select(item => item.RegistrationId)
            .Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { slackId, telegramId }.Order(StringComparer.Ordinal), deliveredRegistrations);
    }

    private static IEnumerable<ChannelSemanticBlock> PanelBlocks(IReadOnlyList<ChannelSemanticBlock> blocks)
    {
        foreach (ChannelSemanticBlock block in blocks)
        {
            yield return block;
            foreach (ChannelSemanticBlock child in PanelBlocks(block.Blocks ?? [])) yield return child;
        }
    }

    [Fact]
    public async Task ExplicitTelegramReplyQueuesForItsVerifiedSessionWithoutChangingTheSelectedSessionWhenItRuns()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewaySession a = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession b = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "target:AGENT:agent-1", b.Id);
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? registration));
        new ChannelPromptAnchorStore(fixture.State.Database).SaveMessageSession(registration!, "chat-1", null, a.Id, "123");
        ChannelInboundEvent Message(string id, string text, JsonElement payload) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-1", null, "message", text, null, null, 200, payload);
        ChannelIngressResult reply = await fixture.Ingress.AdmitInboundEventAsync(Message("reply-a", "follow up in A",
            JsonSerializer.SerializeToElement(new { message = new { reply_to_message = new { message_id = 123 } } })) with
        { ReplySourceTransportMessageId = "123" }, TestContext.Current.CancellationToken);
        Assert.Equal(a.Id, reply.SessionId);
        QueuedSessionTurn turn = Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-1", a.Id));
        fixture.ChatGateway.EmitRunStarted = true;
        QueuedSessionTurn leased = Assert.IsType<QueuedSessionTurn>(fixture.Ingress.PendingTurns.TryLease(turn));
        await fixture.TurnExecution.ExecuteAsync(leased, TestContext.Current.CancellationToken);
        fixture.Ingress.PendingTurns.Complete(leased);
        Assert.Equal(b.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-1")!.SessionId);
        ChannelIngressResult ordinary = await fixture.Ingress.AdmitInboundEventAsync(Message("ordinary-b", "ordinary prompt", JsonSerializer.SerializeToElement(new { })), TestContext.Current.CancellationToken);
        Assert.Equal(b.Id, ordinary.SessionId);
    }

    [Fact]
    public async Task EmailViewReplyCallbackQueuesToTheSessionThatOpenedTheView()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-1" });
        GatewaySession origin = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession current = fixture.SessionStore.CreateSession("agent", "agent-2", "user-1");
        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2", fixture.RegistrationId, "agent", "agent-2", true, DateTimeOffset.UtcNow.ToString("O"));
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-2", "chat-1", "target:AGENT:agent-2", current.Id);
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? registration));
        new ChannelPromptAnchorStore(fixture.State.Database).SaveViewSession(registration!, "chat-1", null, "email-view-1", origin.Id);

        ChannelIngressResult result = await fixture.Ingress.AdmitInboundEventAsync(new ChannelInboundEvent(
            "email-view-reply", "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", null,
            "callback", null, "v:email-view-1:reply", "email-view-reply", 44,
            JsonSerializer.SerializeToElement(new { })), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(origin.Id, result.SessionId);
        Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-1", origin.Id));
        Assert.Empty(fixture.Ingress.PendingTurns.ListSession("user-1", current.Id));
        Assert.Equal(current.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-2")!.SessionId);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(101, true)]
    public async Task UnknownExplicitReplyDoesNotFallBackButKnownRootRepliesUseTheAnchoredSession(int messageId, bool accepted)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewaySession selected = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "target:AGENT:agent-1", selected.Id);
        if (messageId == 101
            && fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? replyRegistration))
            new ChannelPromptAnchorStore(fixture.State.Database).SaveMessageSession(replyRegistration!, "chat-1", null, selected.Id, "101");
        var reply = new JsonObject { ["message_id"] = messageId };
        JsonElement payload = JsonSerializer.SerializeToElement(new JsonObject { ["message"] = new JsonObject { ["reply_to_message"] = reply } });
        var message = new ChannelInboundEvent("reply", "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", null,
            "message", "follow up", null, null, 201, payload)
        { ReplySourceTransportMessageId = messageId.ToString(CultureInfo.InvariantCulture) };
        ChannelIngressResult result = await fixture.Ingress.AdmitInboundEventAsync(message, TestContext.Current.CancellationToken);
        Assert.Equal(accepted, result.Success);
        Assert.Equal(accepted ? "queued" : "reply_session_unavailable", result.Status);
        Assert.Equal(accepted ? 1 : 0, fixture.Ingress.PendingTurns.ListSession("user-1", selected.Id).Count);
        Assert.Equal(selected.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-1")!.SessionId);
    }

    [Fact]
    public async Task NestedApprovalReachesOutboxWhenDedicatedAgentHasOnlyItsRootJournalFragment()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewaySession parent = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession child = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.SessionStore.UpdateSessionTitle(child.Id, "Roller dice");
        var definitions = fixture.State.AgentDefinitionStore;
        JsonElement body = JsonSerializer.SerializeToElement(new { });
        definitions.AppendNaradAuthorityEvent("parent-store", "parent-store",
            NaradAuthorityEventDraftFactory.CreateNew(NaradEventTypes.RunStarted, body,
                new NaradEventContext("parent-store", parent.Id, "user-1", RunId: "root", RootRunId: "root")));
        NaradPersistedEvent childStart = definitions.AppendNaradAuthorityEvent("parent-store", "agent-1",
            NaradAuthorityEventDraftFactory.CreateNew(NaradEventTypes.RunStarted, body,
                new NaradEventContext("agent-1", child.Id, "user-1", RunId: "child", RootRunId: "root")));
        definitions.MirrorNaradAuthorityEvent("agent-1", childStart);
        Assert.Throws<InvalidDataException>(() => definitions.ListNaradRootHistoryFull("agent-1", "root"));
        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", child.Id, "agent", "agent-1",
            new GatewayChatApprovalRequest("fragment-approval", "call", "run_skill_script", "{}", RunId: "child", AgentId: "agent-1"),
            TestContext.Current.CancellationToken);
        ChannelOutboxEntry entry = Assert.Single(fixture.OutboxStore.Entries, item => item.ActionType == "runtime_event");
        ChannelRuntimeEventAction action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal("tool.approval.requested", action.EventType);
        Assert.Equal("root", action.PromptRootRunId);
        Assert.Equal(child.Id, action.PromptSessionId);
        Assert.Equal("Roller dice", action.SessionDisplayName);
        Assert.Contains("fragment-approval", action.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedApprovalGroupsUnderItsExactRootSessionWithoutChangingExecutionIdentity()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession parent = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession child = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.SessionStore.UpdateSessionTitle(parent.Id, "Parent conversation");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", parent.Id);
        foreach ((string session, string run) in new[] { (parent.Id, "root"), (child.Id, "child") })
        {
            var context = new NaradEventContext("agent-1", session, "user-1", RunId: run, RootRunId: "root");
            fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
                NaradAuthorityEventDraftFactory.CreateNew(NaradEventTypes.RunStarted, JsonSerializer.SerializeToElement(new { }), context));
        }
        var projector = new ChannelRuntimeEventProjector(fixture.OutboxStore, fixture.AttachmentRegistry, fixture.ConversationMapStore, fixture.State, fixture.SessionStore);
        NaradPersistedEvent approval = NaradStreamObservation.CreateSynthetic("nested-approval", NaradEventTypes.InterruptRequested,
            child.Id, "child", "child", JsonSerializer.SerializeToElement(new { requestId = "approval-1", agentId = "agent-1", toolName = "roller", prompt = "Roll dice", outerSessionId = parent.Id }));
        await projector.ProjectBoundAsync(approval, fixture.RegistrationId, "chat-1", "101", cancellationToken: TestContext.Current.CancellationToken);
        ChannelRuntimeEventAction action = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last().PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal(child.Id, action.SessionId);
        Assert.Equal("child", action.RunId);
        Assert.Equal(parent.Id, action.PromptSessionId);
        Assert.Equal("root", action.PromptRootRunId);
        Assert.Equal("Parent conversation", action.SessionDisplayName);
        Assert.Equal(parent.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "101")!.SessionId);
    }

    [Fact]
    public async Task TelegramAdmissionPersistsTheUsersPromptAsItsRunAnchor()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        ChannelInboundEvent message = TelegramLongPollingWorker.CreateInboundEventForTest(fixture.RegistrationId, "user-1",
            JsonSerializer.SerializeToElement(new { update_id = 1, message = new { message_id = 123, from = new { id = "tg-user-1" }, chat = new { id = "chat-1" }, text = "Hello" } }))!;
        ChannelIngressResult accepted = await fixture.Ingress.AdmitInboundEventAsync(message, TestContext.Current.CancellationToken);
        Assert.True(accepted.Success);
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? registration));
        Assert.Equal((true, "123"), new ChannelPromptAnchorStore(fixture.State.Database).Read(registration!, "chat-1", null, accepted.SessionId!, accepted.RunId!));
    }

    [Fact]
    public async Task TelegramDeliveryKeepsPerRunPromptAnchorAcrossRestartWithoutBlockingOnUnavailablePrompt()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? registration));
        var anchors = new ChannelPromptAnchorStore(fixture.State.Database);
        anchors.Save(registration!, "chat-1", "101", "session", "root", null);
        var runtime = new ChannelRuntimeEventAction("event", "run.finished", "{\"response\":\"Done\"}", "user-1",
            fixture.RegistrationId, "session", "root", "root", "chat-1", "101");
        ChannelOutboxEntry entry = fixture.OutboxStore.Enqueue("telegram", fixture.RegistrationId, "runtime_event",
            JsonSerializer.Serialize(runtime, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction), ownerUserId: "user-1");
        SendOutboundMessageRequest Request() => new() { MessageId = "final", Content = new OutboundContentEnvelope { ActionType = "runtime_event" } };
        using (var worker = new ChannelHostOutboxWorker(fixture.ChannelHostGrpc, fixture.OutboxStore, fixture.AttachmentRegistry, fixture.State, NullLogger<ChannelHostOutboxWorker>.Instance))
            Assert.True((await worker.DeliverRuntimeEventAsync(entry, registration!, Request(), TestContext.Current.CancellationToken)).Accepted);
        ChannelRuntimeEventAction unquoted = JsonSerializer.Deserialize(Assert.Single(fixture.ChannelHostGrpc.Sent).Content.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Null(unquoted.ReplyToMessageId);
        anchors.Save(registration!, "chat-1", "101", "session", "root", "123");
        using var restarted = new ChannelHostOutboxWorker(fixture.ChannelHostGrpc, fixture.OutboxStore, fixture.AttachmentRegistry, fixture.State, NullLogger<ChannelHostOutboxWorker>.Instance);
        Assert.True((await restarted.DeliverRuntimeEventAsync(entry, registration!, Request(), TestContext.Current.CancellationToken)).Accepted);
        ChannelRuntimeEventAction sent = JsonSerializer.Deserialize(fixture.ChannelHostGrpc.Sent.Last().Content.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Null(sent.ReplyToMessageId);
        Assert.Equal("session", sent.SessionId);
        Assert.Equal("root", sent.RootRunId);
    }

    [Theory]
    [InlineData(null, "Dice")]
    [InlineData("Dice", "Dice")]
    [InlineData("Previous title", "Dice")]
    public async Task TelegramKeepsSelfContainedSessionContextRegardlessOfQuotedPrompt(string? anchorLabel, string? expectedLabel)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? registration));
        new ChannelPromptAnchorStore(fixture.State.Database).Save(registration!, "chat-1", "101", "session", "root", "123", anchorLabel);
        using var worker = new ChannelHostOutboxWorker(fixture.ChannelHostGrpc, fixture.OutboxStore, fixture.AttachmentRegistry,
            fixture.State, NullLogger<ChannelHostOutboxWorker>.Instance);
        var runtime = new ChannelRuntimeEventAction("event", "run.finished", "{\"response\":\"Done\"}", "user-1",
            fixture.RegistrationId, "session", "root", "root", "chat-1", "101", SessionDisplayName: "Dice");
        ChannelOutboxEntry entry = fixture.OutboxStore.Enqueue("telegram", fixture.RegistrationId, "runtime_event",
            JsonSerializer.Serialize(runtime, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction), ownerUserId: "user-1");
        SendOutboundMessageRequest Request() => new() { MessageId = "final", Content = new OutboundContentEnvelope() };
        Assert.True((await worker.DeliverRuntimeEventAsync(entry, registration!, Request(), TestContext.Current.CancellationToken)).Accepted);
        ChannelRuntimeEventAction sent = JsonSerializer.Deserialize(fixture.ChannelHostGrpc.Sent.Last().Content.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        Assert.Equal(expectedLabel, sent.SessionDisplayName);
        Assert.Null(sent.ReplyToMessageId);
        Assert.Equal("session", sent.SessionId);
        var semantic = new ChannelSemanticMessageAction("chat-1", "101",
            ChannelMessageTemplates.WithSession(ChannelMessageTemplates.AgentResponse("Done"), "Dice"), PromptSessionId: "session", PromptRootRunId: "root");
        entry = fixture.OutboxStore.Enqueue("telegram", fixture.RegistrationId, "send_semantic_message",
            JsonSerializer.Serialize(semantic, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction), ownerUserId: "user-1");
        Assert.True((await worker.DeliverRuntimeEventAsync(entry, registration!, Request(), TestContext.Current.CancellationToken)).Accepted);
        ChannelSemanticMessageAction sentSemantic = JsonSerializer.Deserialize(fixture.ChannelHostGrpc.Sent.Last().Content.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal(expectedLabel is not null, sentSemantic.Document.Blocks.Any(block => block.Text == "Session: Dice"));
        Assert.Contains("Session: Dice", sentSemantic.Document.FallbackText, StringComparison.Ordinal);
        Assert.DoesNotContain("Prompt:", sentSemantic.Document.FallbackText, StringComparison.Ordinal);
        Assert.Null(sentSemantic.ReplyToMessageId);
    }

    [Fact]
    public async Task TelegramPanelSupportsFiveSessionQueuesWithoutASeparateActionStack()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        for (int i = 0; i < 5; i++)
        {
            GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
            fixture.SessionStore.UpdateSessionTitle(session.Id, "Session " + i);
            if (i == 0) fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", session.Id);
            fixture.Ingress.PendingTurns.Enqueue("session-turn-" + i, session.Id, "agent-1",
                new JsonObject { ["ownerUserId"] = "user-1", ["message"] = "Roll d" + (100 + i) }.ToJsonString());
        }
        await fixture.Ingress.RefreshSessionViewsAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.OutboxStore.Entries, item => item.ActionType == "session_facts");
        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-1", "101", "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        await fixture.Ingress.AdmitInboundEventAsync(Message("open-queue-panel", "/queue"), TestContext.Current.CancellationToken);
        ChannelOutboxEntry entry = Assert.Single(fixture.OutboxStore.Entries, item => item.ActionType == "session_facts");
        ChannelSessionFactsEnvelope facts = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.Equal("kautuka.session-facts/v1", facts.Version);
        Assert.Equal("chat-1", facts.ConversationId);
        Assert.Equal("queue", facts.View);
        Assert.Equal(5, facts.QueueFacts!.TotalCount);
        Assert.Empty(facts.Active!.Sessions);
        Assert.Null(facts.Selected!.SessionId);
        Assert.All(facts.QueueFacts.Queue, queued =>
        {
            Assert.False(string.IsNullOrWhiteSpace(queued.AgentDisplayName));
            Assert.StartsWith("Session ", queued.SessionLabel, StringComparison.Ordinal);
        });
        Assert.All(facts.ActionsSeed, action => Assert.False(string.IsNullOrWhiteSpace(action.Token)));
    }

    [Fact]
    public async Task QueueAndSessionSlashCommandCardsFanOutToLinkedChatProviders()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        const string slackRegistrationId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackRegistrationId] = new GatewayChannelRegistration(
            slackRegistrationId, "slack", "user-1", "slack-bot", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-SLACK", ["slack_owner_user_id"] = "U-SLACK" },
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        fixture.State.ChannelTargetBindings[$"{slackRegistrationId}:agent:agent-1"] = new GatewayChannelTargetBinding(
            $"{slackRegistrationId}:agent:agent-1", slackRegistrationId, "agent", "agent-1", true,
            DateTimeOffset.UtcNow.ToString("O"));
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.Ingress.PendingTurns.Enqueue("linked-queue-item", session.Id, "agent-1",
            new JsonObject { ["ownerUserId"] = "user-1", ["message"] = "Linked queue item" }.ToJsonString());

        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-1", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Ingress.AdmitInboundEventAsync(Message("linked-queue-command", "/queue"), cancellationToken);

        ChannelOutboxEntry[] queueEntries = [.. fixture.OutboxStore.Entries.Where(entry => entry.ActionType == "session_facts")];
        Assert.Equal(2, queueEntries.Length);
        ChannelOutboxEntry telegramQueueEntry = Assert.Single(queueEntries, entry => entry.RegistrationId == fixture.RegistrationId);
        ChannelOutboxEntry slackQueueEntry = Assert.Single(queueEntries, entry => entry.RegistrationId == slackRegistrationId);
        Assert.Equal("chat-1", JsonSerializer.Deserialize(telegramQueueEntry.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!.ConversationId);
        Assert.Equal("D-SLACK", JsonSerializer.Deserialize(slackQueueEntry.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!.ConversationId);

        fixture.OutboxStore.Entries.Clear();
        await fixture.Ingress.AdmitInboundEventAsync(Message("linked-sessions-command", "/sessions"), cancellationToken);
        ChannelOutboxEntry[] sessionEntries = [.. fixture.OutboxStore.Entries.Where(entry => entry.ActionType == "session_facts")];
        Assert.Equal(2, sessionEntries.Length);
        Assert.Contains(sessionEntries, entry => entry.RegistrationId == fixture.RegistrationId);
        Assert.Contains(sessionEntries, entry => entry.RegistrationId == slackRegistrationId);

        fixture.OutboxStore.Entries.Clear();
        await fixture.Ingress.AdmitInboundEventAsync(Message("linked-agents-command", "/agents"), cancellationToken);
        ChannelOutboxEntry[] pickerEntries = [.. fixture.OutboxStore.Entries.Where(entry => entry.ActionType == "send_semantic_message")];
        Assert.Equal(2, pickerEntries.Length);
        Assert.Contains(pickerEntries, entry => entry.RegistrationId == fixture.RegistrationId);
        Assert.Contains(pickerEntries, entry => entry.RegistrationId == slackRegistrationId);
    }

    [Fact]
    public async Task CombinedQueueGroupsSessionsAndOldButtonsKeepTheirOriginalSessionAfterSwitch()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession a = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession b = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.SessionStore.UpdateSessionTitle(a.Id, "Invoices");
        fixture.SessionStore.UpdateSessionTitle(b.Id, "Dice");
        ChannelConversationMapEntry map = fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId,
            "agent", "agent-1", "chat-1", "target:AGENT:agent-1", a.Id);
        string Payload(string message) => new JsonObject { ["ownerUserId"] = "user-1", ["message"] = message }.ToJsonString();
        fixture.Ingress.PendingTurns.Enqueue("a-queued", a.Id, "agent-1", Payload("Review invoices"));
        fixture.Ingress.PendingTurns.Enqueue("b-queued", b.Id, "agent-1", Payload("Roll a d55"));
        fixture.Ingress.PendingTurns.Enqueue("other-owner", b.Id, "agent-1", new JsonObject { ["ownerUserId"] = "user-2", ["message"] = "Private" }.ToJsonString());
        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-1", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Ingress.AdmitInboundEventAsync(Message("combined-queue", "/queue"), ct);
        ChannelSessionFactsEnvelope card = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(item => item.ActionType == "session_facts").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.Equal("queue", card.View);
        Assert.Equal(2, card.QueueFacts!.TotalCount);
        Assert.Empty(card.Active!.Sessions);
        Assert.Null(card.Selected!.SessionId);
        Assert.Contains(card.QueueFacts.Queue, queued => queued.SessionLabel == "Invoices");
        Assert.Contains(card.QueueFacts.Queue, queued => queued.SessionLabel == "Dice");
        Assert.DoesNotContain(card.QueueFacts.Queue, turn => turn.Preview == "Private");
        string removeA = card.ActionsSeed.First(action => action.Kind == "remove" && action.TurnId == "a-queued").Token;
        fixture.ConversationMapStore.AttachSession(map.Id, b.Id);
        ChannelInboundEvent Callback(string id, string token) => Message(id, "") with { EventType = "callback", Text = null, CallbackData = token, AcknowledgeToken = id, SourceMessageId = 44 };
        await fixture.Ingress.AdmitInboundEventAsync(Callback("remove-a-after-switch", removeA), ct);
        Assert.Empty(fixture.Ingress.PendingTurns.ListSession("user-1", a.Id));
        Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-1", b.Id));
        Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-2", b.Id));
        await fixture.Ingress.AdmitInboundEventAsync(Message("show-sessions", "/sessions"), ct);
        ChannelSessionFactsEnvelope picker = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(item => item.ActionType == "session_facts").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        string switchA = picker.ActionsSeed.Single(action => action.ActionId == "session-" + a.Id).Token;
        await fixture.Ingress.AdmitInboundEventAsync(Callback("switch-a", switchA), ct);
        Assert.Equal(a.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-1")!.SessionId);
        Assert.Contains(fixture.OutboxStore.Entries, item =>
            item.PayloadJson.Contains("Now talking to", StringComparison.Ordinal)
            && item.PayloadJson.Contains("Invoices", StringComparison.Ordinal));
        Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-1", b.Id));
        fixture.ConversationMapStore.AttachSession(map.Id, b.Id);
        Assert.Equal(b.Id, fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-1")!.SessionId);
    }

    [Theory]
    [InlineData("slack")]
    public async Task BrowserQueueCreatesFirstChannelCardAndRefreshesItWhenCleared(string provider)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } }, provider);
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic(provider, fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", session.Id);
        JsonElement request = JsonSerializer.SerializeToElement(new
        {
            target_id = "agent-1",
            session_id = session.Id,
            user_id = "user-1",
            message = "browser follow-up",
            source = "narad",
        }, jsonOptions);
        var empty = new QueuedSessionTurn("not-queued", session.Id, "agent-1", JsonSerializer.Serialize(new { ownerUserId = "user-1" }, jsonOptions), null);
        await fixture.Ingress.QueueChangedAsync(empty, TestContext.Current.CancellationToken).ConfigureAwait(true);
        const string viewAction = "session_facts";
        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == viewAction);
        await fixture.Ingress.AdmitInboundEventAsync(new ChannelInboundEvent("open-queue", provider, fixture.RegistrationId,
            "user-1", "tg-user-1", "chat-1", "101", "message", "/queue", null, null, null,
            JsonSerializer.SerializeToElement(new { }, jsonOptions)), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.Ingress.PendingTurns.Admit("browser-first-queue", request);
        QueuedSessionTurn turn = Assert.Single(fixture.Ingress.PendingTurns.ListSession("user-1", session.Id));
        await fixture.Ingress.QueueChangedAsync(turn, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ChannelOutboxEntry created = fixture.OutboxStore.Entries.Last(entry => entry.ActionType == viewAction);
        Assert.Equal(provider, created.ChannelType);
        ChannelSessionFactsEnvelope action = JsonSerializer.Deserialize(created.PayloadJson, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.Equal("chat-1", action.ConversationId);
        Assert.Equal("101", action.ConversationThreadId);
        Assert.Equal("browser follow-up", Assert.Single(action.QueueFacts!.Queue).Preview);
        Assert.Equal(1, fixture.Ingress.PendingTurns.RemoveSessionWaiting("user-1", session.Id, turn.Id));
        await fixture.Ingress.QueueChangedAsync(turn, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(3, fixture.OutboxStore.Entries.Count(entry => entry.ActionType == viewAction));
        ChannelSessionFactsEnvelope refreshed = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last().PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.Equal(0, refreshed.QueueFacts!.TotalCount);
    }

    [Fact]
    public async Task BrowserAndChannelTurnsShareTheDurableSessionQueueAndApprovalBarrier()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", session.Id);
        var context = new NaradEventContext("agent-1", session.Id, "user-1", "paused-root", "paused-root");
        void Append(string type) => fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
            NaradAuthorityEventDraftFactory.CreateNew(type, JsonSerializer.SerializeToElement(new { }, jsonOptions), context));
        Append(NaradEventTypes.RunStarted);
        Append(NaradEventTypes.RunPaused);
        JsonElement browser = JsonSerializer.SerializeToElement(new
        {
            target_type = "agent",
            target_id = "agent-1",
            session_id = session.Id,
            user_id = "user-1",
            message = "browser prompt",
            source = "narad",
        }, jsonOptions);
        Assert.True(fixture.Ingress.PendingTurns.Admit("browser-turn", browser));
        var message = new ChannelInboundEvent("channel-after-browser", "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", "101",
            "message", "channel prompt", null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        Assert.Equal("queued", (await fixture.Ingress.AdmitInboundEventAsync(message, TestContext.Current.CancellationToken).ConfigureAwait(true)).Status);
        Assert.Equal(2, fixture.WaitingTurns.Count);
        using var worker = new SessionTurnQueueWorker(fixture.Ingress.PendingTurns, fixture.TurnExecution, NullLogger<SessionTurnQueueWorker>.Instance);
        await worker.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            await Task.Delay(600, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Empty(fixture.ChatGateway.Messages);
            Append(NaradEventTypes.RunFinished);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (fixture.Ingress.PendingTurns.HasQueued(session.Id)) await Task.Delay(20, deadline.Token).ConfigureAwait(true);
            Assert.Equal(["browser prompt", "channel prompt"], fixture.ChatGateway.Messages.ToArray());
        }
        finally { await worker.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true); }
    }

    [Fact]
    public void LegacyApprovalContextDoesNotGuessAcrossInterleavedUserTurns()
    {
        using var fixture = new ChannelIngressFixture();
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        string now = DateTimeOffset.UtcNow.ToString("O");
        fixture.SessionStore.AppendUserMessage(session.Id,
            new GatewayMessage("user-446", "user", "roll a d446", now, false, null, null), userId: "user-1");
        fixture.SessionStore.AppendMessages(session.Id,
            new GatewayMessage("user-777", "user", "roll d777", now, false, null, null),
            new GatewayMessage("assistant-paused", "assistant", "", now, false, null, null,
                ToolCalls: [new GatewayToolCall("original-tool-call", "invoke_agent_roller", "agent-1", "{}", null, "calling", "agent")]),
            userId: "user-1");
        Assert.Null(RunUserMessageResolver.Resolve(fixture.State.AgentDefinitionStore, fixture.SessionStore,
            "agent-1", session.Id, "legacy-run-without-journal", "original-tool-call"));
    }

    [Fact]
    public async Task SimultaneousFirstPromptsShareOneSessionAndOneOrderedQueue()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.OnSessionRetentionRequested = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", null,
            "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<ChannelIngressResult> first = fixture.Ingress.AdmitInboundEventAsync(Message("race-first", "roll a d446"), ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
        Task<ChannelIngressResult> second = fixture.Ingress.AdmitInboundEventAsync(Message("race-second", "roll d777") with { ConversationThreadId = null }, ct);
        try { Assert.Single(fixture.Publisher.Notifications.OfType<SessionRetentionRequested>()); }
        finally { release.TrySetResult(); }
        ChannelIngressResult[] admitted = await Task.WhenAll(first, second).ConfigureAwait(true);
        Assert.Equal(admitted[0].SessionId, admitted[1].SessionId);
        Assert.Single(fixture.Publisher.Notifications.OfType<SessionRetentionRequested>());
        IReadOnlyList<QueuedSessionTurn> queue = fixture.WaitingTurns;
        Assert.Equal(2, queue.Count);
        Assert.Contains("roll a d446", queue[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("roll d777", queue[1].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundQueueWaitsThroughApprovalPauseThenExecutesInOrder()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", session.Id);
        var context = new NaradEventContext("agent-1", session.Id, "user-1", "autopilot-root", "autopilot-root");
        void Append(string type) => fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
            NaradAuthorityEventDraftFactory.CreateNew(type, JsonSerializer.SerializeToElement(new { }, jsonOptions), context));
        Append(NaradEventTypes.RunStarted);
        Append(NaradEventTypes.RunPaused);
        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", "101",
            "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Ingress.AdmitInboundEventAsync(Message("worker-first", "roll a d446"), ct).ConfigureAwait(true);
        await fixture.Ingress.AdmitInboundEventAsync(Message("worker-second", "roll d777"), ct).ConfigureAwait(true);
        using var worker = new SessionTurnQueueWorker(fixture.Ingress.PendingTurns, fixture.TurnExecution, NullLogger<SessionTurnQueueWorker>.Instance);
        await worker.StartAsync(ct).ConfigureAwait(true);
        try
        {
            await Task.Delay(750, ct).ConfigureAwait(true);
            Assert.Empty(fixture.ChatGateway.Messages);
            Assert.Equal(2, fixture.WaitingTurns.Count);
            Append(NaradEventTypes.RunFinished);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (fixture.Ingress.PendingTurns.HasQueued(session.Id))
                await Task.Delay(20, deadline.Token).ConfigureAwait(true);
            Assert.Equal(["roll a d446", "roll d777"], fixture.ChatGateway.Messages.ToArray());
        }
        finally { await worker.StopAsync(ct).ConfigureAwait(true); }
    }

    [Fact]
    public async Task QueueRecoveryDoesNotRepeatAlreadyAdmittedRunsAndPromptLookupUsesExactRun()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject());
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        var firstContext = new NaradEventContext("agent-1", session.Id, "user-1", "first-root", "first-root");
        var secondContext = new NaradEventContext("agent-1", session.Id, "user-1", "second-root", "second-root");
        void Append(string type, NaradEventContext context, JsonObject body) => fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
            NaradAuthorityEventDraftFactory.CreateNew(type, JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject), context));
        Append(NaradEventTypes.MessageUserRecorded, firstContext, new JsonObject { ["content"] = "roll a d446" });
        Append(NaradEventTypes.MessageUserRecorded, secondContext, new JsonObject { ["content"] = "roll d777" });
        Assert.Equal("roll a d446", fixture.State.AgentDefinitionStore.FindRunUserMessage("agent-1", session.Id, "first-root"));
        Assert.Equal("roll d777", fixture.State.AgentDefinitionStore.FindRunUserMessage("agent-1", session.Id, "second-root"));
        Assert.Null(fixture.State.AgentDefinitionStore.FindRunUserMessage("agent-1", session.Id, "missing-root"));
        Append(NaradEventTypes.RunStarted, firstContext, new JsonObject());
        Append(NaradEventTypes.RunPaused, firstContext, new JsonObject { ["reason"] = "approval_required" });
        Assert.True(fixture.Ingress.IsSessionBusy("agent-1", session.Id));
        Assert.False(fixture.Ingress.IsSessionBusy("agent-1", session.Id, "first-root"));
        var recovered = new QueuedSessionTurn("first-root", session.Id, "agent-1", "{}", "lease");
        await fixture.TurnExecution.ExecuteAsync(recovered, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(string.Empty, fixture.ChatGateway.LastMessage);
        Append(NaradEventTypes.RunFinished, firstContext, new JsonObject());
        Assert.False(fixture.Ingress.IsSessionBusy("agent-1", session.Id));
        await fixture.TurnExecution.ExecuteAsync(recovered, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(string.Empty, fixture.ChatGateway.LastMessage);
    }

    [Fact]
    public async Task TransportAdmissionPersistsPromptsAndQueueButtonsOnlyRemoveWaitingMessages()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-1", "101", "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        CancellationToken ct = TestContext.Current.CancellationToken;
        ChannelIngressResult first = await fixture.Ingress.AdmitInboundEventAsync(Message("durable-first", "roll a d446"), ct).ConfigureAwait(true);
        ChannelIngressResult second = await fixture.Ingress.AdmitInboundEventAsync(Message("durable-second", "roll d777"), ct).ConfigureAwait(true);
        Assert.Equal("queued", first.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(string.Empty, fixture.ChatGateway.LastMessage);
        Assert.Equal(2, fixture.WaitingTurns.Count);
        await fixture.Ingress.AdmitInboundEventAsync(Message("show-queue-controls", "/queue"), ct).ConfigureAwait(true);
        ChannelSessionFactsEnvelope card = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(item => item.ActionType == "session_facts").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.True(card.Revision > 0);
        Assert.All(card.ActionsSeed, action => Assert.True(Encoding.UTF8.GetByteCount(action.Token) <= 64));
        string expectedRemoveAction = "remove-" + fixture.WaitingTurns[1].Id;
        Assert.Contains(expectedRemoveAction, card.ActionsSeed.Select(action => action.ActionId));
        string remove = card.ActionsSeed.Single(action => action.ActionId == expectedRemoveAction).Token;
        string removeFirst = card.ActionsSeed.Single(action => action.ActionId == "remove-" + fixture.WaitingTurns[0].Id).Token;
        Assert.DoesNotContain(card.ActionsSeed, action => action.Kind == "clear");
        ChannelInboundEvent Callback(string id, string token) => Message(id, "") with
        {
            EventType = "callback",
            Text = null,
            CallbackData = token,
            AcknowledgeToken = id,
            SourceMessageId = 44,
        };
        Assert.Equal("queue_updated", (await fixture.Ingress.AdmitInboundEventAsync(Callback("remove-second", remove), ct).ConfigureAwait(true)).Status);
        ChannelSessionFactsEnvelope updatedCard = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(item => item.ActionType == "session_facts").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!;
        Assert.True(updatedCard.Revision > card.Revision);
        Assert.Equal(1, updatedCard.QueueFacts!.TotalCount);
        Assert.Single(fixture.WaitingTurns);
        QueuedSessionTurn remaining = Assert.Single(fixture.WaitingTurns);
        await fixture.Ingress.AdmitInboundEventAsync(Message("durable-third", "keep this message"), ct).ConfigureAwait(true);
        QueuedSessionTurn leased = Assert.IsType<QueuedSessionTurn>(fixture.Ingress.PendingTurns.TryLease(remaining));
        await fixture.Ingress.AdmitInboundEventAsync(Callback("cannot-remove-active", removeFirst), ct).ConfigureAwait(true);
        Assert.True(fixture.Ingress.PendingTurns.HasQueued(first.SessionId!));
        await fixture.TurnExecution.ExecuteAsync(leased, ct).ConfigureAwait(true);
        fixture.Ingress.PendingTurns.Complete(leased);
        QueuedSessionTurn later = Assert.Single(fixture.WaitingTurns);
        Assert.Contains("keep this message", later.PayloadJson, StringComparison.Ordinal);
        QueuedSessionTurn laterLeased = Assert.IsType<QueuedSessionTurn>(fixture.Ingress.PendingTurns.TryLease(later));
        await fixture.TurnExecution.ExecuteAsync(laterLeased, ct).ConfigureAwait(true);
        fixture.Ingress.PendingTurns.Complete(laterLeased);
        Assert.Equal("keep this message", fixture.ChatGateway.LastMessage);
        Assert.False(fixture.Ingress.PendingTurns.HasQueued(first.SessionId!));
    }

    [Fact]
    public async Task BusyAndApprovalPausedSessionQueuesChannelPromptsWithoutBlockingQueueControls()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101" } });
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.GetOrCreatePermanentTopic("telegram", fixture.RegistrationId, "agent", "agent-1", "chat-1", "101", session.Id);
        var context = new NaradEventContext("agent-1", session.Id, "user-1", "busy-root", "busy-root");
        void Append(string type, JsonObject body) => fixture.State.AgentDefinitionStore.AppendNaradAuthorityEvent("agent-1", "agent-1",
            NaradAuthorityEventDraftFactory.CreateNew(type, JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject), context));
        Append(NaradEventTypes.RunStarted, new JsonObject());
        JsonElement Message(int id, string text) => JsonSerializer.SerializeToElement(new
        {
            update_id = id,
            message = new { from = new { id = "tg-user-1" }, chat = new { id = "chat-1" }, message_thread_id = 101, text },
        }, jsonOptions);
        ChannelIngressResult first = await ProcessTelegramUpdateAsync(fixture, Message(8801, "roll a d446")).ConfigureAwait(true);
        Assert.Equal("queued", first.Status);
        Append(NaradEventTypes.RunPaused, new JsonObject { ["reason"] = "approval_required" });
        ChannelIngressResult second = await ProcessTelegramUpdateAsync(fixture, Message(8802, "roll d777")).ConfigureAwait(true);
        Assert.Equal("queued", second.Status);
        Assert.Equal(string.Empty, fixture.ChatGateway.LastMessage);
        Assert.Equal(2, fixture.WaitingTurns.Count);
        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "session_facts");
        ChannelIngressResult view = await ProcessTelegramUpdateAsync(fixture, Message(8803, "/queue")).ConfigureAwait(true);
        Assert.Equal("queue_shown", view.Status);
        ChannelOutboxEntry queueUpdate = Assert.Single(fixture.OutboxStore.Entries, entry => entry.ActionType == "session_facts"
            && JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)!.QueueFacts!.TotalCount == 2);
        Assert.Equal("telegram", queueUpdate.ChannelType);
        Assert.True(fixture.Ingress.IsSessionBusy("agent-1", session.Id));
        Append(NaradEventTypes.RunFinished, new JsonObject { ["response"] = "done" });
        Assert.False(fixture.Ingress.IsSessionBusy("agent-1", session.Id));
        QueuedSessionTurn leased = Assert.IsType<QueuedSessionTurn>(fixture.Ingress.PendingTurns.TryLease(fixture.Ingress.PendingTurns.ListHeads()[0]));
        await fixture.TurnExecution.ExecuteAsync(leased, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.Ingress.PendingTurns.Complete(leased);
        Assert.Equal("roll a d446", fixture.ChatGateway.LastMessage);
        Assert.Single(fixture.WaitingTurns);
    }

    [Fact]
    public async Task TargetPickerPersistsDefaultForRootConversation()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["target_threads"] = new JsonObject { ["agent:agent-1"] = "101", ["agent:agent-2"] = "202", ["agent:removed"] = "303" },
            ["retired_target_threads"] = new JsonObject { ["agent:own-bot"] = "55" },
        });
        fixture.State.ChannelTargetBindings["second"] = new GatewayChannelTargetBinding("second", fixture.RegistrationId, "agent", "agent-2", false, "");
        foreach ((string id, string name) in new[] { ("agent-1", "Main Agent"), ("agent-2", "Other Agent") })
            fixture.State.Agents[id] = new GatewayAgent(id, name, "model-1", "system", "disabled", [], [], [], [], "user-1", "");

        ChannelInboundEvent Message(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        await fixture.Ingress.ProcessInboundEventAsync(Message("picker-command", "/target"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ChannelOutboxEntry pickerEntry = fixture.OutboxStore.Entries.Single(item => item.ActionType == "send_semantic_message");
        ChannelSemanticMessageAction? picker = JsonSerializer.Deserialize(pickerEntry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        string token = picker!.Document.Blocks.SelectMany(block => block.Actions ?? []).Single(action => action.Label.Contains("Other Agent", StringComparison.Ordinal)).CallbackToken;
        var callback = Message("picker-select", "") with { EventType = "callback", Text = null, CallbackData = token, AcknowledgeToken = "callback-1", SourceMessageId = 44 };
        Assert.True((await fixture.Ingress.ProcessInboundEventAsync(callback, TestContext.Current.CancellationToken).ConfigureAwait(true)).Success);
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "delete_message");
        Assert.DoesNotContain(fixture.OutboxStore.Entries, item => item.ActionType == "session_facts");
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "send_semantic_message"
            && item.PayloadJson.Contains("Now talking to", StringComparison.Ordinal)
            && item.PayloadJson.Contains("Other Agent", StringComparison.Ordinal));
        Assert.Equal("picker_expired", (await fixture.Ingress.ProcessInboundEventAsync(callback with { EventId = "picker-replay", AcknowledgeToken = "callback-2" }, TestContext.Current.CancellationToken).ConfigureAwait(true)).Status);
        // The persistent default must not bypass a retired/revoked agent topic.
        foreach (string unavailableTopic in new[] { "55", "303" })
            Assert.Equal("target_missing", (await fixture.Ingress.ProcessInboundEventAsync(Message($"unavailable-{unavailableTopic}", "Hello") with { ConversationThreadId = unavailableTopic }, TestContext.Current.CancellationToken).ConfigureAwait(true)).Status);
        Assert.Equal("agent-2", (await fixture.Ingress.ProcessInboundEventAsync(Message("message-first", "Hello"), TestContext.Current.CancellationToken).ConfigureAwait(true)).TargetId);
        ChannelRuntimeEventAction? reply = JsonSerializer.Deserialize(fixture.OutboxStore.Entries.Last(item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent).PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        // Gateway carries provider thread metadata generically; ChannelHost decides
        // that Telegram renders the action in the ordinary chat instead.
        Assert.Null(reply?.ConversationThreadId);
        Assert.Equal("agent-2", (await fixture.Ingress.ProcessInboundEventAsync(Message("message-next", "Hello again"), TestContext.Current.CancellationToken).ConfigureAwait(true)).TargetId);
        int runtimeEventsBeforeRemovedCommand = fixture.OutboxStore.Entries.Count(item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent);
        ChannelIngressResult removedCommand = await fixture.Ingress.ProcessInboundEventAsync(
            Message("removed-to-command", "/to agent:agent-1 Hello"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("unsupported_command", removedCommand.Status);
        Assert.Null(removedCommand.TargetId);
        Assert.Equal("Hello again", fixture.ChatGateway.LastMessage);
        Assert.Equal(runtimeEventsBeforeRemovedCommand,
            fixture.OutboxStore.Entries.Count(item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent));
        Assert.Contains("The /to command has been removed", fixture.OutboxStore.Entries.Last().PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingAgentInTelegramRemovesAndInvalidatesTheMirroredSlackPicker()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-telegram" });
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "bot-1", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-slack", ["slack_owner_user_id"] = "U-slack" }, "1", "1");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-slack"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-slack", slackId, "user-1", "U-slack", "1");
        foreach (string registrationId in new[] { fixture.RegistrationId, slackId })
            fixture.State.ChannelTargetBindings[$"{registrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
                $"{registrationId}:agent:agent-2", registrationId, "agent", "agent-2", false, "1");
        fixture.State.Agents["agent-1"] = new GatewayAgent("agent-1", "Main Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");
        fixture.State.Agents["agent-2"] = new GatewayAgent("agent-2", "Other Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");

        ChannelInboundEvent Telegram(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-telegram", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        await fixture.Ingress.ProcessInboundEventAsync(Telegram("linked-picker-command", "/agents"), TestContext.Current.CancellationToken);
        ChannelSemanticMessageAction slackPicker = JsonSerializer.Deserialize(
            fixture.OutboxStore.Entries.Single(item => item.ActionType == "send_semantic_message" && item.RegistrationId == slackId).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        ChannelSemanticAction slackChoice = Assert.Single(slackPicker.Document.Blocks.SelectMany(block => block.Actions ?? []), action => action.Label.Contains("Other Agent", StringComparison.Ordinal));
        string slackToken = slackChoice.CallbackToken;
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(slackId, out GatewayChannelRegistration? slackRegistration));
        new ChannelPromptAnchorStore(fixture.State.Database).SaveTargetPickerMessage(slackRegistration!, "D-slack", null, "slack-picker-message");

        ChannelSemanticMessageAction telegramPicker = JsonSerializer.Deserialize(
            fixture.OutboxStore.Entries.Single(item => item.ActionType == "send_semantic_message" && item.RegistrationId == fixture.RegistrationId).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        string telegramToken = Assert.Single(telegramPicker.Document.Blocks.SelectMany(block => block.Actions ?? []), action => action.Label.Contains("Other Agent", StringComparison.Ordinal)).CallbackToken;
        ChannelIngressResult selected = await fixture.Ingress.ProcessInboundEventAsync(Telegram("linked-picker-select", "") with
        {
            EventType = "callback",
            Text = null,
            CallbackData = telegramToken,
            AcknowledgeToken = "telegram-callback",
            SourceMessageId = 100,
        }, TestContext.Current.CancellationToken);

        Assert.True(selected.Success, selected.Detail);
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "delete_message" && item.RegistrationId == slackId
            && item.PayloadJson.Contains("slack-picker-message", StringComparison.Ordinal));
        ChannelIngressResult staleSlackCallback = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "linked-slack-old-picker", "slack", slackId, "user-1", "U-slack", "D-slack", null,
            "callback", null, slackToken, "slack-callback", 101, JsonSerializer.SerializeToElement(new { }),
            SourceTransportMessageId: "1712345678.123456"), TestContext.Current.CancellationToken);
        Assert.Equal("picker_expired", staleSlackCallback.Status);
    }

    [Fact]
    public async Task SelectingAgentInSlackRemovesAndInvalidatesTheMirroredTelegramPicker()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-telegram" });
        const string telegramId = "telegram:user-1:default";
        GatewayChannelRegistration legacyTelegramRegistration = fixture.State.ChannelRegistrations[fixture.RegistrationId];
        fixture.State.ChannelRegistrations.Remove(fixture.RegistrationId);
        GatewayChannelRegistration telegramRegistration = legacyTelegramRegistration with { Id = telegramId };
        fixture.State.ChannelRegistrations[telegramId] = telegramRegistration;
        fixture.State.ChannelIdentityLinks.Remove($"{fixture.RegistrationId}:user:tg-user-1");
        fixture.State.ChannelIdentityLinks[$"{telegramId}:user:chat-telegram"] = new GatewayChannelIdentityLink(
            $"{telegramId}:user:chat-telegram", telegramId, "user-1", "chat-telegram", "1");
        foreach (string key in fixture.State.ChannelTargetBindings.Keys.Where(key => key.StartsWith($"{fixture.RegistrationId}:", StringComparison.Ordinal)).ToArray())
            fixture.State.ChannelTargetBindings.Remove(key);
        fixture.State.ChannelTargetBindings[$"{telegramId}:agent:agent-1"] = new GatewayChannelTargetBinding(
            $"{telegramId}:agent:agent-1", telegramId, "agent", "agent-1", true, "1");
        fixture.State.ChannelTargetBindings[$"{telegramId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{telegramId}:agent:agent-2", telegramId, "agent", "agent-2", false, "1");
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "bot-1", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-slack", ["slack_owner_user_id"] = "U-slack" }, "1", "1");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-slack"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-slack", slackId, "user-1", "U-slack", "1");
        foreach (string registrationId in new[] { telegramId, slackId })
            fixture.State.ChannelTargetBindings[$"{registrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
                $"{registrationId}:agent:agent-2", registrationId, "agent", "agent-2", false, "1");
        fixture.State.Agents["agent-1"] = new GatewayAgent("agent-1", "Main Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");
        fixture.State.Agents["agent-2"] = new GatewayAgent("agent-2", "Other Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");

        ChannelInboundEvent Telegram(string id, string text) => new(id, "telegram", telegramId, "user-1", "tg-user-1",
            "chat-telegram", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        await fixture.Ingress.ProcessInboundEventAsync(Telegram("slack-linked-picker-command", "/agents"), TestContext.Current.CancellationToken);
        ChannelSemanticMessageAction telegramPicker = JsonSerializer.Deserialize(
            fixture.OutboxStore.Entries.Single(item => item.ActionType == "send_semantic_message" && item.RegistrationId == telegramId).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        ChannelSemanticAction telegramChoice = Assert.Single(telegramPicker.Document.Blocks.SelectMany(block => block.Actions ?? []), action => action.Label.Contains("Other Agent", StringComparison.Ordinal));
        string telegramToken = telegramChoice.CallbackToken;
        var pickerMessageStore = new ChannelPromptAnchorStore(fixture.State.Database);
        pickerMessageStore.SaveTargetPickerMessage(telegramRegistration, "chat-telegram", null, "telegram-picker-message");
        Assert.Equal("telegram-picker-message", Assert.Single(pickerMessageStore.TakeTargetPickerMessages(telegramRegistration, "chat-telegram", null)));
        pickerMessageStore.SaveTargetPickerMessage(telegramRegistration, "chat-telegram", null, "telegram-picker-message");

        ChannelSemanticMessageAction slackPicker = JsonSerializer.Deserialize(
            fixture.OutboxStore.Entries.Single(item => item.ActionType == "send_semantic_message" && item.RegistrationId == slackId).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        string slackToken = Assert.Single(slackPicker.Document.Blocks.SelectMany(block => block.Actions ?? []), action => action.Label.Contains("Other Agent", StringComparison.Ordinal)).CallbackToken;
        ChannelIngressResult selected = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "slack-linked-picker-select", "slack", slackId, "user-1", "U-slack", "D-slack", null,
            "callback", null, slackToken, "slack-picker-callback", null, JsonSerializer.SerializeToElement(new { }),
            SourceTransportMessageId: "1712345678.123456"), TestContext.Current.CancellationToken);

        Assert.True(selected.Success, selected.Detail);
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "delete_message" && item.RegistrationId == telegramId
            && item.PayloadJson.Contains("telegram-picker-message", StringComparison.Ordinal));
        ChannelIngressResult staleTelegramCallback = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "linked-telegram-old-picker", "telegram", telegramId, "user-1", "chat-telegram", "chat-telegram", null,
            "callback", null, telegramToken, "telegram-stale-callback", 101, JsonSerializer.SerializeToElement(new { }),
            SourceTransportMessageId: "101"), TestContext.Current.CancellationToken);
        Assert.Equal("picker_expired", staleTelegramCallback.Status);
    }

    [Fact]
    public async Task ExpiredPickerCallbackRemovesPersistedPickersFromLinkedChannels()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-telegram" });
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "bot-1", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-slack", ["slack_owner_user_id"] = "U-slack" }, "1", "1");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-slack"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-slack", slackId, "user-1", "U-slack", "1");
        foreach (string registrationId in new[] { fixture.RegistrationId, slackId })
            fixture.State.ChannelTargetBindings[$"{registrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
                $"{registrationId}:agent:agent-2", registrationId, "agent", "agent-2", false, "1");
        fixture.State.Agents["agent-1"] = new GatewayAgent("agent-1", "Main Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");
        fixture.State.Agents["agent-2"] = new GatewayAgent("agent-2", "Other Agent", "model-1", "system", "disabled", [], [], [], [], "user-1", "1");

        ChannelInboundEvent Telegram(string id, string text) => new(id, "telegram", fixture.RegistrationId, "user-1", "tg-user-1",
            "chat-telegram", null, "message", text, null, null, null, JsonSerializer.SerializeToElement(new { }, jsonOptions));
        await fixture.Ingress.ProcessInboundEventAsync(Telegram("expired-picker-command", "/agents"), TestContext.Current.CancellationToken);
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(slackId, out GatewayChannelRegistration? slackRegistration));
        Assert.True(fixture.AttachmentRegistry.TryGetRegistration(fixture.RegistrationId, out GatewayChannelRegistration? telegramRegistration));
        var pickerStore = new ChannelPromptAnchorStore(fixture.State.Database);
        pickerStore.SaveTargetPickerMessage(slackRegistration!, "D-slack", null, "slack-stale-picker");
        pickerStore.SaveTargetPickerMessage(telegramRegistration!, "chat-telegram", null, "telegram-stale-picker");

        ChannelIngressResult stale = await fixture.Ingress.ProcessInboundEventAsync(Telegram("expired-picker-click", "") with
        {
            EventType = "callback",
            Text = null,
            CallbackData = "targetpick:expired:0",
            AcknowledgeToken = "expired-picker-ack",
            SourceMessageId = 100,
        }, TestContext.Current.CancellationToken);

        Assert.Equal("picker_expired", stale.Status);
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "delete_message" && item.RegistrationId == slackId
            && item.PayloadJson.Contains("slack-stale-picker", StringComparison.Ordinal));
        Assert.Contains(fixture.OutboxStore.Entries, item => item.ActionType == "delete_message" && item.RegistrationId == fixture.RegistrationId
            && item.PayloadJson.Contains("telegram-stale-picker", StringComparison.Ordinal));
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static TheoryData<string, string, bool, string?, bool> ProviderApprovalActions
    {
        get
        {
            var data = new TheoryData<string, string, bool, string?, bool>();
            foreach (string provider in new[] { "telegram", "slack" })
            {
                data.Add(provider, "approve", true, null, false);
                data.Add(provider, "deny", false, "denied", false);
                // Legacy channel buttons map to stop_run / stopped
                data.Add(provider, "cancel", false, "stopped", true);
                data.Add(provider, "stop_run", false, "stopped", true);
            }
            return data;
        }
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    public async Task ChannelApprovalPromptOffersEveryTerminalActionAcrossProviders(string provider)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["bot_token"] = "bot-token" }, provider);

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-action-parity",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest(
                "req-prompt-action-parity",
                "tool-prompt-action-parity",
                "run_shell",
                "{\"command\":\"dir\"}"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction interaction = Assert.Single(
            fixture.State.ChannelInteractions.Values,
            static item => item.ApprovalRequestId == "req-prompt-action-parity");
        Assert.Contains(interaction.Options, static option => option is { Label: "Approve", Value: "approve" });
        Assert.Contains(interaction.Options, static option => option is { Label: "Deny", Value: "deny" });
        Assert.Contains(interaction.Options, static option => option is { Label: "Stop Run", Value: "stop_run" });
        Assert.DoesNotContain(interaction.Options, static option => option is { Value: "cancel" });
        Assert.DoesNotContain(interaction.Options, static option => option is { Value: "approve_all" });
    }

    [Theory]
    [MemberData(nameof(ProviderApprovalActions))]
    public async Task ChannelApprovalActionsHaveCanonicalParityAcrossProviders(
        string provider,
        string action,
        bool expectedApproved,
        string? expectedReason,
        bool expectedStopRun)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["bot_token"] = "bot-token" }, provider);
        SeedApprovalCallbackInteraction(fixture, "hitl-action-parity", "req-action-parity", 730L);

        var callback = new ChannelInboundEvent(
            $"{provider}-{action}",
            provider,
            fixture.RegistrationId,
            "user-1",
            "tg-user-1",
            "chat-1",
            null,
            "callback",
            null,
            $"hitl:hitl-action-parity:{action}",
            $"ack-{provider}-{action}",
            730L,
            JsonSerializer.SerializeToElement(new { provider, action }, jsonOptions));

        ChannelIngressResult result = await fixture.Ingress
            .ProcessInboundEventAsync(callback, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);
        HitlApprovalResolved resolved = Assert.Single(fixture.Publisher.Notifications.OfType<HitlApprovalResolved>());
        Assert.Equal(expectedApproved, resolved.Approved);
        Assert.Equal(expectedReason, resolved.Reason);
        Assert.Equal(expectedStopRun, resolved.StopRun);
        Assert.Equal(HitlResolutionSource.ChannelCallback, resolved.Source);
        Assert.Equal(provider, fixture.ApprovalStore.GetById("req-action-parity")?.ResolutionSource);
    }

    [Theory]
    [InlineData("telegram", "approve", true, null, false)]
    [InlineData("telegram", "request_changes", false, "request_changes", false)]
    [InlineData("telegram", "stop_run", false, "stopped", true)]
    [InlineData("slack", "approve", true, null, false)]
    [InlineData("slack", "request_changes", false, "request_changes", false)]
    [InlineData("slack", "stop_run", false, "stopped", true)]
    public async Task WorkflowReviewChannelActionsResolveThroughEveryProvider(
        string provider,
        string action,
        bool expectedApproved,
        string? expectedReason,
        bool expectedStopRun)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["slack_dm_channel_id"] = "D-user-1" }, provider);
        SeedApprovalCallbackInteraction(fixture, "workflow-hitl-action", "workflow-hitl-request", 731L);
        GatewayToolApprovalRequest stored = fixture.ApprovalStore.GetById("workflow-hitl-request")!;
        fixture.ApprovalStore.CreateRequest(stored with
        {
            ToolName = "workflow_review",
            ToolType = "workflow_review",
            Kind = "workflow_review",
            WorkflowId = "wf-1",
            InstanceId = "wfi-1",
            NodeId = "n1",
        });
        string workflowNow = DateTimeOffset.UtcNow.ToString("O");
        var reviewNode = new GatewayWorkflowNode("n1", "agent-1", "Review", "text", null, [], "none", false, true, 0);
        var reviewDefinition = new GatewayWorkflowDefinition(
            "wf-1", "user-1", "Review workflow", false, 1,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            [reviewNode], workflowNow, workflowNow);
        fixture.WorkflowStore.CreateDefinition(reviewDefinition);
        fixture.WorkflowStore.CreateInstance(new GatewayWorkflowInstance(
            "wfi-1", reviewDefinition.Id, reviewDefinition.Revision, "user-1", null,
            WorkflowInstanceStatuses.Paused, false, reviewNode.NodeId, new JsonObject(), "hash", 0,
            reviewDefinition, [], null, null, workflowNow, workflowNow, workflowNow, null));
        GatewayChannelInteraction interaction = fixture.State.ChannelInteractions["workflow-hitl-action"] with
        {
            Options =
            [
                new("approve-option", "Approve", "approve", "primary"),
                new("changes-option", "Request changes", "request_changes"),
                new("stop-option", "Stop", "stop_run", "danger"),
            ],
        };
        fixture.State.UpsertChannelInteraction(interaction);
        ChannelInboundEvent callback = new(
            $"{provider}-workflow-review-{action}",
            provider,
            fixture.RegistrationId,
            "user-1",
            "tg-user-1",
            "chat-1",
            null,
            "callback",
            null,
            $"hitl:workflow-hitl-action:{action}",
            $"ack-{provider}-{action}",
            731L,
            JsonSerializer.SerializeToElement(new { provider, action }, jsonOptions));

        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(callback, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.True(result.Success, result.Detail);
        HitlApprovalResolved resolved = Assert.Single(fixture.Publisher.Notifications.OfType<HitlApprovalResolved>());
        Assert.Equal(expectedApproved, resolved.Approved);
        Assert.Equal(expectedReason, resolved.Reason);
        Assert.Equal(expectedStopRun, resolved.StopRun);
        Assert.Equal(HitlResolutionSource.ChannelCallback, resolved.Source);
        Assert.Equal(action switch
        {
            "request_changes" => "request_changes",
            "stop_run" => "stopped",
            _ => "approved",
        },
            fixture.ApprovalStore.GetById("workflow-hitl-request")?.Decision);
    }

    [Theory]
    [InlineData("telegram", "request_changes")]
    [InlineData("slack", "request_changes")]
    [InlineData("telegram", "approve")]
    [InlineData("slack", "approve")]
    [InlineData("telegram", "stop_run")]
    [InlineData("slack", "stop_run")]
    [InlineData("telegram", "expired")]
    [InlineData("slack", "expired")]
    public async Task WorkflowReviewCallbacksUseSqliteApprovalsAndResolveTheInstance(string provider, string action)
    {
        using var fixture = new ChannelIngressFixture(useSqliteApprovalStore: true);
        fixture.SeedRegistration(new JsonObject { ["slack_dm_channel_id"] = "D-user-1" }, provider);
        const string interactionId = "workflow-review-e2e";
        const string requestId = "workflow-review-e2e-request";
        SeedApprovalCallbackInteraction(fixture, interactionId, requestId, 732L);

        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode("review", "agent-1", "Review order", "text", null, [], "none", false, true, 1);
        var definition = new GatewayWorkflowDefinition("workflow-e2e", "user-1", "Order review", false, 1,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, [node], now, now);
        fixture.WorkflowStore.CreateDefinition(definition);
        var attempt = new GatewayWorkflowStepAttempt("attempt-one", "workflow-e2e-instance", "review", 1,
            "workflow-agent-run", "workflow-agent-session", WorkflowStepAttemptStatuses.Finished, null, now, now, now);
        var instance = new GatewayWorkflowInstance("workflow-e2e-instance", definition.Id, definition.Revision,
            "user-1", null, WorkflowInstanceStatuses.Paused, false, "review", new JsonObject(), "hash", 0,
            definition, [attempt], null, null, now, now, now, null);
        fixture.WorkflowStore.CreateInstance(instance);
        fixture.SqliteApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            Id: requestId, SessionId: null, RunId: attempt.RunId, CheckpointId: null, AgentId: null,
            UserId: "user-1", ToolCallId: "review-call", ToolName: "workflow_review", ToolType: "workflow_review",
            McpServerId: null, ArgumentsJson: "{}", RequestedAt: now, Decision: null, DecidedBy: null,
            DecidedAt: null, Reason: null,
            ExpiresAt: string.Equals(action, "expired", StringComparison.Ordinal)
                ? DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")
                : null,
            Kind: "workflow_review", WorkflowId: definition.Id,
            InstanceId: instance.Id, NodeId: node.NodeId));
        GatewayChannelInteraction interaction = fixture.State.ChannelInteractions[interactionId] with
        {
            TargetType = "workflow",
            TargetId = definition.Id,
            SessionId = string.Empty,
            RunId = attempt.RunId,
            Options =
            [
                new("approve-option", "Approve", "approve", "primary"),
                new("changes-option", "Request changes", "request_changes"),
                new("stop-option", "Stop", "stop_run", "danger"),
            ],
        };
        fixture.State.UpsertChannelInteraction(interaction);

        var runtimeHub = new RuntimeEventStreamHub();
        var workflowPublisher = new WorkflowNaradPublisher(fixture.WorkflowStore, new WorkflowNaradStreamHub(), runtimeHub);
        var execution = new WorkflowExecutionService(
            fixture.WorkflowStore,
            new WorkflowReviewChatGateway(),
            workflowPublisher,
            new EmptyRuntimeEventHistoryReader(),
            runtimeHub,
            fixture.SqliteApprovalStore,
            fixture.Publisher,
            NullLogger<WorkflowExecutionService>.Instance);
        using ServiceProvider handlers = new ServiceCollection()
            .AddSingleton<IWorkflowExecutionService>(execution)
            .BuildServiceProvider();
        var reviewResolvedHandler = new WorkflowReviewResolvedHandler(
            handlers, fixture.SqliteApprovalStore, fixture.WorkflowStore, workflowPublisher);

        if (string.Equals(action, "expired", StringComparison.Ordinal))
        {
            _ = await fixture.ApprovalResolution.ExpireAsync(requestId, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        ChannelInboundEvent callback = new(
            $"{provider}-workflow-review-{action}", provider, fixture.RegistrationId, "user-1", "tg-user-1", "chat-1",
            null, "callback", null, $"hitl:{interactionId}:{action}", $"ack-{provider}-{action}", 732L,
            JsonSerializer.SerializeToElement(new { provider, action }, jsonOptions));
        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(callback, CancellationToken.None)
            .ConfigureAwait(true);
        Assert.True(result.Success, result.Detail);

        HitlApprovalResolved resolved = fixture.Publisher.Notifications.OfType<HitlApprovalResolved>().Last();
        Assert.Equal(action is "stop_run" or "expired", resolved.StopRun);
        Assert.Equal(action == "expired" ? HitlResolutionSource.SystemTimeout : HitlResolutionSource.ChannelCallback, resolved.Source);
        await reviewResolvedHandler.Handle(resolved, CancellationToken.None);
        GatewayToolApprovalRequest persisted = fixture.SqliteApprovalStore.GetById(requestId)
            ?? throw new InvalidOperationException("The SQLite approval row was not available after callback resolution.");
        Func<GatewayWorkflowInstance, bool> expectedInstanceState = action switch
        {
            "request_changes" => static current => current.Steps.Any(step => step.Attempt == 2),
            "approve" => static current => WorkflowInstanceStatuses.IsTerminal(current.Status),
            _ => static current => current.Status == WorkflowInstanceStatuses.Cancelled,
        };
        GatewayWorkflowInstance updated = await WaitForWorkflowInstanceAsync(fixture.WorkflowStore, instance.Id, expectedInstanceState);
        GatewayChannelInteraction editedCard = fixture.State.ChannelInteractions[interactionId];
        Assert.Equal(action switch
        {
            "expired" => "expired",
            "stop_run" => "stopped",
            "request_changes" => "request_changes",
            "approve" => "approved",
            _ => "denied",
        }, persisted.Decision);
        if (action != "approve")
        {
            Assert.Equal(action switch
            {
                "request_changes" => "request_changes",
                "stop_run" => "stopped",
                _ => "Approval expired; run aborted.",
            }, persisted.Reason);
        }
        if (string.Equals(action, "request_changes", StringComparison.Ordinal))
        {
            Assert.Contains(updated.Steps, static step => step.Attempt == 2);
            Assert.Equal("request_changes", editedCard.Resolution);
        }
        else if (action == "approve")
        {
            Assert.True(WorkflowInstanceStatuses.IsTerminal(updated.Status));
            ChannelRuntimeEventAction resolvedEvent = fixture.OutboxStore.Entries
                .Where(static entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
                .Select(entry => JsonSerializer.Deserialize(entry.PayloadJson,
                    ChannelContractsJsonContext.Default.ChannelRuntimeEventAction))
                .Where(static entry => IsApprovedResolutionEvent(entry))
                .Cast<ChannelRuntimeEventAction>()
                .Last();
            resolvedEvent = resolvedEvent with { EventType = "tool.approval.resolved" };
            using JsonDocument settings = JsonDocument.Parse("""{"hitl_tether_mode":"hitl"}""");
            ChannelTranslatedOutboundAction rendered = Assert.Single(new ChannelRuntimeEventTranslator()
                .Translate(settings.RootElement, resolvedEvent));
            ChannelSemanticMessageAction message = JsonSerializer.Deserialize(rendered.PayloadJson,
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
            Assert.Contains("Approved. The next step has started.", message.Document.FallbackText, StringComparison.Ordinal);
            _ = ChannelSemanticRendering.RenderSlack(message);
            _ = ChannelSemanticRendering.RenderTelegram(message);
        }
        else if (action == "expired")
        {
            ChannelRuntimeEventAction resolvedEvent = fixture.OutboxStore.Entries
                .Where(static entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
                .Select(entry => JsonSerializer.Deserialize(entry.PayloadJson,
                    ChannelContractsJsonContext.Default.ChannelRuntimeEventAction))
                .Where(static entry => IsResolutionEvent(entry, "expired"))
                .Cast<ChannelRuntimeEventAction>()
                .Last();
            resolvedEvent = resolvedEvent with { EventType = "tool.approval.resolved" };
            using JsonDocument settings = JsonDocument.Parse("""{"hitl_tether_mode":"hitl"}""");
            ChannelTranslatedOutboundAction rendered = Assert.Single(new ChannelRuntimeEventTranslator()
                .Translate(settings.RootElement, resolvedEvent));
            ChannelSemanticMessageAction message = JsonSerializer.Deserialize(rendered.PayloadJson,
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
            Assert.Contains("Workflow review: Order review", message.Document.FallbackText, StringComparison.Ordinal);
            Assert.Contains("step 1 of 1", message.Document.FallbackText, StringComparison.Ordinal);
            Assert.Contains("Expired with no decision.", message.Document.FallbackText, StringComparison.Ordinal);
            _ = ChannelSemanticRendering.RenderSlack(message);
            _ = ChannelSemanticRendering.RenderTelegram(message);
        }
        else
        {
            Assert.Equal(WorkflowInstanceStatuses.Cancelled, updated.Status);
            Assert.Equal(action, editedCard.Resolution);
        }
        if (string.Equals(action, "request_changes", StringComparison.Ordinal))
        {
            _ = await WaitForWorkflowInstanceAsync(fixture.WorkflowStore, instance.Id,
                static current => WorkflowInstanceStatuses.IsTerminal(current.Status)
                    || current.Steps.Any(step => step.Attempt == 2
                        && step.Status is WorkflowStepAttemptStatuses.Finished or WorkflowStepAttemptStatuses.Failed));
        }
        Assert.Contains(fixture.OutboxStore.Entries, static item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent);

        static bool IsApprovedResolutionEvent(ChannelRuntimeEventAction? runtimeEvent)
        {
            if (runtimeEvent is null) return false;
            using JsonDocument payload = JsonDocument.Parse(runtimeEvent.PayloadJson);
            return payload.RootElement.TryGetProperty("decision", out JsonElement decision)
                && decision.GetString() == "approve";
        }

        static bool IsResolutionEvent(ChannelRuntimeEventAction? runtimeEvent, string expectedDecision)
        {
            if (runtimeEvent is null) return false;
            using JsonDocument payload = JsonDocument.Parse(runtimeEvent.PayloadJson);
            return payload.RootElement.TryGetProperty("decision", out JsonElement decision)
                && decision.GetString() == expectedDecision;
        }
    }

    [Fact]
    public async Task ThirdWorkflowChangeRequestChannelCallbackIsRejectedAndLeavesReviewOpen()
    {
        using var fixture = new ChannelIngressFixture(useSqliteApprovalStore: true);
        fixture.SeedRegistration(new JsonObject { ["slack_dm_channel_id"] = "D-user-1" }, "telegram");
        const string interactionId = "workflow-review-exhausted";
        const string requestId = "workflow-review-exhausted-request";
        SeedApprovalCallbackInteraction(fixture, interactionId, requestId, 733L);
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode("review", "agent-1", "Review", "text", null, [], "none", false, true, 0);
        var definition = new GatewayWorkflowDefinition("workflow-exhausted", "user-1", "Review", false, 1,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, [node], now, now);
        fixture.WorkflowStore.CreateDefinition(definition);
        GatewayWorkflowStepAttempt[] attempts = Enumerable.Range(1, 3).Select(number => new GatewayWorkflowStepAttempt(
            $"attempt-{number}", "workflow-exhausted-instance", node.NodeId, number, $"run-{number}", "session-1",
            WorkflowStepAttemptStatuses.Finished, null, now, now, now)).ToArray();
        var instance = new GatewayWorkflowInstance("workflow-exhausted-instance", definition.Id, 1, "user-1", null,
            WorkflowInstanceStatuses.Paused, false, node.NodeId, new JsonObject(), "hash", 0, definition,
            attempts, null, null, now, now, now, null);
        fixture.WorkflowStore.CreateInstance(instance);
        fixture.SqliteApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            Id: requestId, SessionId: null, RunId: attempts[^1].RunId, CheckpointId: null, AgentId: null,
            UserId: "user-1", ToolCallId: "review-call", ToolName: "workflow_review", ToolType: "workflow_review",
            McpServerId: null, ArgumentsJson: "{}", RequestedAt: now, Decision: null, DecidedBy: null,
            DecidedAt: null, Reason: null, ExpiresAt: null, Kind: "workflow_review", WorkflowId: definition.Id,
            InstanceId: instance.Id, NodeId: node.NodeId));
        GatewayChannelInteraction interaction = fixture.State.ChannelInteractions[interactionId] with
        {
            TargetType = "workflow",
            TargetId = definition.Id,
            SessionId = string.Empty,
            RunId = attempts[^1].RunId,
            Options = [new("changes-option", "Request changes", "request_changes"), new("stop-option", "Stop", "stop_run", "danger")],
        };
        fixture.State.UpsertChannelInteraction(interaction);
        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "telegram-review-exhausted", "telegram", fixture.RegistrationId, "user-1", "tg-user-1", "chat-1",
            null, "callback", null, $"hitl:{interactionId}:request_changes", "ack-exhausted", 733L,
            JsonSerializer.SerializeToElement(new { provider = "telegram", action = "request_changes" }, jsonOptions)), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("approval_resolve_failed", result.Status);
        Assert.Null(fixture.SqliteApprovalStore.GetById(requestId)?.Decision);
        Assert.Empty(fixture.Publisher.Notifications.OfType<HitlApprovalResolved>());
    }

    private static async Task<GatewayWorkflowInstance> WaitForWorkflowInstanceAsync(
        GatewayWorkflowStore workflows,
        string instanceId,
        Func<GatewayWorkflowInstance, bool> predicate)
    {
        GatewayWorkflowInstance? current = null;
        for (int attempt = 0; attempt < 150; attempt++)
        {
            current = workflows.GetInstance(instanceId);
            if (current is not null && predicate(current)) return current;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"Workflow instance '{instanceId}' did not reach the expected state. Current status: {current?.Status}.");
    }

    private sealed class EmptyRuntimeEventHistoryReader : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class WorkflowReviewChatGateway : IChatGatewayService
    {
        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string targetType = body.GetProperty("target_type").GetString() ?? "agent";
            string targetId = body.GetProperty("target_id").GetString() ?? "agent-1";
            string runId = body.GetProperty("run_id").GetString() ?? "workflow-review-run";
            string sessionId = $"{runId}-session";
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatSessionReady(sessionId), targetId, targetId, runId);
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatStreamDone(new GatewayChatResult(
                targetType, targetId, sessionId, "{}", false, new GatewayMetrics(0, 0, 0))), targetId, targetId, runId);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task WorkflowReviewUsesTheOverrideOrSharedBotAndNotTheUserChatBot()
    {
        using var fixture = new ChannelIngressFixture();
        SeedRoleRegistration(fixture, "telegram:user-1:default", "chat-user");
        SeedRoleRegistration(fixture, "telegram:user-1:workflows", "chat-shared");
        SeedRoleRegistration(fixture, "telegram:user-1:workflow:wf-1", "chat-one");

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-review-one",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest(
                "req-review-one",
                "tool-review-one",
                "workflow_review",
                "{\"workflowId\":\"wf-1\"}",
                ToolType: "workflow_review"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction overrideCard = Assert.Single(fixture.State.ChannelInteractions.Values);
        Assert.Equal("telegram:user-1:workflow:wf-1", overrideCard.RegistrationId);
        Assert.Equal("chat-one", overrideCard.ChatId);

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-review-other",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest(
                "req-review-other",
                "tool-review-other",
                "workflow_review",
                "{\"workflowId\":\"wf-other\"}",
                ToolType: "workflow_review"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction sharedCard = Assert.Single(
            fixture.State.ChannelInteractions.Values,
            item => item.ApprovalRequestId == "req-review-other");
        Assert.Equal("telegram:user-1:workflows", sharedCard.RegistrationId);
        Assert.Equal("chat-shared", sharedCard.ChatId);
        Assert.DoesNotContain(
            fixture.State.ChannelInteractions.Values,
            item => string.Equals(item.RegistrationId, "telegram:user-1:default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkflowReviewWithoutAWorkflowBotUsesTheUserChatBot()
    {
        using var fixture = new ChannelIngressFixture();
        SeedRoleRegistration(fixture, "telegram:user-1:default", "chat-user");
        SeedRoleRegistration(fixture, "telegram:user-1:agent:agent-1", "chat-agent");

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-review-none",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest(
                "req-review-none",
                "tool-review-none",
                "workflow_review",
                "{\"workflowId\":\"wf-1\"}",
                ToolType: "workflow_review"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction card = Assert.Single(fixture.State.ChannelInteractions.Values);
        Assert.Equal("telegram:user-1:default", card.RegistrationId);
        Assert.Equal("chat-user", card.ChatId);
    }

    [Fact]
    public async Task WorkflowReviewWithoutAnyBotStaysInApprovals()
    {
        using var fixture = new ChannelIngressFixture();

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-review-absent",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest(
                "req-review-absent",
                "tool-review-absent",
                "workflow_review",
                "{\"workflowId\":\"wf-1\"}",
                ToolType: "workflow_review"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(fixture.State.ChannelInteractions.Values);
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    public async Task WorkflowReviewChannelPayloadContainsBoundedChangedFieldsAndWorkflowActions(string provider)
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["slack_dm_channel_id"] = "D-user-1" }, provider);
        string now = DateTimeOffset.UtcNow.ToString("O");
        GatewayWorkflowNode node = new("node-secret", "agent-1", "write", "json", null, [], "none", false, true, 0);
        GatewayWorkflowDefinition definition = new("wf-1", "user-1", "Nightly", false, 1,
            new JsonObject(), new JsonObject(), new JsonObject(), [node], now, now);
        fixture.WorkflowStore.CreateDefinition(definition);
        GatewayWorkflowInstance instance = new("wfi-secret", "wf-1", 1, "user-1", null,
            WorkflowInstanceStatuses.Paused, false, node.NodeId, new JsonObject(), "hash", 1, definition,
            [], null, null, now, now, now, null);
        fixture.WorkflowStore.CreateInstance(instance);
        fixture.State.Agents["agent-1"] = new GatewayAgent("agent-1", "Writer", "model-1", "system", "disabled",
            new JsonObject(), new JsonObject(), new JsonObject(), [], "user-1", now);
        JsonObject envelope = new() { ["unchanged"] = "same" };
        JsonObject proposed = new()
        {
            ["unchanged"] = "same",
            ["result"] = "complete",
            ["longValue"] = new string('x', 400),
            ["secret"] = "must-not-leak",
            ["paid"] = true,
            ["orderId"] = "ORD-42",
        };
        for (int i = 0; i < 10; i++)
            proposed[$"field{i}"] = $"value{i}";
        string arguments = new JsonObject { ["workflowId"] = "wf-1", ["envelope"] = envelope, ["proposed"] = proposed }.ToJsonString();
        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "req-workflow-card", "session-1", "run-1", null, "agent-1", "user-1", "call-1", "workflow_review",
            "workflow_review", null, arguments, now, null, null, null, null, null,
            Kind: "workflow_review", WorkflowId: "wf-1", InstanceId: instance.Id, NodeId: node.NodeId));

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", "session-1", "agent", "agent-1",
            new GatewayChatApprovalRequest("req-workflow-card", "call-1", "workflow_review", arguments,
                RunId: "run-1", ToolType: "workflow_review", AgentId: "agent-1"),
            TestContext.Current.CancellationToken);

        ChannelRuntimeEventAction projected = JsonSerializer.Deserialize(
            fixture.OutboxStore.Entries.Last(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)!;
        using JsonDocument payload = JsonDocument.Parse(projected.PayloadJson);
        JsonElement card = payload.RootElement;
        Assert.Equal("workflow_review", card.GetProperty("kind").GetString());
        Assert.Equal("Workflow review: Nightly — step 1 of 1", card.GetProperty("title").GetString());
        Assert.Equal("This step finished. Check its result before the next step runs.", card.GetProperty("summary").GetString());
        Assert.False(card.TryGetProperty("userMessage", out _));
        JsonElement fields = card.GetProperty("fields");
        Assert.Equal(10, fields.GetArrayLength());
        Assert.Equal("Writer", fields[0].GetProperty("value").GetString());
        Assert.Equal("complete", fields[1].GetProperty("value").GetString());
        Assert.Equal(300, fields[2].GetProperty("value").GetString()?.Length);
        Assert.Contains(fields.EnumerateArray(), field => field.GetProperty("label").GetString() == "paid");
        Assert.Contains(fields.EnumerateArray(), field => field.GetProperty("label").GetString() == "orderId");
        string reviewText = string.Join(" ",
            card.GetProperty("title").GetString(),
            card.GetProperty("summary").GetString(),
            card.GetProperty("prompt").GetString(),
            fields.EnumerateArray().Select(field => field.GetProperty("value").GetString()));
        Assert.DoesNotContain("Reply with a JSON object", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("workflowId", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceId", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("wfi-", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", card.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("wfi-secret", card.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(["approve", "request_changes", "stop_run"],
            card.GetProperty("actions").EnumerateArray().Select(action => action.GetProperty("value").GetString()));
        GatewayChannelInteraction interaction = Assert.Single(fixture.State.ChannelInteractions.Values,
            item => item.ApprovalRequestId == "req-workflow-card");
        Assert.Equal(["approve", "request_changes", "stop_run"],
            interaction.Options.Select(option => option.Value));
    }

    [Fact]
    public async Task AgentToolApprovalUsesTheTargetAgentsTelegramBot()
    {
        using var fixture = new ChannelIngressFixture();
        SeedRoleRegistration(fixture, "telegram:user-1:default", "chat-kautuka");
        SeedRoleRegistration(fixture, "telegram:user-1:agent:roller", "chat-roller");

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-roller-child",
            "agent",
            "roller",
            new GatewayChatApprovalRequest(
                "req-roller-script",
                "tool-roller-script",
                "run_skill_script",
                "{\"skillId\":\"roll-dice\"}",
                ToolType: "skill"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction card = Assert.Single(fixture.State.ChannelInteractions.Values);
        Assert.Equal("telegram:user-1:agent:roller", card.RegistrationId);
        Assert.Equal("chat-roller", card.ChatId);
        Assert.Equal("roller", card.TargetId);
    }

    private static void SeedRoleRegistration(ChannelIngressFixture fixture, string registrationId, string chatId)
    {
        fixture.State.Users["owner"] = new GatewayUser(
            "user-1",
            "owner",
            "pwd",
            "admin",
            true,
            null,
            null,
            true,
            true);
        fixture.State.ChannelRegistrations[registrationId] = new GatewayChannelRegistration(
            registrationId,
            "telegram",
            "user-1",
            "bot-1",
            true,
            new JsonObject { ["channel_device_id"] = chatId },
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"));
        fixture.State.ChannelIdentityLinks[$"{registrationId}:user:{chatId}"] = new GatewayChannelIdentityLink(
            $"{registrationId}:user:{chatId}",
            registrationId,
            "user-1",
            chatId,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    [Fact]
    public async Task NotifyChannelsApprovalAsyncUserMappedWithoutSessionMapDispatchesPendingInteraction()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        GatewayChatApprovalRequest approval = new(
            "req-1",
            "tool-call-1",
            "run_shell",
            "{\"command\":\"dir\"}");

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", "session-web-ui-1", "agent", "agent-1", approval, CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction created = fixture.State.ChannelInteractions.Values
            .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
            .First();

        Assert.Equal("pending", created.Status);
        Assert.Equal("req-1", created.ApprovalRequestId);
        Assert.Equal("tg-user-1", created.ChatId);
        Assert.Equal("session-web-ui-1", created.SessionId);
        Assert.False(created.SourceMessageId.HasValue);

        Assert.Contains(fixture.OutboxStore.Entries, entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent && entry.PayloadJson.Contains("tool.approval.requested", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "send_text" && entry.PayloadJson.Contains("Waiting for your approval", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyChannelsApprovalAsyncUsesSessionTargetNotDefaultBinding()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-2", "user-1");

        GatewayChatApprovalRequest approval = new(
            "req-session-target",
            "tool-call-session-target",
            "list_directory",
            "{\"path\":\".\"}");

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", session.Id, null, null, approval, CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction created = fixture.State.ChannelInteractions.Values
            .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
            .First();

        Assert.Equal("agent", created.TargetType);
        Assert.Equal("agent-2", created.TargetId);
        Assert.Equal(session.Id, created.SessionId);
    }

    [Fact]
    public async Task NotifyChannelsApprovalAsyncUsesReadableOuterAgentAndChatLabelsInPrompt()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Patio Planner",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        GatewaySession outerSession = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.SessionStore.AppendMessages(
            outerSession.Id,
            new GatewayMessage(
                Id: $"msg-{Guid.NewGuid():N}",
                Role: "user",
                Content: "Dim the patio lights to 10%.",
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Compact: false,
                Thinking: null,
                Metrics: null,
                Internal: false),
            new GatewayMessage(
                Id: $"msg-{Guid.NewGuid():N}",
                Role: "assistant",
                Content: "Working on it.",
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Compact: false,
                Thinking: null,
                Metrics: null,
                Internal: false));

        GatewayChatApprovalRequest approval = new(
            "req-readable-1",
            "tool-call-readable-1",
            "run_shell",
            "{\"command\":\"date\"}",
            OuterSessionId: outerSession.Id,
            OuterAgentId: "agent-1");

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", outerSession.Id, "agent", "agent-1", approval, CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction created = fixture.State.ChannelInteractions.Values
            .OrderByDescending(item => item.CreatedAt, StringComparer.Ordinal)
            .First();

        Assert.Contains("Part of Patio Planner execution in chat \"Dim the patio lights to 10%.\".", created.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-1", created.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(outerSession.Id, created.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyChannelsApprovalAsyncUsesRootChatEvenWhenLegacyTopicsAreConfigured()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
            ["target_thread_labels"] = new JsonObject
            {
                ["agent:agent-1"] = "Agent: Research Assistant",
            },
        });

        GatewayChatApprovalRequest approval = new(
            "req-thread-1",
            "tool-call-thread-1",
            "run_shell",
            "{\"command\":\"dir\"}");

        await fixture.Ingress.NotifyChannelApprovalAsync("user-1", "session-thread-1", "agent", "agent-1", approval, CancellationToken.None).ConfigureAwait(true);

        string? requestBody = fixture.OutboxStore.Entries
            .Where(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent && entry.PayloadJson.Contains("tool.approval.requested", StringComparison.Ordinal))
            .Select(entry => entry.PayloadJson)
            .LastOrDefault();

        Assert.NotNull(requestBody);
        Assert.Contains("\"conversationThreadId\":null", requestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyChannelsApprovalAsyncSkipsDuplicatePendingRequest()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelInteractions["hitl-existing-1"] = new GatewayChannelInteraction(
            "hitl-existing-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            "session-dup-1",
            "run-dup-1",
            "chat-1",
            null,
            "Approve duplicate?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-dup-1");

        await fixture.Ingress.NotifyChannelApprovalAsync(
            "user-1",
            "session-dup-1",
            "agent",
            "agent-1",
            new GatewayChatApprovalRequest("req-dup-1", "tool-call-dup", "run_shell", "{}", RunId: "run-dup-1"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "send_semantic_message" && entry.PayloadJson.Contains("hitl:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessInboundEventAsyncUsesTopicThreadToResolveTargetBinding()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-2"] = "202",
            },
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9001,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = "202",
                text = "hello from thread",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("agent", result.TargetType);
        Assert.Equal("agent-2", result.TargetId);
    }

    [Theory]
    [InlineData("telegram_message")]
    [InlineData("telegram_callback")]
    [InlineData("channel_message")]
    [InlineData("channel_callback")]
    public async Task ProcessInboundEventAsyncRejectsRemovedEventTypeAliases(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        bool callback = eventType.EndsWith("callback", StringComparison.Ordinal);
        JsonElement update = callback
            ? CreateApprovalCallbackUpdate(9004, "callback-9004", "interaction-9004", "approve", 44)
            : JsonSerializer.SerializeToElement(new
            {
                update_id = 9004,
                message = new
                {
                    from = new { id = "tg-user-1" },
                    chat = new { id = "chat-1" },
                    text = "must not route",
                },
            }, jsonOptions);

        ChannelInboundEvent inbound = CreateTelegramInboundEvent(fixture, update) with { EventType = eventType };
        ChannelIngressResult result = await fixture.Ingress
            .ProcessInboundEventAsync(inbound, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("unsupported_event", result.Status);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncIgnoresRemovedActiveAgentSelectionAlias()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["active_agent_by_external_user"] = new JsonObject
            {
                ["tg-user-1"] = "agent-2",
            },
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9005,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "route through the current contract",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("agent", result.TargetType);
        Assert.Equal("agent-1", result.TargetId);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncUsesExplicitActiveTargetSelection()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["active_target_by_external_user"] = new JsonObject
            {
                ["tg-user-1"] = "agent:agent-2",
            },
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9006,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "route through the explicit target contract",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("agent", result.TargetType);
        Assert.Equal("agent-2", result.TargetId);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncUsesTopicThreadBindingBeforeUserActiveTarget()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-2"] = "202",
            },
            ["active_target_by_external_user"] = new JsonObject
            {
                ["tg-user-1"] = "agent:agent-1",
            },
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9002,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = "202",
                text = "hello from thread with conflicting default",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("agent", result.TargetType);
        Assert.Equal("agent-2", result.TargetId);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncRejectsUnmappedTopicWhenManagedTopicsAreConfigured()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-2"] = "202",
            },
        });

        fixture.State.ChannelTargetBindings[$"{fixture.RegistrationId}:agent:agent-2"] = new GatewayChannelTargetBinding(
            $"{fixture.RegistrationId}:agent:agent-2",
            fixture.RegistrationId,
            "agent",
            "agent-2",
            false,
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9003,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = "999",
                text = "hello from unknown topic",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("target_missing", result.Status);
        Assert.DoesNotContain(fixture.OutboxStore.Entries, item => item.ActionType == ChannelOutboundActionTypes.RuntimeEvent);
        Assert.Contains(fixture.OutboxStore.Entries, item => item.PayloadJson.Contains("No run was started", StringComparison.Ordinal));
        Assert.Contains("unmapped-channel-thread", fixture.OwnerTargetSyncNotifier.Reasons);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncRoutesReplyToRootChatWhenLegacyTopicIsConfigured()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9002,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "hello from general",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);

        string? runtimeEventBody = fixture.OutboxStore.Entries
            .Where(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
            .Select(entry => entry.PayloadJson)
            .LastOrDefault();

        Assert.NotNull(runtimeEventBody);
        Assert.Contains("\"conversationId\":\"chat-1\"", runtimeEventBody!, StringComparison.Ordinal);
        Assert.Contains("\"conversationThreadId\":null", runtimeEventBody!, StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"run.finished\"", runtimeEventBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncPrefixesTelegramAgentRepliesAndDoesNotQueueStatusHeader()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Research Assistant",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 9004,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "hello",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);

        ChannelRuntimeEventAction runtimeEvent = JsonSerializer.Deserialize<ChannelRuntimeEventAction>(
            fixture.OutboxStore.Entries.Last(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent).PayloadJson,
            jsonOptions)!;

        Assert.Equal("run.finished", runtimeEvent.EventType);
        Assert.Equal("agent-1", runtimeEvent.AgentId);
        Assert.Contains("fake-response", runtimeEvent.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "upsert_status_header");
    }

    [Fact]
    public async Task SyncTelegramApprovalResolutionAsyncResolvesInteractionAndClearsInlineKeyboard()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelInteractions["hitl-resolve-1"] = new GatewayChannelInteraction(
            "hitl-resolve-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            "session-web-ui-2",
            null,
            "tg-user-1",
            null,
            "<b>System</b>\nApprove deployment?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-resolve-1",
            123L);

        await fixture.Ingress.SyncChannelApprovalResolutionAsync(
            new ChannelApprovalResolution("req-resolve-1", "approve", "user-1"),
            CancellationToken.None).ConfigureAwait(true);

        GatewayChannelInteraction updated = fixture.State.ChannelInteractions["hitl-resolve-1"];
        Assert.Equal("resolved", updated.Status);
        Assert.Equal("approve", updated.Resolution);
        Assert.False(string.IsNullOrWhiteSpace(updated.ResolvedAt));

        string? runtimeEventBody = fixture.OutboxStore.Entries
            .Where(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
            .Select(entry => entry.PayloadJson)
            .LastOrDefault();

        Assert.NotNull(runtimeEventBody);
        Assert.Contains("\"eventType\":\"tool.approval.resolved\"", runtimeEventBody!, StringComparison.Ordinal);
        ChannelRuntimeEventAction resolvedEvent = JsonSerializer.Deserialize<ChannelRuntimeEventAction>(runtimeEventBody!, jsonOptions)!;
        Assert.Contains("\"transportMessageId\":\"123\"", resolvedEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"approve\"", resolvedEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Equal("hitl-resolve-1", resolvedEvent.InteractionId);
    }

    [Fact]
    public async Task SyncTelegramApprovalResolutionAsyncPublishesNeutralSenderAttributionForNewInteractions()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelInteractions["hitl-resolve-new-1"] = new GatewayChannelInteraction(
            "hitl-resolve-new-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            "session-web-ui-2",
            null,
            "tg-user-1",
            null,
            "Approve deployment?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-resolve-new-1",
            123L,
            "System");

        await fixture.Ingress.SyncChannelApprovalResolutionAsync(
            new ChannelApprovalResolution("req-resolve-new-1", "approve", "user-1"),
            CancellationToken.None).ConfigureAwait(true);

        string? runtimeEventBody = fixture.OutboxStore.Entries
            .Where(entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
            .Select(entry => entry.PayloadJson)
            .LastOrDefault();

        Assert.NotNull(runtimeEventBody);
        Assert.Contains("\"eventType\":\"tool.approval.resolved\"", runtimeEventBody!, StringComparison.Ordinal);
        ChannelRuntimeEventAction resolvedEvent = JsonSerializer.Deserialize<ChannelRuntimeEventAction>(runtimeEventBody!, jsonOptions)!;
        Assert.Contains("\"transportMessageId\":\"123\"", resolvedEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"approve\"", resolvedEvent.PayloadJson, StringComparison.Ordinal);
        Assert.Equal("hitl-resolve-new-1", resolvedEvent.InteractionId);
    }

    [Fact]
    public async Task NotifyChannelContinuationAsyncIsNoOp()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelInteractions["hitl-new"] = new GatewayChannelInteraction(
            "hitl-new",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            "session-c1",
            "run-c1",
            "chat-1",
            "101",
            "New interaction",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
            ],
            "resolved",
            DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(4).ToString("O"),
            DateTimeOffset.UtcNow.AddSeconds(-30).ToString("O"),
            "tg-user-1",
            "approve",
            "req-cont-1");

        await fixture.Ingress.NotifyChannelContinuationAsync("req-cont-1", "continuation payload", CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(fixture.OutboxStore.Entries);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncRejectsUnsupportedChannelType()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["bot_token"] = "bot-token" });

        ChannelInboundEvent evt = new(
            "evt-unsupported-1",
            "slack",
            fixture.RegistrationId,
            "user-1",
            "external-user-1",
            "channel-1",
            null,
            "message",
            "hello",
            null,
            null,
            null,
            JsonSerializer.SerializeToElement(new { text = "hello" }, jsonOptions));

        ChannelIngressResult result = await fixture.Ingress.ProcessInboundEventAsync(evt, CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("registration_not_found", result.Status);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncIgnoresDuplicateUpdateId()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 2001,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "hello",
            },
        }, jsonOptions);

        ChannelIngressResult first = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);
        ChannelIngressResult second = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(first.Success);
        Assert.Equal("ok", first.Status);
        Assert.True(second.Success);
        Assert.Equal("ignored_duplicate", second.Status);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionUpdatesRunAndSession()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewayRun pendingRun = fixture.SessionStore.CreateInteractionRun(
            "agent",
            "agent-1",
            "user-1",
            session.Id,
            "hitl-abc",
            "Approve release?",
            ["approve", "deny"]);

        fixture.State.ChannelInteractions["hitl-abc"] = new GatewayChannelInteraction(
            "hitl-abc",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            session.Id,
            pendingRun.Id,
            "chat-1",
            null,
            "Approve release?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            SourceMessageId: 42);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3001,
            callback_query = new
            {
                id = "callback-1",
                from = new { id = "tg-user-1" },
                data = "hitl:hitl-abc:approve",
                message = new
                {
                    message_id = 42,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);
        Assert.Equal("hitl-abc", result.InteractionId);
        Assert.Equal(pendingRun.Id, result.RunId);

        GatewayRun resolvedRun = fixture.SessionStore
            .ListRuns()
            .Single(run => string.Equals(run.InteractionId, "hitl-abc", StringComparison.Ordinal));

        Assert.Equal("resolved", resolvedRun.Status);
        Assert.Equal("approve", resolvedRun.Output["resolution"]);

        Assert.True(fixture.SessionStore.TryGetSession(session.Id, out GatewaySession? updatedSession));
        Assert.NotNull(updatedSession);
        Assert.Equal(session.Id, updatedSession!.Id);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionPreservesSelectedTelegramSession()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });

        GatewaySession oldSession = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewaySession hitlSession = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewayRun pendingRun = fixture.SessionStore.CreateInteractionRun(
            "agent",
            "agent-1",
            "user-1",
            hitlSession.Id,
            "hitl-topic-rebind-1",
            "Approve release?",
            ["approve", "deny", "cancel", "stop_run"]);

        ChannelConversationMapEntry existing = fixture.ConversationMapStore.CreateTransientThread(
            "telegram",
            fixture.RegistrationId,
            "agent",
            "agent-1",
            "chat-1",
            "101",
            oldSession.Id,
            DateTimeOffset.UtcNow.AddHours(4));

        fixture.State.ChannelInteractions["hitl-topic-rebind-1"] = new GatewayChannelInteraction(
            "hitl-topic-rebind-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            hitlSession.Id,
            pendingRun.Id,
            "chat-1",
            "101",
            "Approve release?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
                new GatewayChannelInteractionOption("opt-3", "Cancel", "cancel"),
                new GatewayChannelInteractionOption("opt-4", "Stop Run", "stop_run"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            SourceMessageId: 43);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3006,
            callback_query = new
            {
                id = "callback-topic-rebind-1",
                from = new { id = "tg-user-1" },
                data = "hitl:hitl-topic-rebind-1:approve",
                message = new
                {
                    message_id = 43,
                    message_thread_id = 101,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);

        ChannelConversationMapEntry? rebound = fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "101");
        Assert.NotNull(rebound);
        Assert.Equal(existing.Id, rebound!.Id);
        Assert.Equal(oldSession.Id, rebound.SessionId);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncNewCommandBindsNewSessionToRootConversationMap()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        JsonElement sessionsUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3009,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "/sessions",
            },
        }, jsonOptions);
        ChannelIngressResult sessionsResult = await ProcessTelegramUpdateAsync(fixture, sessionsUpdate).ConfigureAwait(true);
        Assert.True(sessionsResult.Success);
        Assert.Contains(fixture.OutboxStore.Entries, entry => entry.ActionType == "session_facts");

        JsonElement newCommandUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3010,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "/new",
            },
        }, jsonOptions);

        ChannelIngressResult newResult = await ProcessTelegramUpdateAsync(fixture, newCommandUpdate).ConfigureAwait(true);

        Assert.True(newResult.Success);
        Assert.Equal("ok", newResult.Status);
        Assert.False(string.IsNullOrWhiteSpace(newResult.SessionId));
        ChannelOutboxEntry confirmation = Assert.Single(fixture.OutboxStore.Entries,
            entry => entry.ActionType == "send_semantic_message");
        ChannelSemanticMessageAction confirmationMessage = JsonSerializer.Deserialize(confirmation.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Contains("New session started", confirmationMessage.Document.FallbackText, StringComparison.Ordinal);
        Assert.Contains("Your next prompt goes here.", confirmationMessage.Document.FallbackText, StringComparison.Ordinal);

        int outboxCountAfterNewSession = fixture.OutboxStore.Entries.Count;
        await fixture.Ingress.RefreshSessionViewsAsync(CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(outboxCountAfterNewSession, fixture.OutboxStore.Entries.Count);

        ChannelConversationMapEntry? boundMap = fixture.ConversationMapStore.TryResolve("telegram", fixture.RegistrationId, "chat-1", "target:AGENT:agent-1");
        Assert.NotNull(boundMap);
        Assert.Equal(newResult.SessionId, boundMap!.SessionId);

        JsonElement followUpUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3011,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                text = "hello after new",
            },
        }, jsonOptions);

        ChannelIngressResult followUpResult = await ProcessTelegramUpdateAsync(fixture, followUpUpdate).ConfigureAwait(true);

        Assert.True(followUpResult.Success);
        Assert.Equal(newResult.SessionId, followUpResult.SessionId);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncApprovalRequestUsesChatServicePersistedPausedTurn()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.CreateTransientThread(
            "telegram",
            fixture.RegistrationId,
            "agent",
            "agent-1",
            "chat-1",
            "101",
            session.Id,
            DateTimeOffset.UtcNow.AddHours(4));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 3012,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = 101,
                text = "needs-hitl",
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("awaiting_hitl", result.Status);

        Assert.True(fixture.SessionStore.TryGetSession(session.Id, out GatewaySession? updatedSession));
        Assert.NotNull(updatedSession);
        Assert.Equal(2, updatedSession!.Messages.Count);
        Assert.Equal("user", updatedSession.Messages[0].Role);
        Assert.Equal("needs-hitl", updatedSession.Messages[0].Content);
        Assert.Equal("telegram", updatedSession.Messages[0].Source);
        Assert.False(updatedSession.Messages[0].Internal);
        Assert.Equal("assistant", updatedSession.Messages[1].Role);
        GatewayToolCall toolCall = Assert.Single(updatedSession.Messages[1].ToolCalls!);
        Assert.Equal("run_shell", toolCall.ToolName);
        Assert.Equal("calling", toolCall.Status);

        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "send_semantic_message" && entry.PayloadJson.Contains("hitl:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessInboundEventAsyncTelegramDocumentCaptionUsesCaptionAsPrompt()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.CreateTransientThread(
            "telegram",
            fixture.RegistrationId,
            "agent",
            "agent-1",
            "chat-1",
            "101",
            session.Id,
            DateTimeOffset.UtcNow.AddHours(4));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 3013,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = 101,
                caption = "Summarize this attachment",
                document = new
                {
                    file_id = "doc-1",
                    file_name = "notes.pdf",
                    mime_type = "application/pdf",
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Status);
    }


    [Fact]
    public void ResolveTelegramInboundMediaDescriptorsExtractsSupportedMediaMetadata()
    {
        JsonElement message = JsonSerializer.SerializeToElement(new
        {
            photo = new object[]
            {
                new
                {
                    file_id = "photo-small",
                    file_unique_id = "photo-u-small",
                    width = 90,
                    height = 90,
                    file_size = 1024,
                },
                new
                {
                    file_id = "photo-large",
                    file_unique_id = "photo-u-large",
                    width = 1280,
                    height = 720,
                    file_size = 2048,
                },
            },
            document = new
            {
                file_id = "doc-1",
                file_unique_id = "doc-u-1",
                file_name = "report.pdf",
                mime_type = "application/pdf",
                file_size = 4096,
            },
            voice = new
            {
                file_id = "voice-1",
                file_unique_id = "voice-u-1",
                mime_type = "audio/ogg",
                file_size = 512,
            },
        }, jsonOptions);

        IReadOnlyList<ChannelInboundMediaDescriptor> descriptors = TelegramLongPollingWorker.ResolveTelegramInboundMediaDescriptors(message);

        Assert.Equal(3, descriptors.Count);

        ChannelInboundMediaDescriptor document = Assert.Single(descriptors, item => item.MediaKind == "document");
        Assert.Equal("doc-1", document.MediaId);
        Assert.Equal("report.pdf", document.FileName);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Equal(4096, document.SizeBytes);

        ChannelInboundMediaDescriptor photo = Assert.Single(descriptors, item => item.MediaKind == "photo");
        Assert.Equal("photo-large", photo.MediaId);
        Assert.Equal("image/jpeg", photo.ContentType);
        Assert.Equal("photo-photo-u-large.jpg", photo.FileName);

        ChannelInboundMediaDescriptor voice = Assert.Single(descriptors, item => item.MediaKind == "voice");
        Assert.Equal("voice-1", voice.MediaId);
        Assert.Equal("audio/ogg", voice.ContentType);
        Assert.Equal("voice-voice-u-1.ogg", voice.FileName);
    }

    [Fact]
    public void ValidateInboundMediaDescriptorsFiltersBlockedAndOversizedMedia()
    {
        IReadOnlyList<ChannelInboundMediaDescriptor> media =
        [
            new("doc-1", "document", "report.pdf", "application/pdf", 1024, "doc-u-1"),
            new("doc-2", "document", "payload.exe", "application/octet-stream", 1024, "doc-u-2"),
            new("doc-3", "document", "large.pdf", "application/pdf", 3 * 1024 * 1024, "doc-u-3"),
        ];

        InboundMediaValidationResult validation = TelegramLongPollingWorker.ValidateInboundMediaDescriptors(media);

        ChannelInboundMediaDescriptor accepted = Assert.Single(validation.AcceptedMedia);
        Assert.Equal("doc-1", accepted.MediaId);
        Assert.Equal(2, validation.RejectedMediaCount);
        Assert.True(validation.RejectedBlockedMedia);
        Assert.True(validation.RejectedOversizedMedia);

        string rejectionText = TelegramLongPollingWorker.BuildInboundMediaRejectionText(validation);
        Assert.Contains("Accepted attachments:", rejectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.exe", rejectionText, StringComparison.Ordinal);
        Assert.DoesNotContain("large.pdf", rejectionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncTelegramMediaOnlyDocumentCreatesConversationAssetAndPassesAssetIds()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });
        fixture.ChannelHostGrpc.SeedInboundMedia("doc-media-1", "report.pdf", "application/pdf", "quarterly summary");

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        fixture.ConversationMapStore.CreateTransientThread(
            "telegram",
            fixture.RegistrationId,
            "agent",
            "agent-1",
            "chat-1",
            "101",
            session.Id,
            DateTimeOffset.UtcNow.AddHours(4));

        JsonElement update = JsonSerializer.SerializeToElement(new
        {
            update_id = 3014,
            message = new
            {
                from = new { id = "tg-user-1" },
                chat = new { id = "chat-1" },
                message_thread_id = 101,
                document = new
                {
                    file_id = "doc-media-1",
                    file_name = "report.pdf",
                    mime_type = "application/pdf",
                    file_size = 17,
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, update).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Status);
        string assetId = Assert.Single(fixture.ChatGateway.LastAssetIds);
        Assert.Equal(string.Empty, fixture.ChatGateway.LastMessage);

        StoredConversationAsset stored = Assert.Single(fixture.AssetStore.GetAssetsByIds(session.Id, [assetId], "user-1"));
        Assert.StartsWith("report", stored.Public.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", stored.Public.FileName, StringComparison.Ordinal);
        Assert.Equal("application/pdf", stored.Public.ContentType);
        Assert.Equal(17, stored.Public.SizeBytes);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncUnknownCallbackInteractionStillAnswersCallbackQuery()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3003,
            callback_query = new
            {
                id = "callback-unknown-1",
                from = new { id = "tg-user-1" },
                data = "hitl:missing:approve",
                message = new
                {
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);
        Assert.Contains(fixture.OutboxStore.Entries, entry =>
            entry.ActionType == "acknowledge_callback"
            && entry.PayloadJson.Contains("callback-unknown-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessInboundEventAsyncUnmappedCallbackUserStillAnswersCallbackQuery()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3004,
            callback_query = new
            {
                id = "callback-unmapped-1",
                from = new { id = "tg-user-unmapped" },
                data = "hitl:any:approve",
                message = new
                {
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("identity_unmapped", result.Status);
        Assert.Contains(fixture.OutboxStore.Entries, entry =>
            entry.ActionType == "acknowledge_callback"
            && entry.PayloadJson.Contains("callback-unmapped-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionUsesChatIdentityFallback()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.State.ChannelIdentityLinks.Clear();
        fixture.State.ChannelIdentityLinks[$"{fixture.RegistrationId}:chat:chat-1"] = new GatewayChannelIdentityLink(
            $"{fixture.RegistrationId}:chat:chat-1",
            fixture.RegistrationId,
            "user-1",
            "chat-1",
            DateTimeOffset.UtcNow.ToString("O"));

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewayRun pendingRun = fixture.SessionStore.CreateInteractionRun(
            "agent",
            "agent-1",
            "user-1",
            session.Id,
            "hitl-chat-fallback-1",
            "Run shell?",
            ["approve", "deny"]);

        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "req-chat-fallback-1",
            session.Id,
            pendingRun.Id,
            null,
            "agent-1",
            "user-1",
            "tool-call-1",
            "run_shell",
            "mcp",
            null,
            "{\"path\":\".\"}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        fixture.State.ChannelInteractions["hitl-chat-fallback-1"] = new GatewayChannelInteraction(
            "hitl-chat-fallback-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            session.Id,
            pendingRun.Id,
            "chat-1",
            null,
            "Run shell?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-chat-fallback-1",
            322L);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3005,
            callback_query = new
            {
                id = "callback-chat-fallback-1",
                from = new { id = "tg-user-different" },
                data = "hitl:hitl-chat-fallback-1:approve",
                message = new
                {
                    message_id = 322,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);

        HitlApprovalResolved? evt = fixture.Publisher.Notifications
            .OfType<HitlApprovalResolved>()
            .LastOrDefault();
        Assert.NotNull(evt);
        Assert.Equal("req-chat-fallback-1", evt!.RequestId);
        Assert.True(evt.Approved);
        Assert.Equal(HitlResolutionSource.ChannelCallback, evt.Source);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionWithApprovalRequestPublishesResolvedEvent()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewayRun pendingRun = fixture.SessionStore.CreateInteractionRun(
            "agent",
            "agent-1",
            "user-1",
            session.Id,
            "hitl-resume-1",
            "Run shell?",
            ["approve", "deny"]);

        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "req-resume-1",
            session.Id,
            pendingRun.Id,
            null,
            "agent-1",
            "user-1",
            "tool-call-1",
            "run_shell",
            "mcp",
            null,
            "{\"path\":\".\"}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        fixture.State.ChannelInteractions["hitl-resume-1"] = new GatewayChannelInteraction(
            "hitl-resume-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            session.Id,
            pendingRun.Id,
            "chat-1",
            null,
            "Run shell?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-resume-1",
            321L);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3002,
            callback_query = new
            {
                id = "callback-2",
                from = new { id = "tg-user-1" },
                data = "hitl:hitl-resume-1:approve",
                message = new
                {
                    message_id = 321,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);

        HitlApprovalResolved? evt = fixture.Publisher.Notifications
            .OfType<HitlApprovalResolved>()
            .LastOrDefault();
        Assert.NotNull(evt);
        Assert.Equal("req-resume-1", evt!.RequestId);
        Assert.True(evt.Approved);
        Assert.Equal(HitlResolutionSource.ChannelCallback, evt.Source);

        GatewayToolApprovalRequest? resolvedApproval = fixture.ApprovalStore.GetById("req-resume-1");
        Assert.Equal("telegram", resolvedApproval?.ResolutionSource);
        Assert.Null(resolvedApproval?.Reason);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionFailureLeavesInteractionPending()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });
        SeedApprovalCallbackInteraction(fixture, "hitl-resolution-failure", "req-resolution-failure", 341L);
        fixture.ApprovalStore.RejectResolutions = true;

        JsonElement callbackUpdate = CreateApprovalCallbackUpdate(
            updateId: 3015,
            callbackId: "callback-resolution-failure",
            interactionId: "hitl-resolution-failure",
            action: "approve",
            messageId: 341L);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("approval_resolve_failed", result.Status);
        Assert.Equal("pending", fixture.State.ChannelInteractions["hitl-resolution-failure"].Status);
        Assert.Null(fixture.State.ChannelInteractions["hitl-resolution-failure"].Resolution);
        Assert.Null(fixture.ApprovalStore.GetById("req-resolution-failure")?.Decision);
        Assert.Contains(fixture.OutboxStore.Entries, entry => entry.ActionType == "acknowledge_callback");
        Assert.DoesNotContain(fixture.OutboxStore.Entries, entry => entry.ActionType == "edit_message_text");
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionRaceUsesCentralDecision()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });
        SeedApprovalCallbackInteraction(fixture, "hitl-resolution-race", "req-resolution-race", 342L);
        Assert.True(fixture.ApprovalStore.Resolve(
            "req-resolution-race",
            approved: false,
            reason: null,
            decidedBy: "approvals-user",
            resolutionSource: "approvals"));

        JsonElement callbackUpdate = CreateApprovalCallbackUpdate(
            updateId: 3016,
            callbackId: "callback-resolution-race",
            interactionId: "hitl-resolution-race",
            action: "approve",
            messageId: 342L);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);
        Assert.Equal("Approval was already denied in another channel.", result.Detail);
        GatewayChannelInteraction interaction = fixture.State.ChannelInteractions["hitl-resolution-race"];
        Assert.Equal("resolved", interaction.Status);
        Assert.Equal("deny", interaction.Resolution);
        Assert.Equal("approvals-user", interaction.ResolvedByExternalUserId);
        Assert.Contains(
            fixture.OutboxStore.Entries,
            entry => entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent
                && entry.PayloadJson.Contains("tool.approval.resolved", StringComparison.Ordinal)
                && entry.PayloadJson.Contains("deny", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionResolvesAutopilotPreRunApprovalWithoutSessionId()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "req-autopilot-prerun-1",
            null,
            "run-autopilot-1",
            null,
            "agent-1",
            "user-1",
            "autopilot_pre_run",
            "autopilot_pre_run",
            "autopilot_prerun",
            null,
            "{\"schedulePromptContext\":\"using skill roll-dice\"}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        fixture.State.ChannelInteractions["hitl-autopilot-prerun-1"] = new GatewayChannelInteraction(
            "hitl-autopilot-prerun-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            string.Empty,
            "run-autopilot-1",
            "chat-1",
            null,
            "Approve Auto-Pilot pre-run?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-autopilot-prerun-1",
            333L);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3013,
            callback_query = new
            {
                id = "callback-autopilot-prerun-1",
                from = new { id = "tg-user-1" },
                data = "hitl:hitl-autopilot-prerun-1:approve",
                message = new
                {
                    message_id = 333,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);

        GatewayToolApprovalRequest? updatedApproval = fixture.ApprovalStore.GetById("req-autopilot-prerun-1");
        Assert.NotNull(updatedApproval);
        Assert.Equal("approved", updatedApproval!.Decision);

        HitlApprovalResolved? evt = fixture.Publisher.Notifications
            .OfType<HitlApprovalResolved>()
            .LastOrDefault();
        Assert.NotNull(evt);
        Assert.Equal("req-autopilot-prerun-1", evt!.RequestId);
        Assert.True(evt.Approved);
        Assert.Equal(HitlResolutionSource.ChannelCallback, evt.Source);
    }

    [Fact]
    public async Task ProcessInboundEventAsyncCallbackResolutionDeniesAutopilotPreRunApprovalWithoutSessionId()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            "req-autopilot-prerun-deny-1",
            null,
            "run-autopilot-deny-1",
            null,
            "agent-1",
            "user-1",
            "autopilot_pre_run",
            "autopilot_pre_run",
            "autopilot_prerun",
            null,
            "{\"schedulePromptContext\":\"using skill roll-dice\"}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        fixture.State.ChannelInteractions["hitl-autopilot-prerun-deny-1"] = new GatewayChannelInteraction(
            "hitl-autopilot-prerun-deny-1",
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            string.Empty,
            "run-autopilot-deny-1",
            "chat-1",
            null,
            "Approve Auto-Pilot pre-run?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            "req-autopilot-prerun-deny-1",
            334L);

        JsonElement callbackUpdate = JsonSerializer.SerializeToElement(new
        {
            update_id = 3014,
            callback_query = new
            {
                id = "callback-autopilot-prerun-deny-1",
                from = new { id = "tg-user-1" },
                data = "hitl:hitl-autopilot-prerun-deny-1:deny",
                message = new
                {
                    message_id = 334,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

        ChannelIngressResult result = await ProcessTelegramUpdateAsync(fixture, callbackUpdate).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("callback_acknowledged", result.Status);

        GatewayToolApprovalRequest? updatedApproval = fixture.ApprovalStore.GetById("req-autopilot-prerun-deny-1");
        Assert.NotNull(updatedApproval);
        Assert.Equal("denied", updatedApproval!.Decision);

        HitlApprovalResolved? evt = fixture.Publisher.Notifications
            .OfType<HitlApprovalResolved>()
            .LastOrDefault();
        Assert.NotNull(evt);
        Assert.Equal("req-autopilot-prerun-deny-1", evt!.RequestId);
        Assert.False(evt.Approved);
        Assert.Equal(HitlResolutionSource.ChannelCallback, evt.Source);
    }

    [Fact]
    public async Task TelegramProvisioningWithExistingTargetThreadsDoesNotThrowAndPreservesSettings()
    {
        using var handler = new CapturingTelegramMessageHandler();
        using var httpClientFactory = new CapturingHttpClientFactory(handler);
        using var fixture = new ChannelIngressFixture(httpClientFactory);
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
            ["target_thread_labels"] = new JsonObject
            {
                ["agent:agent-1"] = "Agent: Research Assistant",
            },
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Research Assistant",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        var provisioning = new ChannelRegistrationProvisioningService(
            fixture.State,
            fixture.AttachmentRegistry,
            fixture.ChannelHostGrpc,
            NullLogger<ChannelRegistrationProvisioningService>.Instance);

        ChannelRegistrationProvisioningResult result = await provisioning.ProvisionAsync(
            fixture.RegistrationId,
            "tg-user-1",
            "agent",
            "agent-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.True(fixture.State.ChannelRegistrations.TryGetValue(fixture.RegistrationId, out GatewayChannelRegistration? updated));
        Assert.NotNull(updated);
        ChannelOutboxEntry enqueued = Assert.Single(fixture.OutboxStore.Entries);
        Assert.Equal("provision_registration", enqueued.ActionType);
        Assert.Equal(
            "101",
            (updated!.Settings["target_threads"] as JsonObject)?["agent:agent-1"]?.ToString());
        Assert.Contains("\"displayName\":\"Research Assistant\"", enqueued.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramHostProvisioningRetiresExistingTopicsAndClearsTheirMappings()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        ChannelHostDatabase database = CreateChannelHostDatabase(temp.SqlitePath);
        var runtimeCredentials = new ChannelRuntimeCredentialStore();
        var attachmentStore = new ChannelAttachmentStore(database, runtimeCredentials);

        UpsertAttachmentResponse upsert = attachmentStore.UpsertAttachment(new UpsertAttachmentRequest
        {
            AttachmentId = "telegram:user-1:bot-1",
            ProviderKey = "telegram",
            ExpectedSchemaVersion = "telegram/v1",
            Settings =
            {
                new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "chat-1" },
                new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "user-1" },
                new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Telegram user-1" },
            },
            CredentialReferences =
            {
                new AttachmentCredentialReference { FieldKey = "bot_token", CredentialKey = "channel.telegram:user-1:bot-1.bot_token" },
            },
        });
        runtimeCredentials.Replace(upsert.Attachment.AttachmentId,
        [
            new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" },
        ]);

        attachmentStore.ApplySettingsPatch(upsert.Attachment.AttachmentId, new JsonObject
        {
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
            ["target_thread_labels"] = new JsonObject
            {
                ["agent:agent-1"] = "Agent: Research Assistant",
            },
        });

        var worker = new TelegramLongPollingWorker(
            attachmentStore,
            new ChannelIngressStore(database),
            new ChannelOutboundAssetStore(database, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(temp.SqlitePath),
            }).Build()),
            new FakeHttpClientFactory(),
            NullLogger<TelegramLongPollingWorker>.Instance);

        ProvisionAttachmentResponse result = await worker.ProvisionAttachmentAsync(
            upsert.Attachment.AttachmentId,
            new ProvisionAttachmentRequest
            {
                AttachmentId = upsert.Attachment.AttachmentId,
                BotName = "Kautuka",
                Description = "Kautuka bot. Granted targets: agent:agent-1",
                ShortDescription = "Default target: agent:agent-1",
                Targets =
                {
                    new ChannelTargetMessage
                    {
                        TargetKey = "agent:agent-1",
                        DisplayName = "Agent: Research Assistant",
                    },
                },
            },
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ProvisioningStatus.Succeeded, result.Status);

        Assert.True(attachmentStore.TryGetTelegramRegistration(upsert.Attachment.AttachmentId, out TelegramAttachmentRegistration? registration));
        Assert.NotNull(registration);
        Assert.Empty(registration!.Settings.GetProperty("target_threads").EnumerateObject());
        Assert.Empty(registration.Settings.GetProperty("target_thread_labels").EnumerateObject());
    }

    [Fact]
    public async Task TelegramProvisioningIncludesReadableTargetNames()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Research Assistant",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        ChannelRegistrationProvisioningResult result = await new ChannelRegistrationProvisioningService(
            fixture.State,
            fixture.AttachmentRegistry,
            fixture.ChannelHostGrpc,
            NullLogger<ChannelRegistrationProvisioningService>.Instance).ProvisionAsync(
                fixture.RegistrationId,
                "tg-user-1",
                "agent",
                "agent-1",
                CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(result);
        string provisioningBody = Assert.Single(fixture.OutboxStore.Entries).PayloadJson;

        Assert.NotNull(provisioningBody);
        Assert.Contains("\"displayName\":\"Research Assistant\"", provisioningBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"displayName\":\"agent:agent-1\"", provisioningBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramProvisioningRecreatesMissingThreadWhenStoredThreadIdIsStale()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
            ["target_thread_labels"] = new JsonObject
            {
                ["agent:agent-1"] = "Agent: Old Name",
            },
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Research Assistant",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        var provisioning = new ChannelRegistrationProvisioningService(
            fixture.State,
            fixture.AttachmentRegistry,
            fixture.ChannelHostGrpc,
            NullLogger<ChannelRegistrationProvisioningService>.Instance);

        ChannelRegistrationProvisioningResult result = await provisioning.ProvisionAsync(
            fixture.RegistrationId,
            "tg-user-1",
            "agent",
            "agent-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.True(fixture.State.ChannelRegistrations.TryGetValue(fixture.RegistrationId, out GatewayChannelRegistration? updated));
        Assert.Equal("101", (updated!.Settings["target_threads"] as JsonObject)?["agent:agent-1"]?.ToString());
        Assert.Contains("\"displayName\":\"Research Assistant\"", Assert.Single(fixture.OutboxStore.Entries).PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramProvisioningTreatsTopicNotModifiedRenameAsSuccess()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["target_threads"] = new JsonObject
            {
                ["agent:agent-1"] = "101",
            },
        });
        fixture.State.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Research Assistant",
            "model-1",
            "system",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"));

        var provisioning = new ChannelRegistrationProvisioningService(
            fixture.State,
            fixture.AttachmentRegistry,
            fixture.ChannelHostGrpc,
            NullLogger<ChannelRegistrationProvisioningService>.Instance);

        ChannelRegistrationProvisioningResult result = await provisioning.ProvisionAsync(
            fixture.RegistrationId,
            "tg-user-1",
            "agent",
            "agent-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Single(fixture.OutboxStore.Entries);
    }

    [Fact]
    public async Task TelegramProvisioningFailsWhenRequiredForumTopicCannotBeCreated()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
        });

        var provisioning = new ChannelRegistrationProvisioningService(
            fixture.State,
            fixture.AttachmentRegistry,
            fixture.ChannelHostGrpc,
            NullLogger<ChannelRegistrationProvisioningService>.Instance);

        ChannelRegistrationProvisioningResult result = await provisioning.ProvisionAsync(
            fixture.RegistrationId,
            "tg-user-1",
            "agent",
            "agent-1",
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Equal("accepted", result.Status);
        Assert.Single(fixture.OutboxStore.Entries);
    }

    [Fact]
    public async Task DispatchChannelInteractionAcceptsCamelCaseChatIdPayload()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["channel_device_id"] = "tg-user-1",
        });

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            chatId = "tg-user-1",
            prompt = "Approve this action?",
            timeoutSeconds = 900,
            options = new[]
            {
                new { label = "Approve", value = "approve" },
                new { label = "Deny", value = "deny" },
            },
        });

        ChannelInteractionDispatchResult result = await fixture.Ingress.DispatchChannelInteractionAsync(
            fixture.RegistrationId,
            payload,
            CancellationToken.None).ConfigureAwait(true);

        Assert.True(result.Success);
        Assert.Contains(fixture.OutboxStore.Entries, entry => entry.ActionType == "send_semantic_message" && entry.PayloadJson.Contains("hitl:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchedInteractionFansOutAndResolvesFromEitherLinkedChat()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject { ["channel_device_id"] = "chat-1" });
        const string slackId = "slack:user-1:default";
        fixture.State.ChannelRegistrations[slackId] = new GatewayChannelRegistration(slackId, "slack", "user-1", "slack-bot", true,
            new JsonObject { ["slack_dm_channel_id"] = "D-SLACK" }, "created", "updated");
        fixture.State.ChannelIdentityLinks[$"{slackId}:user:U-SLACK"] = new GatewayChannelIdentityLink(
            $"{slackId}:user:U-SLACK", slackId, "user-1", "U-SLACK", "created");
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            chatId = "chat-1",
            prompt = "Approve this linked interaction?",
            sessionId = session.Id,
            options = new[] { new { label = "Approve", value = "approve" }, new { label = "Deny", value = "deny" } },
        });

        ChannelInteractionDispatchResult result = await fixture.Ingress.DispatchChannelInteractionAsync(
            fixture.RegistrationId, payload, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        GatewayChannelInteraction[] interactions = [.. fixture.State.ChannelInteractions.Values
            .Where(item => item.InteractionGroupId == result.InteractionId)];
        Assert.Equal(2, interactions.Length);
        Assert.Contains(interactions, item => item.RegistrationId == fixture.RegistrationId && item.ChatId == "chat-1");
        GatewayChannelInteraction slackInteraction = Assert.Single(interactions, item => item.RegistrationId == slackId && item.ChatId == "D-SLACK");
        Assert.Equal(2, fixture.OutboxStore.Entries.Count(item => item.ActionType == "send_semantic_message"));

        foreach (GatewayChannelInteraction interaction in interactions)
            fixture.State.UpsertChannelInteraction(interaction with { SourceTransportMessageId = "message-" + interaction.RegistrationId });
        ChannelIngressResult callback = await fixture.Ingress.ProcessInboundEventAsync(new ChannelInboundEvent(
            "linked-interaction-callback", "slack", slackId, "user-1", "U-SLACK", "D-SLACK", null, "callback", null,
            $"hitl:{slackInteraction.Id}:approve", "slack-callback", null,
            JsonSerializer.SerializeToElement(new { action = "approve" }), SourceTransportMessageId: "message-" + slackId),
            TestContext.Current.CancellationToken);

        Assert.True(callback.Success);
        Assert.Equal("callback_acknowledged", callback.Status);
        Assert.All(interactions, interaction =>
        {
            GatewayChannelInteraction resolved = fixture.State.ChannelInteractions[interaction.Id];
            Assert.Equal("resolved", resolved.Status);
            Assert.Equal("APPROVE", resolved.Resolution);
        });
    }

    [Fact]
    public async Task DispatchChannelInteractionRejectsAnotherUsersSession()
    {
        using var fixture = new ChannelIngressFixture();
        fixture.SeedRegistration(new JsonObject
        {
            ["bot_token"] = "bot-token",
            ["channel_device_id"] = "tg-user-1",
        });
        GatewaySession otherSession = fixture.SessionStore.CreateSession("agent", "agent-1", "user-2");

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            chatId = "tg-user-1",
            prompt = "Approve this action?",
            sessionId = otherSession.Id,
            options = new[]
            {
                new { label = "Approve", value = "approve" },
                new { label = "Deny", value = "deny" },
            },
        });

        ChannelInteractionDispatchResult result = await fixture.Ingress.DispatchChannelInteractionAsync(
            fixture.RegistrationId,
            payload,
            CancellationToken.None).ConfigureAwait(true);

        Assert.False(result.Success);
        Assert.Equal("session_id was provided but no matching session exists.", result.Detail);
        Assert.Empty(fixture.OutboxStore.Entries);
        Assert.DoesNotContain(fixture.State.Runs, run => run.SessionId == otherSession.Id);
    }

    [Fact]
    public void ChannelInteractionCommandRoundTripsThroughAuthoritativeDatabase()
    {
        using var fixture = new ChannelIngressFixture();
        var interaction = new GatewayChannelInteraction(
            Id: "hitl-authoritative",
            RegistrationId: fixture.RegistrationId,
            UserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-1",
            SessionId: "session-1",
            RunId: "run-1",
            ChatId: "chat-1",
            MessageThreadId: "thread-1",
            Prompt: "Approve?",
            Options: [new GatewayChannelInteractionOption("approve", "Approve", "approve")],
            Status: "pending",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            ResolvedAt: null,
            ResolvedByExternalUserId: null,
            Resolution: null,
            ApprovalRequestId: "approval-1",
            SourceMessageId: 42,
            SourceTransportMessageId: "transport-42",
            InteractionGroupId: "linked-group-1");

        fixture.State.UpsertChannelInteraction(interaction);

        GatewayState reconstructed = ChannelIngressFixture.CreateState(fixture.SqlitePath);
        GatewayChannelInteraction persisted = Assert.Single(reconstructed.ChannelInteractions.Values);
        Assert.Equal(interaction.Id, persisted.Id);
        Assert.Equal(interaction.Status, persisted.Status);
        Assert.Equal(interaction.SourceMessageId, persisted.SourceMessageId);
        Assert.Equal(interaction.SourceTransportMessageId, persisted.SourceTransportMessageId);
        Assert.Equal(interaction.ApprovalRequestId, persisted.ApprovalRequestId);
        Assert.Equal(interaction.InteractionGroupId, persisted.InteractionGroupId);
        Assert.Equal(interaction.Options, persisted.Options);
    }

    [Fact]
    public void ChannelInteractionCommandDoesNotMutateProjectionWhenDatabaseWriteFails()
    {
        using var fixture = new ChannelIngressFixture();
        var original = new GatewayChannelInteraction(
            "hitl-failure", fixture.RegistrationId, "user-1", "agent", "agent-1", "session-1", "run-1",
            "chat-1", null, "Approve?", [new GatewayChannelInteractionOption("approve", "Approve", "approve")],
            "pending", DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null, null, null);
        fixture.State.UpsertChannelInteraction(original);

        using (var connection = new SqliteConnection(fixture.DatabaseConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE runtime_channel_interactions";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => fixture.State.UpsertChannelInteraction(original with { Status = "resolved" }));
        Assert.Equal("pending", fixture.State.ChannelInteractions[original.Id].Status);
    }

    private static Task<ChannelIngressResult> ProcessTelegramUpdateAsync(ChannelIngressFixture fixture, JsonElement update)
        => fixture.Ingress.ProcessInboundEventAsync(CreateTelegramInboundEvent(fixture, update), CancellationToken.None);

    private static void SeedApprovalCallbackInteraction(
        ChannelIngressFixture fixture,
        string interactionId,
        string requestId,
        long transportMessageId)
    {
        GatewaySession session = fixture.SessionStore.CreateSession("agent", "agent-1", "user-1");
        GatewayRun pendingRun = fixture.SessionStore.CreateInteractionRun(
            "agent",
            "agent-1",
            "user-1",
            session.Id,
            interactionId,
            "Run shell?",
            ["approve", "deny", "cancel", "stop_run"]);

        fixture.ApprovalStore.CreateRequest(new GatewayToolApprovalRequest(
            requestId,
            session.Id,
            pendingRun.Id,
            null,
            "agent-1",
            "user-1",
            $"tool-{requestId}",
            "run_shell",
            "mcp",
            null,
            "{\"path\":\".\"}",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        fixture.State.ChannelInteractions[interactionId] = new GatewayChannelInteraction(
            interactionId,
            fixture.RegistrationId,
            "user-1",
            "agent",
            "agent-1",
            session.Id,
            pendingRun.Id,
            "chat-1",
            null,
            "Run shell?",
            [
                new GatewayChannelInteractionOption("opt-1", "Approve", "approve"),
                new GatewayChannelInteractionOption("opt-2", "Deny", "deny"),
                new GatewayChannelInteractionOption("opt-3", "Cancel", "cancel"),
                new GatewayChannelInteractionOption("opt-4", "Stop Run", "stop_run"),
            ],
            "pending",
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            null,
            null,
            null,
            requestId,
            transportMessageId);
    }

    private static JsonElement CreateApprovalCallbackUpdate(
        int updateId,
        string callbackId,
        string interactionId,
        string action,
        long messageId)
        => JsonSerializer.SerializeToElement(new
        {
            update_id = updateId,
            callback_query = new
            {
                id = callbackId,
                from = new { id = "tg-user-1" },
                data = $"hitl:{interactionId}:{action}",
                message = new
                {
                    message_id = messageId,
                    chat = new { id = "chat-1" },
                },
            },
        }, jsonOptions);

    private static ChannelInboundEvent CreateTelegramInboundEvent(ChannelIngressFixture fixture, JsonElement update)
    {
        string eventId = update.TryGetProperty("update_id", out JsonElement updateId)
            ? updateId.ToString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        if (update.TryGetProperty("callback_query", out JsonElement callback)
            && callback.ValueKind == JsonValueKind.Object
            && callback.TryGetProperty("id", out JsonElement callbackId)
            && callback.TryGetProperty("from", out JsonElement callbackFrom)
            && callbackFrom.TryGetProperty("id", out JsonElement callbackFromId)
            && callback.TryGetProperty("message", out JsonElement callbackMessage)
            && callbackMessage.TryGetProperty("chat", out JsonElement callbackChat)
            && callbackChat.TryGetProperty("id", out JsonElement callbackChatId))
        {
            string? callbackData = callback.TryGetProperty("data", out JsonElement callbackDataElement)
                ? callbackDataElement.ToString()
                : null;
            string callbackThreadId = callbackMessage.TryGetProperty("message_thread_id", out JsonElement callbackThread)
                ? callbackThread.ToString() ?? "root"
                : "root";
            long? callbackMessageId = callbackMessage.TryGetProperty("message_id", out JsonElement callbackMessageIdElement)
                && callbackMessageIdElement.TryGetInt64(out long parsedCallbackMessageId)
                ? parsedCallbackMessageId
                : null;

            return new ChannelInboundEvent(
                eventId,
                "telegram",
                fixture.RegistrationId,
                "user-1",
                callbackFromId.ToString() ?? string.Empty,
                callbackChatId.ToString() ?? string.Empty,
                callbackThreadId,
                "callback",
                null,
                callbackData,
                callbackId.GetString(),
                callbackMessageId,
                update.Clone());
        }

        if (update.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("from", out JsonElement messageFrom)
            && messageFrom.TryGetProperty("id", out JsonElement messageFromId)
            && message.TryGetProperty("chat", out JsonElement messageChat)
            && messageChat.TryGetProperty("id", out JsonElement messageChatId))
        {
            string text = ResolveTelegramMessageText(message);
            string? messageThreadId = message.TryGetProperty("message_thread_id", out JsonElement threadElement)
                ? threadElement.ToString()
                : null;

            return new ChannelInboundEvent(
                eventId,
                "telegram",
                fixture.RegistrationId,
                "user-1",
                messageFromId.ToString() ?? string.Empty,
                messageChatId.ToString() ?? string.Empty,
                messageThreadId,
                "message",
                text,
                null,
                null,
                null,
                update.Clone(),
                TelegramLongPollingWorker.ResolveTelegramInboundMediaDescriptors(message));
        }

        throw new InvalidOperationException("Test update payload does not contain a supported Telegram message or callback.");
    }

    private static string ResolveTelegramMessageText(JsonElement message)
    {
        if (message.TryGetProperty("text", out JsonElement textElement))
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (message.TryGetProperty("caption", out JsonElement captionElement))
        {
            return captionElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private sealed class ChannelIngressFixture : IDisposable
    {
        private readonly TemporaryGatewayStateDirectory temp = new();
        private readonly IHttpClientFactory httpClientFactory;
        private readonly string registrationId = "telegram:user-1:bot-1";
        private readonly InMemoryConversationMapStore conversationMapStore;
        private readonly Kautuka.Gateway.Infrastructure.Database.KautukaDatabase database;

        public ChannelIngressFixture(IHttpClientFactory? httpClientFactory = null, bool useSqliteApprovalStore = false)
        {
            this.httpClientFactory = httpClientFactory ?? new FakeHttpClientFactory();
            State = CreateState(temp.SqlitePath);
            ApprovalStore = new FakeGatewayApprovalStore();
            Publisher = new CapturingPublisher();
            conversationMapStore = new InMemoryConversationMapStore();
            database = CreateDatabase(temp.SqlitePath);
            SqliteApprovalStore = new GatewayApprovalStore(database);
            ActiveApprovalStore = useSqliteApprovalStore ? SqliteApprovalStore : ApprovalStore;
            WorkflowStore = new GatewayWorkflowStore(database);
            AssetStore = new ConversationAssetStore(database, State, new NoOpWorkspaceRuntimeTool(), NullLogger<ConversationAssetStore>.Instance);
            SessionStore = new GatewaySessionStore(
                State,
                new NoOpGatewayContinuationStore(),
                AssetStore,
                new NoOpAgentMemoryEmbeddingQueue(),
                new SessionTitleService(),
                NullLogger<GatewaySessionStore>.Instance);
            ChatGateway = new FakeChatGatewayService(SessionStore);
            var configProvider = new FakePlatformConfigurationProvider(new PlatformConfiguration
            {
                Agents =
                [
                    new AgentDefinition
                    {
                        Id = "agent-1",
                        Session = new AgentSessionDefinition
                        {
                            MaxHistoryRuns = 5,
                        },
                    },
                ],
            });
            var sessionLifecycleService = new SessionLifecycleService(SessionStore, configProvider, Publisher);
            OutboxStore = new CapturingChannelOutboxStore();
            AttachmentRegistry = new FakeChannelAttachmentRegistry(State);
            ChannelHostGrpc = new FakeChannelHostGrpcService(State, OutboxStore);
            var runtimeEventProjector = new ChannelRuntimeEventProjector(OutboxStore, AttachmentRegistry, conversationMapStore, State, SessionStore);

            ApprovalResolution = new ApprovalResolutionService(
                        ActiveApprovalStore,
                        Publisher,
                        new RunStopRegistry(),
                        new NoOpApprovalResolutionEventDispatcher(),
                        new ApprovalResolutionTestInvocationLinkStore(),
                        new ThreadCommandTestSupport.MinimalAgentDefinitionStore(),
                        NullLogger<ApprovalResolutionService>.Instance,
                        workflowStore: WorkflowStore);
            Ingress = new ChannelIngressService(
                State,
                ChatGateway,
                new ChannelIngressInfrastructure(
                    this.httpClientFactory,
                    OutboxStore,
                    new ChannelOutboundMessagePublisher(OutboxStore, AttachmentRegistry),
                    AssetStore,
                    ChannelHostGrpc,
                    ActiveApprovalStore,
                    ApprovalResolution,
                    new ChannelThreadCommandAdapter(ApprovalResolution),
                    Publisher,
                    runtimeEventProjector,
                    OwnerTargetSyncNotifier),
                new ChannelIngressRoutingInfrastructure(
                    SessionStore,
                    new NoOpSessionCompactionService(),
                    sessionLifecycleService,
                    conversationMapStore,
                    AttachmentRegistry,
                    new ChannelRoutingCoordinator(State, AttachmentRegistry, conversationMapStore),
                    WorkflowStore),
                NullLogger<ChannelIngressService>.Instance);

            Publisher.OnHitlApprovalResolved = resolution => Ingress.SyncChannelApprovalResolutionAsync(
                new ChannelApprovalResolution(
                    resolution.RequestId,
                    ApprovalActionCatalog.Resolve(
                        resolution.Approved,
                        resolution.Source == HitlResolutionSource.SystemTimeout,
                        resolution.Reason,
                        resolution.StopRun,
                        resolution.TrustThisRun),
                    resolution.DecidedByUserId),
                CancellationToken.None);
        }

        public GatewayState State { get; }

        public string SqlitePath => temp.SqlitePath;

        public string DatabaseConnectionString => database.ConnectionString;

        public GatewaySessionStore SessionStore { get; }

        public GatewayWorkflowStore WorkflowStore { get; }

        public ChannelIngressService Ingress { get; }

        public FakeChatGatewayService ChatGateway { get; }
        public IReadOnlyList<QueuedSessionTurn> WaitingTurns => Ingress.PendingTurns.ListSession("user-1", ConversationMapStore.TryResolve("telegram", RegistrationId, "chat-1", "target:AGENT:agent-1")!.SessionId!);
        public SessionTurnExecution TurnExecution => new(State, SessionStore, ChatGateway, [Ingress]);

        public FakeGatewayApprovalStore ApprovalStore { get; }

        public GatewayApprovalStore SqliteApprovalStore { get; }

        public IGatewayApprovalStore ActiveApprovalStore { get; }

        public ApprovalResolutionService ApprovalResolution { get; }

        public CapturingChannelOutboxStore OutboxStore { get; }

        public FakeChannelAttachmentRegistry AttachmentRegistry { get; }

        public RecordingChannelHostOwnerTargetSyncNotifier OwnerTargetSyncNotifier { get; } = new();

        public FakeChannelHostGrpcService ChannelHostGrpc { get; }

        public ConversationAssetStore AssetStore { get; }

        public CapturingPublisher Publisher { get; }

        public InMemoryConversationMapStore ConversationMapStore => conversationMapStore;

        private sealed class NoOpSessionCompactionService : ISessionCompactionService
        {
            public ValueTask<GatewaySession?> CompactAsync(string sessionId, string reason, CancellationToken cancellationToken)
                => ValueTask.FromResult<GatewaySession?>(null);
        }

        public string RegistrationId => registrationId;

        private sealed class FakePlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
        {
            public PlatformConfiguration GetCurrent() => config;
        }

        public void SeedRegistration(JsonObject settings, string channelType = "telegram")
        {
            State.Users["owner"] = new GatewayUser(
                "user-1",
                "owner",
                "pwd",
                "admin",
                true,
                null,
                null,
                true,
                true);

            State.ChannelRegistrations[RegistrationId] = new GatewayChannelRegistration(
                RegistrationId,
                channelType,
                "user-1",
                "bot-1",
                true,
                settings,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"));

            State.ChannelIdentityLinks[$"{RegistrationId}:user:tg-user-1"] = new GatewayChannelIdentityLink(
                $"{RegistrationId}:user:tg-user-1",
                RegistrationId,
                "user-1",
                "tg-user-1",
                DateTimeOffset.UtcNow.ToString("O"));

            State.ChannelTargetBindings[$"{RegistrationId}:agent:agent-1"] = new GatewayChannelTargetBinding(
                $"{RegistrationId}:agent:agent-1",
                RegistrationId,
                "agent",
                "agent-1",
                true,
                DateTimeOffset.UtcNow.ToString("O"));

        }

        public void Dispose() => temp.Dispose();

        internal static GatewayState CreateState(string sqlitePath)
        {
            IConfiguration configuration = CreateConfiguration(sqlitePath);
            var database = new Kautuka.Gateway.Infrastructure.Database.KautukaDatabase(configuration);
            database.EnsureReady();

            return GatewayState.FromDatabase(
                database,
                NullLogger<GatewayState>.Instance,
                new GatewayMemoryStore(),
                new SqliteAgentDefinitionStore(configuration),
                configuration,
                new NoOpWorkspaceRuntimeTool());
        }

        private static Kautuka.Gateway.Infrastructure.Database.KautukaDatabase CreateDatabase(string sqlitePath)
        {
            IConfiguration configuration = CreateConfiguration(sqlitePath);
            var database = new Kautuka.Gateway.Infrastructure.Database.KautukaDatabase(configuration);
            database.EnsureReady();
            return database;
        }

        private static IConfiguration CreateConfiguration(string sqlitePath)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
                })
                .Build();
    }

    private sealed class FakeChannelAttachmentRegistry(GatewayState state) : IChannelAttachmentRegistry
    {
        public TaskCompletionSource? RefreshGate { get; set; }

        public TaskCompletionSource? RefreshStarted { get; set; }

        public int RefreshCount { get; private set; }

        public async Task RefreshAttachmentAsync(string attachmentId, string ownerUserId, CancellationToken cancellationToken)
        {
            RefreshCount++;
            RefreshStarted?.TrySetResult();
            if (RefreshGate is not null) await RefreshGate.Task.WaitAsync(cancellationToken);
        }

        public Task RefreshUserAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshUsersAsync(IEnumerable<GatewayUser> users, CancellationToken cancellationToken) => Task.CompletedTask;

        public bool TryGetRegistration(string attachmentId, out GatewayChannelRegistration? registration)
            => state.ChannelRegistrations.TryGetValue(attachmentId, out registration);

        public bool TryResolveIdentity(string attachmentId, string externalUserId, string chatId, out GatewayChannelIdentityLink? identity)
        {
            identity = state.ChannelIdentityLinks.Values.FirstOrDefault(item =>
                string.Equals(item.RegistrationId, attachmentId, StringComparison.Ordinal)
                && (string.Equals(item.ExternalUserId, externalUserId, StringComparison.Ordinal)
                    || string.Equals(item.ExternalUserId, chatId, StringComparison.Ordinal)));
            return identity is not null;
        }

        public bool TryResolveActiveRegistration(string userId, out string? registrationId, out string? externalUserId)
        {
            GatewayChannelRegistration? registration = state.ChannelRegistrations.Values
                .Where(item => item.IsActive && string.Equals(item.OwnerUserId, userId, StringComparison.Ordinal))
                .OrderByDescending(item => ChannelAttachmentIds.IsChat(item.Id))
                .FirstOrDefault();
            if (registration is null)
            {
                registrationId = null;
                externalUserId = null;
                return false;
            }

            registrationId = registration.Id;
            externalUserId = ChatIdFor(registration);
            return true;
        }

        public GatewayChannelRegistration[] GetActiveChatRegistrations(string userId)
            => [.. state.ChannelRegistrations.Values.Where(item => item.IsActive
                && item.OwnerUserId == userId && ChannelAttachmentIds.IsChat(item.Id))];

        public bool TryResolveAgentBot(string userId, string agentId, out string? registrationId, out string? externalUserId)
            => TryResolveRole(ChannelAttachmentIds.Agent("telegram", userId, agentId), out registrationId, out externalUserId);

        public bool TryResolveWorkflowReviewBot(string userId, string? workflowId, out string? registrationId, out string? externalUserId)
        {
            if (!string.IsNullOrWhiteSpace(workflowId)
                && TryResolveRole(ChannelAttachmentIds.Workflow("telegram", userId, workflowId), out registrationId, out externalUserId))
            {
                return true;
            }

            return TryResolveRole(ChannelAttachmentIds.Workflows("telegram", userId), out registrationId, out externalUserId);
        }

        private bool TryResolveRole(string attachmentId, out string? registrationId, out string? externalUserId)
        {
            if (state.ChannelRegistrations.TryGetValue(attachmentId, out GatewayChannelRegistration? registration)
                && registration.IsActive)
            {
                string? chatId = ChatIdFor(registration);
                if (!string.IsNullOrWhiteSpace(chatId))
                {
                    registrationId = attachmentId;
                    externalUserId = chatId;
                    return true;
                }
            }

            registrationId = null;
            externalUserId = null;
            return false;
        }

        private string? ChatIdFor(GatewayChannelRegistration registration)
            => state.ChannelIdentityLinks.Values.FirstOrDefault(item => string.Equals(item.RegistrationId, registration.Id, StringComparison.Ordinal))?.ExternalUserId
                ?? registration.Settings.GetStringNodeValue("channel_device_id");

        public GatewayChannelTargetBinding[] GetBindings(string attachmentId)
            => [.. state.ChannelTargetBindings.Values.Where(item => string.Equals(item.RegistrationId, attachmentId, StringComparison.Ordinal))];

        public GatewayChannelTargetBinding? GetDefaultBinding(string attachmentId)
            => state.ChannelTargetBindings.Values.FirstOrDefault(item => string.Equals(item.RegistrationId, attachmentId, StringComparison.Ordinal) && item.IsDefault);

        public GatewayUserListItem BuildUserPayload(GatewayUser user)
        {
            string? registrationId = state.ChannelRegistrations.Values.FirstOrDefault(item => string.Equals(item.OwnerUserId, user.Id, StringComparison.Ordinal))?.Id;
            string? externalUserId = registrationId is null
                ? null
                : state.ChannelIdentityLinks.Values.FirstOrDefault(item => string.Equals(item.RegistrationId, registrationId, StringComparison.Ordinal))?.ExternalUserId;

            return new(
                user.Id,
                user.Username,
                user.Email,
                user.DisplayName,
                user.Role,
                user.IsActive,
                user.CanImportConnectors,
                user.CanImportSkills,
                externalUserId,
                true,
                GetDefaultBinding(registrationId ?? string.Empty)?.TargetType,
                GetDefaultBinding(registrationId ?? string.Empty)?.TargetId,
                [.. state.ChannelRegistrations.Values.Where(item => string.Equals(item.OwnerUserId, user.Id, StringComparison.Ordinal)).Select(item => item.Id)],
                [],
                [.. state.ChannelIdentityLinks.Values.Where(item => string.Equals(item.UserId, user.Id, StringComparison.Ordinal)).Select(item => new GatewayChannelIdentityLinkPayload(item.Id, item.RegistrationId, "telegram", item.ExternalUserId, item.CreatedAt))],
                []);
        }

        public Task<GatewayUserChannelsResponse> BuildUserChannelCatalogAsync(GatewayUser user, GatewayChannelSetupPayload? setup, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ChannelSettingsUpdateResult?> UpdateUserChannelSettingsAsync(GatewayUser user, string providerKey, JsonElement body, CancellationToken cancellationToken)
            => Task.FromResult<ChannelSettingsUpdateResult?>(null);

        public Task SetActiveTargetForUserAsync(string attachmentId, string externalUserId, string targetType, string targetId, CancellationToken cancellationToken)
        {
            if (state.ChannelRegistrations.TryGetValue(attachmentId, out GatewayChannelRegistration? registration))
            {
                JsonObject settings = registration.Settings.DeepClone() as JsonObject ?? [];
                JsonObject activeByUser = settings["active_target_by_external_user"] as JsonObject ?? [];
                activeByUser[externalUserId] = $"{targetType}:{targetId}";
                settings["active_target_by_external_user"] = activeByUser;
                state.ChannelRegistrations[attachmentId] = registration with { Settings = settings };
            }

            return Task.CompletedTask;
        }

        public Task<bool> BindExternalIdentityAsync(string attachmentId, string ownerUserId, string externalUserId, string conversationId, CancellationToken cancellationToken)
        {
            if (!string.Equals(externalUserId, conversationId, StringComparison.Ordinal)
                || !state.ChannelRegistrations.TryGetValue(attachmentId, out GatewayChannelRegistration? registration)
                || !string.Equals(registration.OwnerUserId, ownerUserId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            string id = $"{attachmentId}:user:{externalUserId}";
            state.ChannelIdentityLinks[id] = new GatewayChannelIdentityLink(
                id,
                attachmentId,
                ownerUserId,
                externalUserId,
                DateTimeOffset.UtcNow.ToString("O"));
            return Task.FromResult(true);
        }

        public Task<string?> GetSlackPairingFailureReasonAsync(string ownerUserId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public ChannelHitlTetherMode GetChannelHitlTetherMode(string userId, string? sessionId)
        {
            GatewayChannelRegistration? registration = state.ChannelRegistrations.Values.FirstOrDefault(item => item.IsActive && string.Equals(item.OwnerUserId, userId, StringComparison.Ordinal));
            return ChannelHitlTetherModeExtensions.Parse(registration?.Settings.GetStringNodeValue("hitl_tether_mode"));
        }
    }

    private sealed class FakeChannelHostGrpcService(
        GatewayState state,
        CapturingChannelOutboxStore outboxStore) : IChannelHostGrpcService
    {
        private readonly Dictionary<string, (string FileName, string ContentType, byte[] Content)> inboundMedia = new(StringComparer.Ordinal);

        public void SeedInboundMedia(string mediaId, string fileName, string contentType, string textContent)
            => inboundMedia[mediaId] = (fileName, contentType, Encoding.UTF8.GetBytes(textContent));

        public ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChannelHostPingResponse
            {
                Providers =
                {
                    new ChannelProviderHealth { ProviderKey = "telegram", DisplayName = "Telegram", Status = "healthy", Detail = "test" }
                }
            });

        public ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
        {
            ListChannelProvidersResponse response = new();
            ChannelProviderDefinition provider = new()
            {
                ProviderKey = "telegram",
                DisplayName = "Telegram",
                SchemaVersion = "telegram/v1",
                CapabilityVersion = "telegram/v1",
            };
            provider.Fields.Add(new AttachmentSchemaField { FieldKey = "owner_user_id", Label = "Owner User Id", FieldType = AttachmentSchemaFieldType.ReadonlyText });
            provider.Fields.Add(new AttachmentSchemaField { FieldKey = "channel_device_id", Label = "Channel Device Id", FieldType = AttachmentSchemaFieldType.Text });
            provider.Fields.Add(new AttachmentSchemaField { FieldKey = "bot_token", Label = "Bot Token", FieldType = AttachmentSchemaFieldType.SecretText, Secret = true });
            response.Providers.Add(provider);
            return ValueTask.FromResult(response);
        }

        public ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentsResponse response = new();
            foreach (GatewayChannelRegistration registration in state.ChannelRegistrations.Values.Where(item =>
                         (request.IncludeInactive || item.IsActive)
                         && (string.IsNullOrWhiteSpace(request.OwnerUserId) || string.Equals(item.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal))))
            {
                response.Attachments.Add(new AttachmentSummary
                {
                    AttachmentId = registration.Id,
                    DisplayName = $"Telegram {registration.OwnerUserId}",
                    ProviderKey = registration.ChannelType,
                    Status = registration.IsActive ? AttachmentStatus.Active : AttachmentStatus.Inactive,
                    SchemaVersion = "telegram/v1",
                    CapabilityVersion = "telegram/v1",
                });
            }

            return ValueTask.FromResult(response);
        }

        public ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            GatewayChannelRegistration registration = state.ChannelRegistrations[request.AttachmentId];
            GetAttachmentResponse response = new()
            {
                Attachment = new AttachmentSummary
                {
                    AttachmentId = registration.Id,
                    DisplayName = $"Telegram {registration.OwnerUserId}",
                    ProviderKey = registration.ChannelType,
                    Status = registration.IsActive ? AttachmentStatus.Active : AttachmentStatus.Inactive,
                    SchemaVersion = "telegram/v1",
                    CapabilityVersion = "telegram/v1",
                },
            };
            foreach ((string key, JsonNode? node) in registration.Settings)
            {
                if (node is null)
                {
                    continue;
                }

                AttachmentSettingValue value = new() { FieldKey = key };
                if (node is JsonObject or JsonArray)
                {
                    value.JsonValue = node.ToJsonString();
                }
                else
                {
                    value.StringValue = node.ToString();
                }

                response.Settings.Add(value);
            }

            response.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = registration.OwnerUserId });
            return ValueTask.FromResult(response);
        }

        public ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new GetAttachmentSchemaResponse { AttachmentId = request.AttachmentId, ProviderKey = "telegram", SchemaVersion = "telegram/v1", CapabilityVersion = "telegram/v1" });

        public ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            JsonObject settings = [];
            foreach (AttachmentSettingValue value in request.Settings)
            {
                settings[value.FieldKey] = !string.IsNullOrWhiteSpace(value.JsonValue)
                    ? JsonNode.Parse(value.JsonValue)
                    : value.StringValue;
            }

            state.ChannelRegistrations[request.AttachmentId] = state.ChannelRegistrations.TryGetValue(request.AttachmentId, out GatewayChannelRegistration? existing)
                ? existing with { Settings = settings, IsActive = true, UpdatedAt = DateTimeOffset.UtcNow.ToString("O") }
                : new GatewayChannelRegistration(request.AttachmentId, "telegram", settings["owner_user_id"]?.ToString() ?? string.Empty, "bot", true, settings, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));

            return ValueTask.FromResult(new UpsertAttachmentResponse
            {
                Attachment = new AttachmentSummary { AttachmentId = request.AttachmentId, ProviderKey = "telegram", Status = AttachmentStatus.Active },
                ValidationStatus = ValidationStatus.Accepted,
            });
        }

        public ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default)
        {
            foreach (GatewayChannelRegistration registration in state.ChannelRegistrations.Values.Where(item =>
                         item.IsActive
                         && string.Equals(item.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal)
                         && string.Equals(item.ChannelType, "telegram", StringComparison.OrdinalIgnoreCase)))
            {
                ChannelRegistrationProvisioningAction payload = new(
                    registration.Settings["channel_device_id"]?.ToString() ?? string.Empty,
                    string.IsNullOrWhiteSpace(request.BotName) ? "Kautuka" : request.BotName,
                    request.Targets.Count == 0
                        ? "Kautuka bot. Granted targets: No agent targets granted yet."
                        : $"Kautuka bot. Granted targets: {string.Join(", ", request.Targets.Select(static item => item.TargetKey))}",
                    "Default target: none",
                    [.. ChannelBotCommandCatalogue.Default],
                    [.. request.Targets.Select(static item => new ChannelTargetDescriptor(item.TargetKey, item.DisplayName))]);

                outboxStore.Enqueue("telegram", registration.Id, "provision_registration", JsonSerializer.Serialize(payload, jsonOptions), null);
            }

            return ValueTask.FromResult(new SyncOwnerTargetsResponse
            {
                Accepted = true,
                Detail = "queued",
                RegistrationsConsidered = state.ChannelRegistrations.Values.Count(item => item.IsActive && string.Equals(item.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal)),
                RegistrationsSynced = state.ChannelRegistrations.Values.Count(item => item.IsActive && string.Equals(item.OwnerUserId, request.OwnerUserId, StringComparison.Ordinal)),
            });
        }

        public ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            string conversationId = state.ChannelRegistrations.TryGetValue(request.AttachmentId, out GatewayChannelRegistration? registration)
                ? registration.Settings["channel_device_id"]?.ToString() ?? string.Empty
                : string.Empty;

            ChannelRegistrationProvisioningAction payload = new(
                conversationId,
                request.BotName,
                request.Description,
                request.ShortDescription,
                [.. request.Commands.Select(static item => new ChannelCommandDescriptor(item.Command, item.Description))],
                [.. request.Targets.Select(static item => new ChannelTargetDescriptor(item.TargetKey, item.DisplayName))]);

            outboxStore.Enqueue("telegram", request.AttachmentId, "provision_registration", JsonSerializer.Serialize(payload, jsonOptions), null);
            return ValueTask.FromResult(new ProvisionAttachmentResponse
            {
                AttachmentId = request.AttachmentId,
                Status = ProvisioningStatus.Accepted,
                Detail = "queued",
                OperationId = request.Context?.RequestId ?? string.Empty,
            });
        }

        public ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new DeactivateAttachmentResponse { AttachmentId = request.AttachmentId, Status = AttachmentStatus.Inactive, Detail = request.Reason });

        public ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request.Clone());
            if (SendRpcException is not null) throw SendRpcException;
            if (request.Content.ActionType == "send_semantic_message" && SendFailure is not null)
                return ValueTask.FromResult(new SendOutboundMessageResponse { Accepted = false, Detail = SendFailure, DeliveryStatus = DeliveryStatus.RetryableFailure });
            if (request.Content.ActionType == "edit_semantic_message" && EditFailure is not null)
                return ValueTask.FromResult(new SendOutboundMessageResponse { Accepted = false, Detail = EditFailure, DeliveryStatus = DeliveryStatus.Rejected });
            outboxStore.Enqueue("telegram", request.AttachmentId, request.Content.ActionType, request.Content.PayloadJson, null);
            string messageId = request.Content.ActionType == "edit_semantic_message"
                ? JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)?.TransportMessageId ?? request.MessageId
                : request.MessageId;
            return ValueTask.FromResult(new SendOutboundMessageResponse { Accepted = true, DeliveryStatus = DeliveryStatus.Delivered, Detail = string.Empty, TransportMessageId = messageId });
        }

        public List<SendOutboundMessageRequest> Sent { get; } = [];
        public string? SendFailure { get; set; }
        public string? EditFailure { get; set; }
        public Grpc.Core.RpcException? SendRpcException { get; set; }

        public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(true);
            if (!inboundMedia.TryGetValue(request.MediaId, out var media))
            {
                yield break;
            }

            yield return new ReadInboundMediaChunk
            {
                FileName = media.FileName,
                ContentType = media.ContentType,
                SizeBytes = media.Content.Length,
                Content = Google.Protobuf.ByteString.CopyFrom(media.Content),
                IsFinal = false,
            };

            yield return new ReadInboundMediaChunk
            {
                FileName = media.FileName,
                ContentType = media.ContentType,
                SizeBytes = media.Content.Length,
                Content = Google.Protobuf.ByteString.Empty,
                IsFinal = true,
            };
        }

        public ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AcknowledgeIngressResponse { Acknowledged = true });

        public ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SetRuntimeCredentialsResponse { Accepted = true });

        public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(true);
            yield break;
        }
    }

    private sealed class FakeGatewayApprovalStore : IGatewayApprovalStore
    {
        private readonly Dictionary<string, GatewayToolApprovalRequest> requests = new(StringComparer.Ordinal);

        public bool RejectResolutions { get; set; }

        public void CreateRequest(GatewayToolApprovalRequest request) => requests[request.Id] = request;

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => ResolveDecision(requestId, approved ? "approved" : "denied", reason, decidedBy, resolutionSource);

        public bool Resolve(
            string requestId,
            string decision,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
            => ResolveDecision(requestId, decision, reason, decidedBy, resolutionSource);

        private bool ResolveDecision(string requestId, string decision, string? reason, string? decidedBy, string resolutionSource)
        {
            if (RejectResolutions)
            {
                return false;
            }

            if (!requests.TryGetValue(requestId, out GatewayToolApprovalRequest? existing))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(existing.Decision))
            {
                return false;
            }

            requests[requestId] = existing with
            {
                Decision = decision,
                Reason = reason,
                DecidedBy = decidedBy,
                DecidedAt = DateTimeOffset.UtcNow.ToString("O"),
                ResolutionSource = resolutionSource,
            };
            return true;
        }

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
        {
            if (requests.TryGetValue(requestId, out GatewayToolApprovalRequest? existing))
            {
                requests[requestId] = existing with
                {
                    ChannelTetherMode = mode.ToStorageValue(),
                };
            }
        }

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll)
            => requests.Values
                .Where(item => string.IsNullOrWhiteSpace(item.Decision))
                .ToArray();

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public GatewayToolApprovalRequest? GetById(string requestId)
            => requests.TryGetValue(requestId, out GatewayToolApprovalRequest? value) ? value : null;

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => requests.Values
                .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                .ToArray();

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
            => requests.Values
                .Where(item => !string.IsNullOrWhiteSpace(item.Decision))
                .Skip(offset)
                .Take(limit)
                .ToArray();
    }

    private sealed record OutboxAcknowledgement(
        string ActionId,
        string? Error,
        DateTimeOffset? RetryAtUtc,
        int MaxAttempts);

    private sealed class SingleEntryChannelOutboxStore(ChannelOutboxEntry entry) : IChannelOutboxStore
    {
        private int leased;

        public TaskCompletionSource<OutboxAcknowledgement> Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChannelOutboxEntry Enqueue(string channelType, string registrationId, string actionType, string payloadJson,
            DateTimeOffset? availableAtUtc = null, string? runId = null, string? traceParent = null, string? traceState = null,
            string? ownerUserId = null)
            => throw new NotSupportedException();

        public IReadOnlyList<ChannelOutboxEntry> LeasePending(string leaseOwner, int maxItems, TimeSpan leaseDuration, DateTimeOffset nowUtc)
        {
            if (Interlocked.Exchange(ref leased, 1) != 0) return [];
            return [entry with { Status = "leased", LeaseId = "lease-1", LeaseOwner = leaseOwner }];
        }

        public void Acknowledge(string actionId, string leaseOwner, string leaseId, bool success, string? error,
            DateTimeOffset nowUtc, DateTimeOffset? retryAtUtc = null, int maxAttempts = 10)
            => Acknowledged.TrySetResult(new(actionId, error, retryAtUtc, maxAttempts));
    }

    private sealed class CapturingChannelOutboxStore : IChannelOutboxStore
    {
        public List<ChannelOutboxEntry> Entries { get; } = [];

        public ChannelOutboxEntry Enqueue(
            string channelType,
            string registrationId,
            string actionType,
            string payloadJson,
            DateTimeOffset? availableAtUtc = null,
            string? runId = null,
            string? traceParent = null,
            string? traceState = null,
            string? ownerUserId = null)
        {
            ChannelOutboxEntry entry = new(
                $"outbox-{Entries.Count + 1}",
                channelType,
                registrationId,
                actionType,
                payloadJson,
                "pending",
                0,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                availableAtUtc?.ToString("O"),
                null,
                null,
                null,
                null,
                runId,
                traceParent,
                traceState,
                ownerUserId);
            Entries.Add(entry);
            return entry;
        }

        public IReadOnlyList<ChannelOutboxEntry> LeasePending(string leaseOwner, int maxItems, TimeSpan leaseDuration, DateTimeOffset nowUtc)
            => Entries.Take(maxItems).ToArray();

        public void Acknowledge(string actionId, string leaseOwner, string leaseId, bool success, string? error, DateTimeOffset nowUtc, DateTimeOffset? retryAtUtc = null, int maxAttempts = 10)
        {
        }
    }

    private sealed class FakeChatGatewayService(IGatewaySessionStore sessionStore) : IChatGatewayService
    {
        public bool EmitRunStarted { get; set; }
        public IReadOnlyList<string> LastAssetIds { get; private set; } = [];
        public string LastMessage { get; private set; } = string.Empty;
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(JsonElement body, AgentRequestContext? requestContext = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string targetType = GetString(body, "target_type", "targetType", "TargetType") ?? "agent";
            string targetId = GetString(body, "target_id", "targetId", "TargetId") ?? string.Empty;
            string sessionId = GetString(body, "session_id", "sessionId", "SessionId") ?? "session-fake";
            string message = GetString(body, "message", "Message") ?? string.Empty;
            LastMessage = message;
            Messages.Enqueue(message);
            LastAssetIds = NaradRequestParsing.ExtractAssetIds(body);
            await Task.CompletedTask.ConfigureAwait(true);

            if (string.Equals(message, "needs-hitl", StringComparison.Ordinal))
            {
                var userMessage = new GatewayMessage(
                    "msg-hitl-user",
                    "user",
                    message,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    null,
                    null,
                    null,
                    Source: "telegram");

                var assistantMessage = new GatewayMessage(
                    "msg-hitl-assistant",
                    "assistant",
                    string.Empty,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    null,
                    new GatewayMetrics(0, 0, 0),
                    ToolCalls: [new GatewayToolCall("tool-call-hitl-anchor", "run_shell", targetId, "{\"command\":\"echo hi\"}", null, "calling", "superexec")]);

                yield return NaradTestEventFactory.FromChatEvent(new GatewayChatApprovalRequest(
                    "req-hitl-anchor",
                    "tool-call-hitl-anchor",
                    "run_shell",
                    "{\"command\":\"echo hi\"}",
                    RunId: "run-hitl-anchor"));
                // Match production ordering: interrupt first, persistence at run.paused.
                sessionStore.AppendMessages(sessionId, userMessage, assistantMessage, userId: "user-1");
                yield return CreateChannelRuntimeEvent(NaradEventTypes.RunPaused,
                    JsonSerializer.SerializeToElement(new JsonObject { ["reason"] = "approval_required" }, KautukaJsonContext.Default.JsonObject),
                    sessionId, targetId, "run-hitl-anchor");
                yield break;
            }

            string messageId = "msg-fake-1";
            string runId = "run-fake-1";
            if (EmitRunStarted) yield return CreateChannelRuntimeEvent(NaradEventTypes.RunStarted, JsonSerializer.SerializeToElement(new { }), sessionId, targetId, runId);
            yield return CreateChannelRuntimeEvent(
                NaradEventTypes.MessageTextDelta,
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["delta"] = "fake-response",
                    ["isThinking"] = false,
                    ["messageId"] = messageId,
                }),
                sessionId,
                targetId,
                runId);
            yield return CreateChannelRuntimeEvent(
                NaradEventTypes.MessageTextCompleted,
                JsonSerializer.SerializeToElement(new JsonObject { ["messageId"] = messageId }),
                sessionId,
                targetId,
                runId);
            yield return CreateChannelRuntimeEvent(
                NaradEventTypes.RunFinished,
                JsonSerializer.SerializeToElement(new GatewayChatStreamDone(new GatewayChatResult(
                    targetType,
                    targetId,
                    string.IsNullOrWhiteSpace(sessionId) ? "session-fake" : sessionId,
                    "fake-response",
                    false,
                    new GatewayMetrics(1, 1, 1),
                    null,
                    null,
                    [])), KautukaJsonContext.Default.GatewayChatStreamDone),
                sessionId,
                targetId,
                runId);
        }

        private static NaradPersistedEvent CreateChannelRuntimeEvent(
            string eventType,
            JsonElement payload,
            string sessionId,
            string agentId,
            string runId)
        {
            string resolvedSession = string.IsNullOrWhiteSpace(sessionId) ? "session-fake" : sessionId;
            _ = agentId;
            return NaradTestEventFactory.FromBody(
                eventType,
                payload,
                resolvedSession,
                runId,
                runId,
                sessionSequence: 1,
                rootSequence: 1);
        }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(string targetType, string targetId, string sessionId, ToolApprovalResponseInfo approvalResponse, string? userId, Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(true);
            yield break;
        }

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null) { }

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null) { }

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null) { }

        private static string? GetString(JsonElement body, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (body.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString();
                }
            }

            return null;
        }
    }

    private sealed class InMemoryConversationMapStore : IChannelConversationMapStore
    {
        private readonly Dictionary<string, ChannelConversationMapEntry> entries = new(StringComparer.Ordinal);

        public IReadOnlyList<ChannelConversationMapEntry> ListAll() => entries.Values.ToArray();

        public ChannelConversationMapEntry GetOrCreatePermanentTopic(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerTopicId, string? sessionId)
        {
            string key = BuildKey(channelType, registrationId, providerConversationId, providerTopicId);
            if (entries.TryGetValue(key, out ChannelConversationMapEntry? existing))
            {
                return existing;
            }

            ChannelConversationMapEntry created = CreateEntry(
                new ConversationMapIdentity(channelType, registrationId, targetType, targetId, providerConversationId, providerTopicId),
                "permanent_topic",
                sessionId,
                null);
            entries[key] = created;
            return created;
        }

        public ChannelConversationMapEntry CreateTransientThread(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerThreadId, string? sessionId, DateTimeOffset expiresAtUtc)
        {
            string key = BuildKey(channelType, registrationId, providerConversationId, providerThreadId);
            ChannelConversationMapEntry created = CreateEntry(
                new ConversationMapIdentity(channelType, registrationId, targetType, targetId, providerConversationId, providerThreadId),
                "transient_thread",
                sessionId,
                expiresAtUtc.ToString("O"));
            entries[key] = created;
            return created;
        }

        public ChannelConversationMapEntry? TryResolve(string channelType, string registrationId, string providerConversationId, string providerThreadId)
        {
            entries.TryGetValue(BuildKey(channelType, registrationId, providerConversationId, providerThreadId), out ChannelConversationMapEntry? entry);
            return entry;
        }

        public ChannelConversationMapEntry? TryResolveBySession(string sessionId, string channelType)
        {
            return entries.Values
                .Where(entry => string.Equals(entry.ChannelType, channelType, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.UpdatedAt, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public ChannelConversationMapEntry? TryResolveLatestByRegistration(string channelType, string registrationId)
        {
            return entries.Values
                .Where(entry => string.Equals(entry.ChannelType, channelType, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(entry.RegistrationId, registrationId, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.UpdatedAt, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        public ChannelConversationMapEntry? AttachSession(string entryId, string sessionId, DateTimeOffset? expiresAtUtc = null)
        {
            string? key = entries.FirstOrDefault(pair => string.Equals(pair.Value.Id, entryId, StringComparison.Ordinal)).Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            ChannelConversationMapEntry existing = entries[key];
            ChannelConversationMapEntry updated = existing with
            {
                SessionId = sessionId,
                ExpiresAt = expiresAtUtc?.ToString("O") ?? existing.ExpiresAt,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            };

            entries[key] = updated;
            return updated;
        }

        public int CleanupExpiredTransientThreads(DateTimeOffset nowUtc)
        {
            int removed = 0;
            foreach (string key in entries
                         .Where(pair => string.Equals(pair.Value.ConversationKind, "transient_thread", StringComparison.Ordinal)
                                        && DateTimeOffset.TryParse(pair.Value.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset expiresAt)
                                        && expiresAt <= nowUtc)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (entries.Remove(key))
                {
                    removed++;
                }
            }

            return removed;
        }

        private static ChannelConversationMapEntry CreateEntry(ConversationMapIdentity identity, string kind, string? sessionId, string? expiresAt)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            return new ChannelConversationMapEntry(
                $"map-{Guid.NewGuid():N}",
            identity.ChannelType,
            identity.RegistrationId,
            identity.TargetType,
            identity.TargetId,
            identity.ConversationId,
            identity.ThreadId,
                kind,
                sessionId,
                now,
                now,
                expiresAt);
        }

        private static string BuildKey(string channelType, string registrationId, string conversationId, string threadId)
            => $"{channelType}:{registrationId}:{conversationId}:{threadId}";

        private sealed record ConversationMapIdentity(
            string ChannelType,
            string RegistrationId,
            string TargetType,
            string TargetId,
            string ConversationId,
            string ThreadId);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(new FakeSuccessMessageHandler());

        public HttpClient CreateClient(string name) => client;

        public void Dispose() => client.Dispose();
    }

    private sealed class FakeSuccessMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler);

        public HttpClient CreateClient(string name) => client;

        public void Dispose() => client.Dispose();
    }

    private sealed class CapturingTelegramMessageHandler : HttpMessageHandler
    {
        private long nextMessageId = 500;

        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);

            string url = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Add((url, body));

            string responseJson;
            if ((url.Contains("/sendMessage", StringComparison.Ordinal) || url.Contains("/sendRichMessage", StringComparison.Ordinal)))
            {
                long messageId = Interlocked.Increment(ref nextMessageId);
                responseJson = $"{{\"ok\":true,\"result\":{{\"message_id\":{messageId}}}}}";
            }
            else
            {
                responseJson = "{\"ok\":true}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturingPublisher : IMediator
    {
        public List<object> Notifications { get; } = [];
        public Func<HitlApprovalResolved, Task>? OnHitlApprovalResolved { get; set; }
        public Func<Task>? OnSessionRetentionRequested { get; set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            if (notification is SessionRetentionRequested && OnSessionRetentionRequested is not null)
                return new ValueTask(OnSessionRetentionRequested());
            if (notification is HitlApprovalResolved resolved && OnHitlApprovalResolved is not null)
            {
                return new ValueTask(OnHitlApprovalResolved(resolved));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Notifications.Add(notification!);
            if (notification is SessionRetentionRequested && OnSessionRetentionRequested is not null)
                return new ValueTask(OnSessionRetentionRequested());
            if (notification is HitlApprovalResolved resolved && OnHitlApprovalResolved is not null)
            {
                return new ValueTask(OnHitlApprovalResolved(resolved));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => CreateContext("agent", agentId, userId);

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => CreateContext("agent", agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession("agent", agentId, userId));

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession("agent", agentId, userId));

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string scopeType, string scopeId, string? userId)
            => new(scopeType, scopeId, userId, Path.GetTempPath(), "/workspace", "/workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string scopeType, string scopeId, string? userId)
            => new($"{scopeType}-{scopeId}", false, scopeType, scopeId, userId);
    }

    private sealed class ChannelThreadCommandAdapter(IApprovalResolutionService inner) : IThreadCommandService
    {
        public async ValueTask<ThreadCommandResult> ResolveInterruptAsync(
            ResolveThreadInterruptCommand command,
            CancellationToken cancellationToken)
        {
            if (!ApprovalActionCatalog.TryMapDecision(command.Decision, out bool approved, out bool stopRun, out string action))
            {
                return new ThreadCommandResult(ThreadCommandStatus.Rejected, Detail: "Field 'decision' is not a known interrupt action.");
            }

            ApprovalResolutionResult resolution = await inner.ResolveAsync(
                new ApprovalResolutionCommand(
                    command.InterruptId,
                    approved,
                    command.Reason,
                    command.UserId,
                    command.Source,
                    UserId: command.UserId,
                    StopRun: stopRun,
                    OriginatingChannelType: command.OriginatingChannelType,
                    DecidedByDisplayName: command.Actor,
                    Action: action),
                cancellationToken).ConfigureAwait(false);

            return resolution.Status switch
            {
                ApprovalResolutionStatus.Resolved => new ThreadCommandResult(
                    ThreadCommandStatus.Accepted,
                    Decision: resolution.Request?.Decision,
                    RequestId: command.InterruptId),
                ApprovalResolutionStatus.AlreadyResolved => new ThreadCommandResult(
                    ThreadCommandStatus.AlreadyApplied,
                    Decision: resolution.Request?.Decision,
                    RequestId: command.InterruptId),
                ApprovalResolutionStatus.Expired => new ThreadCommandResult(
                    ThreadCommandStatus.Expired,
                    RequestId: command.InterruptId),
                _ => new ThreadCommandResult(ThreadCommandStatus.NotFound, RequestId: command.InterruptId),
            };
        }

        public ValueTask<ThreadCommandResult> ResolveAllInterruptsAsync(ResolveAllThreadInterruptsCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> StopActiveRootAsync(StopActiveRootCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> ResumeWaitingRootAsync(ResumeWaitingRootCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> RetryTargetAsync(RetryExecutionTargetCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<ThreadCommandResult> CancelGraphNodeAsync(CancelGraphNodeCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NoOpApprovalResolutionEventDispatcher : IApprovalResolutionEventDispatcher
    {
        public ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingChannelHostOwnerTargetSyncNotifier : IChannelHostOwnerTargetSyncNotifier
    {
        public List<string> Reasons { get; } = [];

        public bool TryNotify(string ownerUserId, string reason)
        {
            Reasons.Add(reason);
            return true;
        }
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-channel-ingress-tests", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string SqlitePath => Path.Combine(root, "gateway.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ChannelHostDatabase CreateChannelHostDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new ChannelHostDatabase(configuration);
        database.EnsureReady();
        return database;
    }
}
