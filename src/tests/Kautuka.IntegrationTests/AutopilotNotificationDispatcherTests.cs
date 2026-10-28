using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotNotificationDispatcherTests
{
    [Fact]
    public async Task UserVisibleTextIsDeliveredToEveryLinkedChatWithProviderLocalDestination()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        attachments.Register(new GatewayChannelRegistration("slack:user-1:default", "slack", "user-1", "bot-slack", true,
            JsonNode.Parse("{\"slack_dm_channel_id\":\"D-SLACK\"}")!.AsObject(), "created", "updated"));
        attachments.Register(new GatewayChannelRegistration("telegram:user-1:default", "telegram", "user-1", "bot-telegram", true,
            JsonNode.Parse("{\"channel_device_id\":\"TG-USER\"}")!.AsObject(), "created", "updated"));
        var publisher = new ChannelOutboundMessagePublisher(outbox, attachments);

        await publisher.PublishTextMessageAsync(
            new("slack", "slack:user-1:default", "D-SLACK", "thread-1", "Help reply"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, outbox.Entries.Count);
        ChannelOutboxEntry slack = Assert.Single(outbox.Entries, entry => entry.RegistrationId == "slack:user-1:default");
        ChannelOutboxEntry telegram = Assert.Single(outbox.Entries, entry => entry.RegistrationId == "telegram:user-1:default");
        ChannelSemanticMessageAction slackAction = JsonSerializer.Deserialize(slack.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        ChannelSemanticMessageAction telegramAction = JsonSerializer.Deserialize(telegram.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("D-SLACK", slackAction.ConversationId);
        Assert.Equal("thread-1", slackAction.ConversationThreadId);
        Assert.Equal("TG-USER", telegramAction.ConversationId);
        Assert.Null(telegramAction.ConversationThreadId);
    }

    [Fact]
    public async Task StreamUpdatesAreDeliveredToEveryLinkedChatWithProviderLocalDestination()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        attachments.Register(new GatewayChannelRegistration("slack:user-1:default", "slack", "user-1", "bot-slack", true,
            JsonNode.Parse("{\"slack_dm_channel_id\":\"D-SLACK\"}")!.AsObject(), "created", "updated"));
        attachments.Register(new GatewayChannelRegistration("telegram:user-1:default", "telegram", "user-1", "bot-telegram", true,
            JsonNode.Parse("{\"channel_device_id\":\"TG-USER\"}")!.AsObject(), "created", "updated"));
        var publisher = new ChannelOutboundMessagePublisher(outbox, attachments);

        await publisher.PublishStreamMessageAsync(
            new("slack", "slack:user-1:default", "stream-1", 2, "D-SLACK", "thread-2", "A complete reply", true, "Agent"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, outbox.Entries.Count);
        ChannelStreamMessageAction slack = JsonSerializer.Deserialize(
            Assert.Single(outbox.Entries, entry => entry.RegistrationId == "slack:user-1:default").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelStreamMessageAction)!;
        ChannelStreamMessageAction telegram = JsonSerializer.Deserialize(
            Assert.Single(outbox.Entries, entry => entry.RegistrationId == "telegram:user-1:default").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelStreamMessageAction)!;
        Assert.Equal("D-SLACK", slack.ConversationId);
        Assert.Equal("thread-2", slack.ConversationThreadId);
        Assert.Equal("TG-USER", telegram.ConversationId);
        Assert.Null(telegram.ConversationThreadId);
        Assert.Equal(slack.AccumulatedText, telegram.AccumulatedText);
        Assert.True(telegram.IsFinal);
    }

    [Fact]
    public async Task FileMessagesAreUploadedAndDeliveredToEveryLinkedChat()
    {
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry();
        attachments.Register(new GatewayChannelRegistration("slack:user-1:default", "slack", "user-1", "bot-slack", true,
            JsonNode.Parse("{\"slack_dm_channel_id\":\"D-SLACK\"}")!.AsObject(), "created", "updated"));
        attachments.Register(new GatewayChannelRegistration("telegram:user-1:default", "telegram", "user-1", "bot-telegram", true,
            JsonNode.Parse("{\"channel_device_id\":\"TG-USER\"}")!.AsObject(), "created", "updated"));
        var uploads = new RecordingAssetUploadService();
        var publisher = new ChannelOutboundMessagePublisher(outbox, attachments, uploads);

        await publisher.PublishFileAsync(
            new("slack", "slack:user-1:default", "session-1", "user-1", "asset-1", "D-SLACK", "thread-2",
                "report.txt", "text/plain", 12, "Report", "Agent"),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, uploads.Destinations.Count);
        Assert.Contains(uploads.Destinations, item => item.RegistrationId == "slack:user-1:default"
            && item.ConversationId == "D-SLACK" && item.ThreadId == "thread-2");
        Assert.Contains(uploads.Destinations, item => item.RegistrationId == "telegram:user-1:default"
            && item.ConversationId == "TG-USER" && item.ThreadId is null);
        Assert.Equal(2, outbox.Entries.Count);
        Assert.All(outbox.Entries, entry => Assert.Equal("send_file", entry.ActionType));
        ChannelOutboundFileAction slack = JsonSerializer.Deserialize(
            Assert.Single(outbox.Entries, entry => entry.RegistrationId == "slack:user-1:default").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelOutboundFileAction)!;
        ChannelOutboundFileAction telegram = JsonSerializer.Deserialize(
            Assert.Single(outbox.Entries, entry => entry.RegistrationId == "telegram:user-1:default").PayloadJson,
            ChannelContractsJsonContext.Default.ChannelOutboundFileAction)!;
        Assert.Equal("D-SLACK", slack.ConversationId);
        Assert.Equal("thread-2", slack.ConversationThreadId);
        Assert.Equal("TG-USER", telegram.ConversationId);
        Assert.Null(telegram.ConversationThreadId);
        Assert.Equal("Report", telegram.Caption);
    }

    [Fact]
    public async Task StreamPublisherPersistsOrderedAccumulatedUpdatesAndFinalization()
    {
        var outbox = new CapturingChannelOutboxStore();
        var publisher = new ChannelOutboundMessagePublisher(outbox, new FakeChannelAttachmentRegistry());
        await publisher.PublishStreamMessageAsync(new("slack", "registration-1", "stream-1", 0, "C1", "T1", "Hel", false, "Agent"), CancellationToken.None).ConfigureAwait(true);
        await publisher.PublishStreamMessageAsync(new("slack", "registration-1", "stream-1", 1, "C1", "T1", "Hello", true, "Agent"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, outbox.Entries.Count);
        Assert.All(outbox.Entries, entry => Assert.Equal("stream_message", entry.ActionType));
        ChannelStreamMessageAction? first = JsonSerializer.Deserialize(outbox.Entries[0].PayloadJson, ChannelContractsJsonContext.Default.ChannelStreamMessageAction);
        ChannelStreamMessageAction? final = JsonSerializer.Deserialize(outbox.Entries[1].PayloadJson, ChannelContractsJsonContext.Default.ChannelStreamMessageAction);
        Assert.Equal(0, first?.Sequence);
        Assert.False(first?.IsFinal);
        Assert.Equal("Hello", final?.AccumulatedText);
        Assert.True(final?.IsFinal);
    }

    [Theory]
    [InlineData(false, null, false, false)]
    [InlineData(false, "101", false, false)]
    [InlineData(true, null, false, false)]
    [InlineData(false, null, true, false)]
    [InlineData(false, "101", true, false)]
    [InlineData(true, null, true, false)]
    [InlineData(true, null, true, true)]
    public async Task DispatchNotificationsAsyncRoutesToAgentDestinationWithAgentName(
        bool ownBot,
        string? topic,
        bool completed,
        bool workflowTarget)
    {
        var store = new RecordingAutopilotStore();
        var routingCoordinator = new FakeChannelRoutingCoordinator { Topic = topic };
        var outbox = new CapturingChannelOutboxStore();
        var attachments = new FakeChannelAttachmentRegistry { OwnBot = ownBot };
        attachments.TargetUnavailable = workflowTarget;
        var publisher = new ChannelOutboundMessagePublisher(outbox, attachments);
        using var mappingFixture = new MappingFixture();
        var dispatcher = new AutopilotNotificationDispatcher(store, attachments, routingCoordinator, publisher, mappingFixture.Maps);

        GatewayAutopilotSchedule schedule = new(
            Id: "schedule-1",
            OwnerUserId: "user-1",
            TargetType: workflowTarget ? AutopilotTargetTypes.Workflow : "agent",
            TargetId: workflowTarget ? "workflow-1" : "agent-1",
            CronExpression: "*/5 * * * *",
            TimeZoneId: "UTC",
            Mode: AutopilotScheduleModes.On,
            RequiresPreRunApproval: false,
            SchedulePromptContext: "Check the patio lights",
            WorkspaceFilePaths: [],
            NotifyUiInbox: false,
            NotifyChannels: true,
            PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.NextOccurrence,
            PreRunApprovalTimeoutSeconds: null,
            OverlapPolicy: AutopilotOverlapPolicies.SkipAndNotify,
            IsPaused: false,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            NextDueAt: null,
            LastEvaluatedAt: null,
            LeaseOwner: null,
            LeaseExpiresAt: null);
        store.Schedule = schedule;

        GatewayScheduledRun run = new(
            Id: "run-1",
            ScheduleId: schedule.Id,
            OwnerUserId: schedule.OwnerUserId,
            TargetType: schedule.TargetType,
            TargetId: schedule.TargetId,
            Status: AutopilotRunStatuses.Running,
            TriggerType: "cron",
            ScheduledFor: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: "session-1",
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
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            EndedAt: null);

        Assert.True(attachments.TryGetRegistration(ownBot ? "agent-bot" : "reg-1", out GatewayChannelRegistration? destination));
        if (!workflowTarget)
        {
            ChannelConversationSessionBinding.BindSelectedSession(mappingFixture.Maps, destination!,
                ownBot ? "tg-agent-user" : "tg-user-1", topic, "agent", "agent-1", "previous-session");
        }

        await dispatcher.DispatchNotificationsAsync(
            run,
            schedule.Id,
            startedMessage: completed ? null : "Autopilot run started.",
            finalMessage: completed ? "Final answer" : null,
            DateTimeOffset.UtcNow,
            CancellationToken.None).ConfigureAwait(true);

        if (!completed)
        {
            Assert.Empty(outbox.Entries);
            return;
        }
        ChannelOutboxEntry entry = Assert.Single(outbox.Entries);
        Assert.Equal("send_semantic_message", entry.ActionType);
        Assert.Equal("user-1", entry.OwnerUserId);

        if (workflowTarget)
        {
            Assert.Null(attachments.ActiveSelection);
            return;
        }

        ChannelSemanticMessageAction? payload = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        Assert.NotNull(payload);
        Assert.Equal(ownBot ? "agent-bot" : "reg-1", entry.RegistrationId);
        Assert.Null(payload!.ConversationThreadId);
        Assert.Equal("Patio Planner", payload.Document.SenderDisplayName);
        Assert.Equal(completed ? "Final answer" : "Autopilot run started.", payload.Document.FallbackText);
        var coordinator = new ChannelRoutingCoordinator(null!, attachments, mappingFixture.Maps);
        var envelope = new ChannelInboundEnvelope("reply", "user", payload.ConversationId, payload.ConversationId,
            null, "roll a d787", false, null, null, null, null, []);
        var binding = new GatewayChannelTargetBinding("binding", entry.RegistrationId, "agent", "agent-1", true, DateTimeOffset.UtcNow.ToString("O"));
        ChannelConversationMapEntry nextReply = coordinator.ResolveConversationMap(entry.RegistrationId, binding, envelope);
        Assert.Equal("session-1", nextReply.SessionId);
        Assert.Equal(("agent", "agent-1"), attachments.ActiveSelection);
    }

    private sealed class MappingFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-autopilot-binding-" + Guid.NewGuid().ToString("N"));
        public SqliteChannelConversationMapStore Maps { get; }
        public MappingFixture()
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            var database = new KautukaDatabase(config);
            database.EnsureReady();
            Maps = new SqliteChannelConversationMapStore(database);
        }
        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingAutopilotStore : IGatewayAutopilotStore
    {
        public GatewayAutopilotSchedule? Schedule { get; set; }

        public GatewayAutopilotSchedule CreateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool UpdateSchedule(GatewayAutopilotSchedule schedule) => throw new NotSupportedException();
        public bool DeleteSchedule(string scheduleId, string ownerUserId) => throw new NotSupportedException();
        public GatewayAutopilotSchedule? GetSchedule(string scheduleId) => Schedule;
        public IReadOnlyList<GatewayAutopilotSchedule> ListSchedulesByOwner(string ownerUserId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> ListAllSchedules(int limit, int offset) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAutopilotSchedule> LeaseDueSchedules(string leaseOwner, DateTimeOffset nowUtc, TimeSpan leaseDuration, int limit) => throw new NotSupportedException();
        public GatewayScheduledRun InsertRun(GatewayScheduledRun run) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestRunBySchedule(string scheduleId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetRunById(string runId) => throw new NotSupportedException();
        public GatewayScheduledRun? GetLatestNonTerminalRunBySession(string sessionId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListRunsByStatus(string status, int limit) => throw new NotSupportedException();
        public IReadOnlyList<GatewayScheduledRun> ListNonTerminalRuns(int limit) => throw new NotSupportedException();
        public bool TransitionRunState(string runId, string expectedCurrentStatus, string nextStatus, string? error, string? outputJson, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, DateTimeOffset nowUtc, bool markStarted, bool markEnded) => throw new NotSupportedException();
        public bool UpdateRunMetadata(string runId, string? sessionId, string? runtimeRunId, string? preRunApprovalRequestId, string? outputJson, string? error, DateTimeOffset nowUtc) => throw new NotSupportedException();
        public bool SetRunSkipReason(string runId, string reason, DateTimeOffset nowUtc) => false;
        public IReadOnlyList<GatewayScheduledRun> ListRuns(string ownerUserId, string? scheduleId, int limit, int offset) => throw new NotSupportedException();
        public void RecordSuppression(GatewayAutopilotSuppression suppression) => throw new NotSupportedException();
        public void RecordNotification(GatewayAutopilotNotification notification) { }
        public IReadOnlyList<GatewayAutopilotNotification> ListNotifications(string ownerUserId, int limit, int offset, bool unreadOnly) => throw new NotSupportedException();
        public bool MarkNotificationRead(string ownerUserId, string notificationId, DateTimeOffset nowUtc) => throw new NotSupportedException();
    }

    private sealed class FakeChannelAttachmentRegistry : IChannelAttachmentRegistry
    {
        private readonly Dictionary<string, GatewayChannelRegistration> registrations = new(StringComparer.Ordinal);

        public void Register(GatewayChannelRegistration registration) => registrations[registration.Id] = registration;

        public bool OwnBot { get; set; }
        public bool TargetUnavailable { get; set; }
        public (string TargetType, string TargetId)? ActiveSelection { get; private set; }
        public bool TryResolveAgentBot(string userId, string agentId, out string? registrationId, out string? externalUserId)
        {
            registrationId = OwnBot ? "agent-bot" : null;
            externalUserId = OwnBot ? "tg-agent-user" : null;
            return OwnBot;
        }
        public Task RefreshAttachmentAsync(string attachmentId, string ownerUserId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshUserAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshUsersAsync(IEnumerable<GatewayUser> users, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool TryGetRegistration(string attachmentId, out GatewayChannelRegistration? registration)
        {
            if (registrations.TryGetValue(attachmentId, out registration)) return true;
            registration = new GatewayChannelRegistration(
                attachmentId,
                "telegram",
                "user-1",
                "bot-1",
                true,
                [],
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }

        public GatewayChannelRegistration[] GetActiveChatRegistrations(string userId)
            => [.. registrations.Values.Where(registration => registration.OwnerUserId == userId
                && registration.IsActive && ChannelAttachmentIds.IsChat(registration.Id))];

        public bool TryResolveIdentity(string attachmentId, string externalUserId, string chatId, out GatewayChannelIdentityLink? identity)
        {
            identity = null;
            return false;
        }

        public bool TryResolveActiveRegistration(string userId, out string? registrationId, out string? externalUserId)
        {
            registrationId = "reg-1";
            externalUserId = "tg-user-1";
            return true;
        }

        public GatewayChannelTargetBinding[] GetBindings(string attachmentId) => [];
        public GatewayChannelTargetBinding? GetDefaultBinding(string attachmentId) => null;
        public GatewayUserListItem BuildUserPayload(GatewayUser user) => throw new NotSupportedException();
        public Task<GatewayUserChannelsResponse> BuildUserChannelCatalogAsync(GatewayUser user, GatewayChannelSetupPayload? setup, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ChannelSettingsUpdateResult?> UpdateUserChannelSettingsAsync(GatewayUser user, string providerKey, JsonElement body, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetActiveTargetForUserAsync(string attachmentId, string externalUserId, string targetType, string targetId, CancellationToken cancellationToken)
        {
            if (TargetUnavailable)
                throw new InvalidOperationException("Selected channel target is no longer available.");
            ActiveSelection = (targetType, targetId);
            return Task.CompletedTask;
        }
        public Task<bool> BindExternalIdentityAsync(string attachmentId, string ownerUserId, string externalUserId, string conversationId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string?> GetSlackPairingFailureReasonAsync(string ownerUserId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public ChannelHitlTetherMode GetChannelHitlTetherMode(string userId, string? sessionId) => ChannelHitlTetherMode.Hitl;
    }

    private sealed class RecordingAssetUploadService : IChannelOutboundAssetUploadService
    {
        public List<(string RegistrationId, string ConversationId, string? ThreadId)> Destinations { get; } = [];

        public Task<UploadOutboundAssetResponse> UploadAsync(
            string sessionId,
            string ownerUserId,
            string assetId,
            string attachmentId,
            string conversationId,
            string? conversationThreadId,
            CancellationToken cancellationToken)
        {
            _ = sessionId;
            _ = ownerUserId;
            _ = assetId;
            _ = cancellationToken;
            Destinations.Add((attachmentId, conversationId, conversationThreadId));
            return Task.FromResult(new UploadOutboundAssetResponse
            {
                Accepted = true,
                UploadId = "upload-" + attachmentId,
            });
        }
    }

    private sealed class FakeChannelRoutingCoordinator : IChannelRoutingCoordinator
    {
        public string? Topic { get; set; }
        public ChannelConversationMapEntry ResolveConversationMap(string registrationId, GatewayChannelTargetBinding targetBinding, ChannelInboundEnvelope envelope) => throw new NotSupportedException();
        public GatewayChannelTargetBinding? ResolveTargetBinding(GatewayChannelRegistration registration, string externalUserId, string? messageThreadId) => throw new NotSupportedException();
        public (string TargetType, string TargetId) ResolveCurrentTargetSelection(GatewayChannelRegistration registration, string externalUserId) => throw new NotSupportedException();
        public string ResolveTargetDisplayName(string targetType, string targetId) => "Patio Planner";
        public string? ResolveTargetColor(string targetType, string targetId) => null;
        public string BuildPickerLabel(string targetType, bool selected, string displayName) => throw new NotSupportedException();
        public bool HasThreadTargetBinding(GatewayChannelRegistration registration, string? messageThreadId) => false;
        public string? ResolveTargetThreadId(GatewayChannelRegistration registration, string targetType, string targetId) => Topic;
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
