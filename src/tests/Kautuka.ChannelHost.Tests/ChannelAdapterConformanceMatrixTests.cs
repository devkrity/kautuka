using System.Text.Json;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

public sealed class ChannelAdapterConformanceMatrixTests
{
    public static TheoryData<string> AdmittedProviders => new() { "slack", "telegram" };

    public static TheoryData<string, string> SlackCommandMappings
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (SlackSlashCommand command in SlackSetupManifest.SlashCommands)
                data.Add("/kautuka-" + command.Name, command.GatewayCommand);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void IngressNormalizationPreservesIdentityThreadTextAndBoundedMedia(string provider)
    {
        ChannelInboundEventMessage message = NormalizeMessage(provider);

        Assert.Equal(provider, message.ChannelType);
        Assert.Equal($"{provider}-registration", message.RegistrationId);
        Assert.Equal("owner-1", message.OwnerUserId);
        Assert.Equal("user-1", message.ExternalUserId);
        Assert.Equal(provider == "slack" ? "D100" : "100", message.ConversationId);
        Assert.Equal("thread-1", message.ConversationThreadId);
        Assert.Equal("message", message.EventType);
        Assert.Equal("hello from provider", message.Text);
        Assert.Equal("{}", message.PayloadJson);
        Assert.Single(message.InboundMedia);
        Assert.Equal("report.pdf", message.InboundMedia[0].FileName);
        Assert.Equal("application/pdf", message.InboundMedia[0].ContentType);
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void CommandNormalizationDropsProviderCallbackSecrets(string provider)
    {
        ChannelInboundEventMessage command = NormalizeCommand(provider);

        Assert.Equal(provider, command.ChannelType);
        Assert.Equal($"{provider}-registration", command.RegistrationId);
        Assert.Equal("owner-1", command.OwnerUserId);
        Assert.Equal("user-1", command.ExternalUserId);
        Assert.Equal("100", command.ConversationId);
        Assert.Equal("/help status", command.Text);
        Assert.DoesNotContain("response_url", command.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("interaction_token", command.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-secret", command.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/kautuka-queue", "/queue")]
    [InlineData("/kautuka-sessions", "/sessions")]
    [InlineData("/kautuka-agents", "/agents")]
    [InlineData("/kautuka-context", "/context")]
    public void NamespacedSlackCommandsMapToExistingKautukaCommands(string command, string expected)
    {
        using JsonDocument document = JsonDocument.Parse($"{{\"user_id\":\"user-1\",\"channel_id\":\"D1\",\"command\":\"{command}\",\"text\":\"\"}}");

        ChannelInboundEventMessage message = SlackSocketModeWorker.TryCreateSlashCommandMessageForTest(
            "slack-registration", "owner-1", document.RootElement, "envelope-1")!;

        Assert.Equal(expected, message.Text);
    }

    [Theory]
    [MemberData(nameof(SlackCommandMappings))]
    public void EveryRegisteredSlackSlashCommandMapsToItsGatewayCommand(string command, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            user_id = "user-1",
            channel_id = "D1",
            command,
            text = "status",
            response_url = "https://invalid.example/secret",
        }));

        ChannelInboundEventMessage message = SlackSocketModeWorker.TryCreateSlashCommandMessageForTest(
            "slack-registration", "owner-1", document.RootElement, "envelope-1")!;

        Assert.Equal(expected + " status", message.Text);
        Assert.DoesNotContain("invalid.example", message.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void CapabilitiesTruthfullyCoverTheCommonProductionSurface(string provider)
    {
        ChannelProviderCapabilities capabilities = Capabilities(provider);

        Assert.Equal(provider, capabilities.ProviderKey);
        Assert.Equal(ChannelSemanticContractVersions.Capabilities, capabilities.Version);
        Assert.True(capabilities.Streaming);
        Assert.True(capabilities.Edits);
        Assert.True(capabilities.Deletes);
        Assert.True(capabilities.Reactions);
        Assert.True(capabilities.Files);
        Assert.True(capabilities.InboundFiles);
        Assert.True(capabilities.Commands);
        Assert.InRange(capabilities.MaximumMessageCharacters, 1, 40_000);
        Assert.InRange(capabilities.MaximumActions, 1, 100);
        Assert.InRange(capabilities.MaximumCallbackTokenBytes, 1, 2_000);
        Assert.InRange(capabilities.MaximumFileBytes, 1, 26_214_400);
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void ThreadAndReplyTargetingSurvivesNativeRendering(string provider)
    {
        ChannelSemanticMessageAction action = new("100", "42",
            ChannelMessageTemplates.AgentResponse("Thread reply", "Agent"));
        string rendered = Render(provider, action);

        Assert.Equal("100", action.ConversationId);
        Assert.Equal("42", action.ConversationThreadId);
        Assert.Contains("Thread reply", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void RenderingIsDeterministicAndEnforcesProviderBudgets(string provider)
    {
        ChannelSemanticMessageAction action = new("conversation-1", null,
            ChannelMessageTemplates.ApprovalRequest("Approval", "Continue?", "hitl:stable:approve", "hitl:stable:deny"));

        Assert.Equal(Render(provider, action), Render(provider, action));
        ChannelProviderCapabilities capabilities = Capabilities(provider);
        ChannelSemanticMessageAction oversized = action with
        {
            Document = action.Document with
            {
                Blocks = [new ChannelSemanticBlock(ChannelSemanticBlockTypes.Actions, Actions:
                    [.. Enumerable.Range(0, capabilities.MaximumActions + 1).Select(index =>
                        new ChannelSemanticAction($"action-{index}", ChannelSemanticActionTypes.Button, $"Action {index}", $"opaque:{index}"))])],
            },
        };
        Assert.Throws<InvalidOperationException>(() => Render(provider, oversized));
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public async Task DuplicateIngressIsDeduplicatedAndPendingDeliveryReplaysAfterRestart(string provider)
    {
        string root = CreateRoot(provider, "ingress");
        try
        {
            IConfiguration configuration = Configuration(root);
            ChannelInboundEventMessage normalized = NormalizeMessage(provider);
            string firstLease;
            using (ChannelIngressStore first = OpenIngress(configuration))
            {
                Assert.True(first.Enqueue(normalized, "cursor-1"));
                Assert.False(first.Enqueue(normalized, "cursor-1"));
                ChannelInboundEventMessage delivered = await ReadOneAsync(first).ConfigureAwait(true);
                Assert.Equal(normalized.EventId, delivered.EventId);
                Assert.False(string.IsNullOrWhiteSpace(delivered.DeliveryLeaseId));
                firstLease = delivered.DeliveryLeaseId;
            }

            ExpireIngressLeases(configuration);

            using (ChannelIngressStore restarted = OpenIngress(configuration))
            {
                ChannelInboundEventMessage replayed = await ReadOneAsync(restarted).ConfigureAwait(true);
                Assert.Equal(normalized.EventId, replayed.EventId);
                Assert.NotEqual(firstLease, replayed.DeliveryLeaseId);
                Assert.True(restarted.Acknowledge(ChannelIngressStore.GatewaySubscriberId, replayed.EventId, replayed.DeliveryLeaseId));
            }
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void StreamDeliveryIdentityAndSequenceRemainDurableAcrossRestart(string provider)
    {
        string root = CreateRoot(provider, "stream");
        try
        {
            IConfiguration configuration = Configuration(root);
            using (AdapterFixture first = new(provider, configuration))
                first.Attachments.RecordStreamDelivery(first.AttachmentId, "stream-1", "transport-1", 7, false);
            using (AdapterFixture restarted = new(provider, configuration, createAttachment: false))
            {
                ChannelStreamDelivery? delivery = restarted.Attachments.GetStreamDelivery(restarted.AttachmentId, "stream-1");
                Assert.NotNull(delivery);
                Assert.Equal("transport-1", delivery.TransportMessageId);
                Assert.Equal(7, delivery.LastSequence);
                Assert.False(delivery.IsFinal);
            }
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public async Task MalformedPayloadAndProviderFailureNeverLeakRuntimeCredentials(string provider)
    {
        string root = CreateRoot(provider, "secrets");
        try
        {
            using AdapterFixture fixture = new(provider, Configuration(root));
            SendOutboundMessageResponse invalid = await fixture.Adapter.SendOutboundMessageAsync(fixture.AttachmentId,
                new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "send_text", PayloadJson = "{not-json" } },
                CancellationToken.None).ConfigureAwait(true);

            Assert.False(invalid.Accepted);
            Assert.DoesNotContain(AdapterFixture.Secret, invalid.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(AdapterFixture.Secret, string.Join('\n', fixture.Handler.RequestBodies), StringComparison.Ordinal);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void RequiredInteractionRendersWithoutLosingOpaqueDecision(string provider)
    {
        ChannelSemanticMessageAction action = new("conversation", null,
            ChannelMessageTemplates.ApprovalRequest("Approval", "Continue?", "hitl:one:approved", "hitl:one:denied"));
        string rendered = Render(provider, action);
        Assert.Contains("hitl:one:approved", rendered, StringComparison.Ordinal);
        Assert.Contains("hitl:one:denied", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public void CanonicalHitlActionSetSurvivesEveryProviderBoundary(string provider)
    {
        ChannelSemanticAction[] actions =
        [
            new("approve", ChannelSemanticActionTypes.Button, "Approve", "hitl:interaction-1:approve", ChannelSemanticStyles.Primary, Required: true),
            new("deny", ChannelSemanticActionTypes.Button, "Deny", "hitl:interaction-1:deny", ChannelSemanticStyles.Danger, Required: true),
            new("stop_run", ChannelSemanticActionTypes.Button, "Stop Run", "hitl:interaction-1:stop_run", ChannelSemanticStyles.Danger, Required: true),
        ];
        ChannelSemanticMessageAction action = new(
            "conversation",
            null,
            new ChannelSemanticMessageDocument(
                ChannelSemanticContractVersions.MessageDocument,
                "approval_request",
                "Agent",
                "Approval required",
                [new(ChannelSemanticBlockTypes.Actions, Actions: actions)]));

        string rendered = Render(provider, action);

        foreach (ChannelSemanticAction expected in actions)
        {
            Assert.Contains(expected.Label, rendered, StringComparison.Ordinal);
            Assert.Contains(expected.CallbackToken, rendered, StringComparison.Ordinal);
        }
    }

    private static string Render(string provider, ChannelSemanticMessageAction action) => provider switch
    {
        "slack" => ChannelSemanticRendering.RenderSlack(action).ToJsonString(),
        _ => ChannelSemanticRendering.RenderTelegram(action).ToJsonString(),
    };

    private static ChannelProviderCapabilities Capabilities(string provider) => provider switch
    {
        "slack" => ChannelProviderCapabilityCatalog.Slack,
        _ => ChannelProviderCapabilityCatalog.Telegram,
    };

    private static ChannelInboundEventMessage NormalizeMessage(string provider)
    {
        using JsonDocument document = JsonDocument.Parse(provider switch
        {
            "slack" => """{"event_id":"event-1","event":{"type":"message","channel_type":"im","user":"user-1","channel":"D100","thread_ts":"thread-1","text":"hello from provider","files":[{"id":"file-1","name":"report.pdf","mimetype":"application/pdf","size":123}]}}""",
            _ => """{"update_id":1,"message":{"message_id":2,"from":{"id":"user-1"},"chat":{"id":"100"},"message_thread_id":"thread-1","text":"hello from provider","document":{"file_id":"file-1","file_unique_id":"unique-1","file_name":"report.pdf","mime_type":"application/pdf","file_size":123}}}""",
        });
        if (provider == "slack")
            return SlackSocketModeWorker.TryCreateEventsApiMessageForTest("slack-registration", "owner-1", document.RootElement)!;
        ChannelInboundEvent telegram = TelegramLongPollingWorker.CreateInboundEventForTest("telegram-registration", "owner-1", document.RootElement)!;
        ChannelInboundEventMessage message = new()
        {
            EventId = telegram.EventId,
            ChannelType = telegram.ChannelType,
            RegistrationId = telegram.RegistrationId,
            OwnerUserId = telegram.OwnerUserId ?? string.Empty,
            ExternalUserId = telegram.ExternalUserId,
            ConversationId = telegram.ConversationId,
            ConversationThreadId = telegram.ConversationThreadId ?? string.Empty,
            EventType = telegram.EventType,
            Text = telegram.Text ?? string.Empty,
            PayloadJson = "{}",
        };
        message.InboundMedia.AddRange((telegram.InboundMedia ?? []).Select(media => new InboundMediaDescriptorMessage
        {
            MediaId = media.MediaId,
            ProviderUniqueId = media.ProviderUniqueId ?? string.Empty,
            MediaKind = media.MediaKind,
            FileName = media.FileName ?? string.Empty,
            ContentType = media.ContentType ?? string.Empty,
            SizeBytes = media.SizeBytes ?? 0,
        }));
        return message;
    }

    private static ChannelInboundEventMessage NormalizeCommand(string provider)
    {
        using JsonDocument document = JsonDocument.Parse(provider switch
        {
            "slack" => """{"user_id":"user-1","channel_id":"100","command":"/help","text":"status","response_url":"provider-secret"}""",
            _ => """{"update_id":2,"message":{"from":{"id":"user-1"},"chat":{"id":"100"},"text":"/help status"}}""",
        });
        if (provider == "slack") return SlackSocketModeWorker.TryCreateSlashCommandMessageForTest("slack-registration", "owner-1", document.RootElement, "envelope-1")!;
        ChannelInboundEvent telegram = TelegramLongPollingWorker.CreateInboundEventForTest("telegram-registration", "owner-1", document.RootElement)!;
        return new ChannelInboundEventMessage { EventId = telegram.EventId, ChannelType = telegram.ChannelType, RegistrationId = telegram.RegistrationId, OwnerUserId = telegram.OwnerUserId ?? string.Empty, ExternalUserId = telegram.ExternalUserId, ConversationId = telegram.ConversationId, EventType = telegram.EventType, Text = telegram.Text ?? string.Empty, PayloadJson = "{}" };
    }

    private static IConfiguration Configuration(string root) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
    private static ChannelIngressStore OpenIngress(IConfiguration configuration)
    {
        ChannelHostDatabase database = new(configuration); database.EnsureReady(); return new ChannelIngressStore(database);
    }
    private static void ExpireIngressLeases(IConfiguration configuration)
    {
        ChannelHostDatabase database = new(configuration); database.EnsureReady();
        using Microsoft.Data.Sqlite.SqliteConnection connection = database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE channel_ingress_outbox SET delivery_lease_expires_at_utc=$expired";
        command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        _ = command.ExecuteNonQuery();
    }
    private static string CreateRoot(string provider, string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-conformance-{scenario}-{provider}-{Guid.NewGuid():N}"); Directory.CreateDirectory(root); return root;
    }
    private static void Cleanup(string root)
    {
        ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
    }
    private static async Task<ChannelInboundEventMessage> ReadOneAsync(ChannelIngressStore store)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        await foreach (ChannelInboundEventMessage message in store.StreamAsync(ChannelIngressStore.GatewaySubscriberId, timeout.Token).ConfigureAwait(false)) return message;
        throw new InvalidOperationException("conformance_ingress_not_delivered");
    }

    private sealed class AdapterFixture : IDisposable
    {
        public const string Secret = "provider-secret-never-leak";
        private readonly ChannelIngressStore ingress;
        private readonly ChannelOutboundAssetStore assets;
        private readonly SingleClientFactory factory;
        public string AttachmentId { get; }
        public ChannelAttachmentStore Attachments { get; }
        public IChannelAdapter Adapter { get; }
        public CapturingProviderHandler Handler { get; }

        public AdapterFixture(string provider, IConfiguration configuration, bool createAttachment = true)
        {
            ChannelHostDatabase database = new(configuration); database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new(); Attachments = new(database, credentials); ingress = new(database); assets = new(database, configuration);
            AttachmentId = $"{provider}-conformance";
            if (createAttachment)
                _ = Attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = AttachmentId, ProviderKey = provider, ExpectedSchemaVersion = $"{provider}/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = provider }, new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "conversation-1" } } });
            credentials.Replace(AttachmentId, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = Secret }, new RuntimeCredentialValue { FieldKey = "app_token", SecretValue = Secret }]);
            Handler = new CapturingProviderHandler(); factory = new SingleClientFactory(Handler);
            Adapter = provider switch
            {
                "slack" => new SlackSocketModeWorker(Attachments, credentials, ingress, assets, factory, NullLogger<SlackSocketModeWorker>.Instance),
                _ => new TelegramLongPollingWorker(Attachments, ingress, assets, factory, NullLogger<TelegramLongPollingWorker>.Instance),
            };
        }
        public void Dispose() { factory.Dispose(); ingress.Dispose(); }
    }

    private sealed class CapturingProviderHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("{\"error\":\"sanitized\"}") };
        }
    }

    [Theory]
    [MemberData(nameof(AdmittedProviders))]
    public async Task UnsupportedActionFailsClosedAcrossEveryAdapter(string provider)
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-conformance-{provider}-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration); database.EnsureReady(); ChannelRuntimeCredentialStore credentials = new(); ChannelAttachmentStore attachments = new(database, credentials); using ChannelIngressStore ingress = new(database); ChannelOutboundAssetStore assets = new(database, configuration);
            string attachmentId = $"{provider}-1";
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = attachmentId, ProviderKey = provider, ExpectedSchemaVersion = $"{provider}/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner" }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = provider }, new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "100" } } });
            credentials.Replace(attachmentId, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "token" }, new RuntimeCredentialValue { FieldKey = "app_token", SecretValue = "app-token" }]);
            using var handler = new RejectUnexpectedHttpHandler(); using var factory = new SingleClientFactory(handler);
            IChannelAdapter adapter = provider switch
            {
                "slack" => new SlackSocketModeWorker(attachments, credentials, ingress, assets, factory, NullLogger<SlackSocketModeWorker>.Instance),
                _ => new TelegramLongPollingWorker(attachments, ingress, assets, factory, NullLogger<TelegramLongPollingWorker>.Instance),
            };
            SendOutboundMessageResponse response = await adapter.SendOutboundMessageAsync(attachmentId,
                new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "unsupported_conformance_action", PayloadJson = "{}" } }, CancellationToken.None).ConfigureAwait(true);
            Assert.False(response.Accepted);
            Assert.Equal(DeliveryStatus.Rejected, response.DeliveryStatus);
            Assert.Contains("unsupported", response.Detail, StringComparison.Ordinal);
            Assert.Equal(0, handler.CallCount);
        }
        finally { Cleanup(root); }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler, false);
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }

    private sealed class RejectUnexpectedHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
