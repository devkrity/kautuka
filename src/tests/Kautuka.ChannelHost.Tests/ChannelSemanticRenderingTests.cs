using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

public sealed class ChannelSemanticRenderingTests
{
    [Fact]
    public void NativeDetailsRenderCollapsedWithEscapedSessionAndPromptTextAndInlineControls()
    {
        var document = new ChannelSemanticMessageDocument(ChannelSemanticContractVersions.MessageDocument, "session_status", null, null,
        [new(ChannelSemanticBlockTypes.Details, Text: "2 sessions · 3 waiting", Blocks: [
            new(ChannelSemanticBlockTypes.Heading, Text: "Invoices <admin>"),
            new(ChannelSemanticBlockTypes.Section, Text: "1. Check <script> & totals", Actions: [
                new("remove-1", "button", "Remove", "ui:opaque-token")])])]);
        string json = JsonSerializer.Serialize(new ChannelSemanticMessageAction("123", "101", document), ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        ChannelSemanticMessageAction action = JsonSerializer.Deserialize(json, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        string markdown = ChannelSemanticRendering.RenderTelegram(action)["rich_message"]!["markdown"]!.GetValue<string>();
        Assert.Contains("<details><summary>2 sessions &#183; 3 waiting</summary>", markdown, StringComparison.Ordinal);
        Assert.Contains("Invoices &lt;admin&gt;", markdown, StringComparison.Ordinal);
        Assert.Contains("Check &lt;script&gt; &amp; totals", markdown, StringComparison.Ordinal);
        Assert.Contains("data=\"ui:opaque-token\"", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<details open>", markdown, StringComparison.Ordinal);
        Assert.Contains("Check <script> & totals", ChannelSemanticRendering.GetFallbackText(document), StringComparison.Ordinal);
    }

    [Fact]
    public void NestedDetailsDoNotBypassActionValidation()
    {
        var document = new ChannelSemanticMessageDocument(ChannelSemanticContractVersions.MessageDocument, "session_status", null, "text",
        [new(ChannelSemanticBlockTypes.Details, Text: "Details", Blocks: [
            new(ChannelSemanticBlockTypes.Section, Text: "Unsafe", Actions: [new("action", "button", "Remove", new string('x', 65))])])]);
        Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderTelegram(new("123", "101", document)));
    }

    [Fact]
    public void TelegramReplyKeyboardKeepsQueueVisibleAlongsideAgentAndSessionControls()
    {
        JsonObject keyboard = ChannelSemanticRendering.CreateTelegramReplyKeyboardMarkup();
        JsonArray rows = Assert.IsType<JsonArray>(keyboard["keyboard"]);
        Assert.True(keyboard["is_persistent"]!.GetValue<bool>());
        Assert.Contains(rows.SelectMany(row => row!.AsArray()), button => button!.GetValue<string>() == "📥 Queue");
        Assert.Contains(rows.SelectMany(row => row!.AsArray()), button => button!.GetValue<string>() == "👥 Agents");
        Assert.Contains(rows.SelectMany(row => row!.AsArray()), button => button!.GetValue<string>() == "🗂 Sessions");
    }

    [Fact]
    public void TelegramMirroredPromptHasItsOwnParagraphAndExplicitPrefix()
    {
        ChannelSemanticMessageDocument document = ChannelMessageTemplates.WithSession(
            ChannelMessageTemplates.UserMessage("roll d88"), "Dice");
        string markdown = ChannelSemanticRendering.RenderTelegram(new("chat", "101", document))["rich_message"]!["markdown"]!.GetValue<string>();
        Assert.Contains("_Session: Dice_" + Environment.NewLine + Environment.NewLine + "Prompt: roll d88", markdown, StringComparison.Ordinal);
        Assert.Equal("roll d88", document.Blocks[1].Text);
    }

    [Fact]
    public void TelegramEscapesUntrustedSectionsFieldsAndTableCellsBeforeRichMarkdownParsing()
    {
        ChannelSemanticMessageDocument document = new(
            ChannelSemanticContractVersions.MessageDocument,
            "agent_response",
            null,
            null,
            [
                new(ChannelSemanticBlockTypes.Section, Text: "<tg-button type=\"url\" data=\"https://evil.invalid\">Approve</tg-button> & hi"),
                new(ChannelSemanticBlockTypes.Fields, Fields: [new("Details", "<tg-button>Approve</tg-button>")]),
                new(ChannelSemanticBlockTypes.Table, TableHeaders: ["Value"], TableRows: [new(["<tg-button>Approve</tg-button>"])])
            ]);

        string markdown = ChannelSemanticRendering.RenderTelegram(new("chat", null, document))["rich_message"]!["markdown"]!.GetValue<string>();

        Assert.Contains("&lt;tg-button", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<tg-button type=\"url\"", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<tg-button>Approve", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionNameAppearsBelowAgentAttributionAndIsEscapedAsContext()
    {
        ChannelSemanticMessageDocument document = ChannelMessageTemplates.WithSession(
            ChannelMessageTemplates.AgentResponse("The result is 55.", "My Assistant"), "Invoices *today*");
        JsonObject rendered = ChannelSemanticRendering.RenderTelegram(new("chat", "101", document));
        string wire = rendered.ToJsonString();
        Assert.Contains("Session:", wire, StringComparison.Ordinal);
        Assert.Contains("Invoices", wire, StringComparison.Ordinal);
        Assert.Equal(ChannelSemanticBlockTypes.Context, document.Blocks[0].Type);
        Assert.Equal("My Assistant", document.SenderDisplayName);
        Assert.Contains("Session: Invoices *today*", document.FallbackText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:48231/slack/socket", true)]
    [InlineData("ws://127.0.0.1:48232/slack/socket", false)]
    [InlineData("ws://127.0.0.1:48231/other", false)]
    [InlineData("ws://localhost:48231/slack/socket", false)]
    [InlineData("ws://example.invalid:48231/slack/socket", false)]
    [InlineData("ws://user@127.0.0.1:48231/slack/socket", false)]
    [InlineData("wss://wss-primary.slack.com/socket", false)]
    public void SimulatorSlackSocketAllowsOnlyConfiguredEndpoint(string value, bool allowed)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kautuka:Channels:TransportProfile"] = "Simulator",
            ["Kautuka:Channels:SimulatorBaseAddress"] = "http://127.0.0.1:48231/",
        }).Build();
        var profile = new ChannelProviderTransportProfile(configuration, new TestHostEnvironment("Test"));
        Assert.Equal(allowed, profile.IsSimulatorSlackSocketUri(new Uri(value)));
        var official = new ChannelProviderTransportProfile(new ConfigurationBuilder().Build(), new TestHostEnvironment("Production"));
        Assert.False(official.IsSimulatorSlackSocketUri(new Uri(value)));
    }

    [Fact]
    public void SimulatorTransportIsDeniedOutsideTestOrDevelopment()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kautuka:Channels:TransportProfile"] = "Simulator",
            ["Kautuka:Channels:SimulatorBaseAddress"] = "http://127.0.0.1:48231/",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => new ChannelProviderTransportProfile(configuration, new TestHostEnvironment("Production")));
    }

    private static System.Xml.Linq.XElement[] RichButtons(JsonObject message)
        => System.Text.RegularExpressions.Regex.Matches(message["rich_message"]!["markdown"]!.GetValue<string>(), "<tg-button-row>.*?</tg-button-row>")
            .Select(match => System.Xml.Linq.XElement.Parse(match.Value)).SelectMany(row => row.Elements("tg-button")).ToArray();

    [Fact]
    public void TelegramRichButtonsStayBesideTheirContentEscapeLabelsAndPackEightPerRow()
    {
        ChannelSemanticAction[] buttons = [.. Enumerable.Range(0, 9).Select(index =>
            new ChannelSemanticAction($"remove-{index}", ChannelSemanticActionTypes.Button, "Remove <this> & \"that\"", $"ui:{index}"))];
        JsonObject rendered = ChannelSemanticRendering.RenderTelegram(Build([
            new(ChannelSemanticBlockTypes.Section, Text: "Queued prompt"), new(ChannelSemanticBlockTypes.Actions, Actions: buttons),
            new(ChannelSemanticBlockTypes.Heading, Text: "Next session")]));
        string markdown = rendered["rich_message"]!["markdown"]!.GetValue<string>();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(markdown, "<tg-button-row>"));
        Assert.Equal(9, RichButtons(rendered).Length);
        Assert.All(RichButtons(rendered), button => Assert.Equal("Remove <this> & \"that\"", button.Value));
        Assert.All(RichButtons(rendered), button => Assert.Equal("link", button.Attribute("style")!.Value));
        Assert.True(markdown.IndexOf("Queued prompt", StringComparison.Ordinal) < markdown.IndexOf("<tg-button-row>", StringComparison.Ordinal));
        Assert.True(markdown.LastIndexOf("</tg-button-row>", StringComparison.Ordinal) < markdown.IndexOf("Next session", StringComparison.Ordinal));
        Assert.Null(rendered["reply_markup"]);
    }

    [Fact]
    public void TelegramOrdinaryMessagesSetPersistentKeyboardButActionCardsDoNotAttachIt()
    {
        JsonObject ordinary = ChannelSemanticRendering.RenderTelegram(new("chat", null,
            ChannelMessageTemplates.AgentResponse("Done.")));
        JsonObject actionCard = ChannelSemanticRendering.RenderTelegram(new("chat", null,
            ChannelMessageTemplates.ApprovalRequest("Permission required", "Allow?", "approve-token", "deny-token")));

        Assert.True(ordinary["reply_markup"]!["is_persistent"]!.GetValue<bool>());
        Assert.Null(actionCard["reply_markup"]);
    }

    [Fact]
    public void SlackOrdinaryMessagesDoNotRepeatQuickActionsAndActionCardsKeepTheirOwnControls()
    {
        JsonArray ordinaryBlocks = Assert.IsType<JsonArray>(ChannelSemanticRendering.RenderSlack(new("C1", null,
            ChannelMessageTemplates.AgentResponse("Done.")))["blocks"]);
        Assert.DoesNotContain(ordinaryBlocks.OfType<JsonObject>(), block =>
            block["block_id"]?.GetValue<string>() == "kautuka_quick_actions");
        Assert.Single(ChannelSemanticRendering.CreateSlackTextBlocks("Done.", null));

        JsonArray approvalBlocks = Assert.IsType<JsonArray>(ChannelSemanticRendering.RenderSlack(new("C1", null,
            ChannelMessageTemplates.ApprovalRequest("Permission required", "Allow?", "hitl:one:approve", "hitl:one:deny")))["blocks"]);
        Assert.DoesNotContain(approvalBlocks.OfType<JsonObject>(), block =>
            block["block_id"]?.GetValue<string>() == "kautuka_quick_actions");
    }

    [Fact]
    public void TelegramRichMessagePreservesLongMarkdownAndSenderAttribution()
    {
        string body = "**Bold**\n\n" + new string('x', 5_000);
        ChannelSemanticMessageAction action = new("chat", "101", ChannelMessageTemplates.AgentResponse(body, "Agent *one*"));
        JsonObject rendered = ChannelSemanticRendering.RenderTelegram(action);
        string markdown = rendered["rich_message"]!["markdown"]!.GetValue<string>();
        Assert.Contains("**Bold**", markdown, StringComparison.Ordinal);
        Assert.Contains(new string('x', 200), markdown, StringComparison.Ordinal);
        Assert.Contains("Agent *one*", markdown, StringComparison.Ordinal);
        Assert.Null(rendered["message_thread_id"]);
        Assert.Null(rendered["parse_mode"]);
    }

    [Fact]
    public void ProviderBudgetRejectsOversizeOpaqueCallbackRatherThanTruncatingIt()
    {
        ChannelSemanticMessageAction action = Build([new(ChannelSemanticBlockTypes.Actions, Actions: [new("approve", ChannelSemanticActionTypes.Button, "Approve", new string('a', 65), Required: true)])]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderTelegram(action));

        Assert.Equal("semantic_callback_token_too_long", exception.Message);
    }

    [Fact]
    public void SlackNativeLimitsAndSecurityCriticalFallbackFailBeforeProviderTruncation()
    {
        ChannelSemanticMessageAction oversizedSection = Build([new(ChannelSemanticBlockTypes.Section, Text: new string('x', 3_001))]);
        Assert.Equal("slack_section_text_limit_exceeded", Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderSlack(oversizedSection)).Message);

        ChannelSemanticMessageAction critical = new("conversation", null,
            new(ChannelSemanticContractVersions.MessageDocument, "approval", null, new string('x', 40_001),
                [new(ChannelSemanticBlockTypes.Actions, Actions: [new("approve", ChannelSemanticActionTypes.Button, "Approve", "hitl:one:approved", Required: true)])], TerminalState: "pending"));
        Assert.Equal("semantic_security_critical_content_too_large", Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderSlack(critical)).Message);
    }

    [Fact]
    public void RequiredInteractionIdentityCannotBeDuplicated()
    {
        ChannelSemanticAction duplicate = new("decision", ChannelSemanticActionTypes.Button, "Approve", "opaque-1", Required: true);
        ChannelSemanticMessageAction action = Build([new(ChannelSemanticBlockTypes.Actions, Actions: [duplicate, duplicate with { CallbackToken = "opaque-2" }])]);

        Assert.Equal("duplicate_semantic_action_id", Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderSlack(action)).Message);
    }

    [Fact]
    public void WhatsAppRemainsUnadmittedWhilePollingCompatibleTransportIsDeferred()
    {
        ChannelProviderAdmissionDecision decision = ChannelProviderAdmissionPolicy.Evaluate("whatsapp");

        Assert.False(decision.Admitted);
        Assert.Equal("whatsapp_chg019_deferred_pending_official_polling_compatible_transport", decision.Reason);
    }

    [Fact]
    public void SemanticImageRejectsCredentialBearingOrSignedUrls()
    {
        ChannelSemanticMessageAction action = Build([new(ChannelSemanticBlockTypes.Image, ImageUrl: new Uri("https://user:secret@example.test/image.png?token=secret"), AltText: "unsafe")]);

        Assert.Equal("semantic_image_uri_not_safe", Assert.Throws<InvalidOperationException>(() => ChannelSemanticRendering.RenderSlack(action)).Message);
    }

    [Fact]
    public void DurableInteractionRoundTripPreservesEveryAuthorizationBinding()
    {
        ChannelDurableInteractionDocument interaction = new(
            ChannelSemanticContractVersions.Interaction, "interaction-1", "approve", "opaque-token", "approved",
            ChannelSemanticContractVersions.MessageDocument, DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "tenant-1", "owner-1", "registration-1", "conversation-1", "thread-1", "session-1", "approval", "approval-1", ["approved", "denied"], null);

        string json = JsonSerializer.Serialize(interaction, ChannelContractsJsonContext.Default.ChannelDurableInteractionDocument);
        ChannelDurableInteractionDocument? restored = JsonSerializer.Deserialize(json, ChannelContractsJsonContext.Default.ChannelDurableInteractionDocument);

        Assert.NotNull(restored);
        Assert.Equal(interaction.InteractionId, restored.InteractionId);
        Assert.Equal(interaction.TenantId, restored.TenantId);
        Assert.Equal(interaction.OwnerUserId, restored.OwnerUserId);
        Assert.Equal(interaction.RegistrationId, restored.RegistrationId);
        Assert.Equal(interaction.ConversationId, restored.ConversationId);
        Assert.Equal(interaction.SessionId, restored.SessionId);
        Assert.Equal(interaction.ApprovalRequestId, restored.ApprovalRequestId);
        Assert.Equal(interaction.AllowedValues, restored.AllowedValues);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StreamDeliveryIdentityAndSequenceSurviveStoreRecreation()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-channel-stream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelAttachmentStore first = new(database);
            _ = first.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = "slack-1",
                ProviderKey = "slack",
                ExpectedSchemaVersion = "slack/v1",
                Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Slack test" } },
            });
            first.RecordStreamDelivery("slack-1", "stream-1", "message-1", 4, false);

            ChannelStreamDelivery? restored = new ChannelAttachmentStore(database).GetStreamDelivery("slack-1", "stream-1");

            Assert.NotNull(restored);
            Assert.Equal("message-1", restored.TransportMessageId);
            Assert.Equal(4, restored.LastSequence);
            Assert.False(restored.IsFinal);
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public void SlackSlashCommandNormalizesToDurableCommandEvent()
    {
        using JsonDocument document = JsonDocument.Parse("""{"user_id":"U1","channel_id":"C1","command":"/agent","text":"status","response_url":"https://hooks.slack.test/secret"}""");
        var message = SlackSocketModeWorker.TryCreateSlashCommandMessageForTest("registration", "owner", document.RootElement, "envelope-1");
        Assert.NotNull(message);
        Assert.Equal("command", message.EventType);
        Assert.Equal("/agent status", message.Text);
        Assert.Equal("envelope-1", message.EventId);
        Assert.Equal("{}", message.PayloadJson);
        Assert.DoesNotContain("secret", message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SlackFileDownloadRejectsAHostThatOnlyEndsWithSlackCom()
    {
        Assert.True(SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(new Uri("https://files.slack.com/files-pri/T1/download/report.txt")));
        Assert.True(SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(new Uri("https://slack.com/files/report.txt")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(new Uri("https://notslack.com/steal")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(new Uri("https://slack.com.evil.example/steal")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(new Uri("http://files.slack.com/files-pri/T1/download/report.txt")));
        Assert.True(SlackFileDownloadPolicy.IsSlackOwnedWebSocketUri(new Uri("wss://wss-primary.slack.com/link/?ticket=1")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedWebSocketUri(new Uri("wss://notslack.com/link/?ticket=1")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedWebSocketUri(new Uri("ws://wss-primary.slack.com/link/?ticket=1")));
        Assert.False(SlackFileDownloadPolicy.IsSlackOwnedWebSocketUri(new Uri("wss://slack.com.evil.example/link/?ticket=1")));
    }

    [Fact]
    public async Task SlackFileDownloadDoesNotSendTheBotTokenToALookalikeHost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-slack-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = "slack-1",
                ProviderKey = "slack",
                ExpectedSchemaVersion = "slack/v1",
                Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner" } },
            });
            credentials.Replace("slack-1", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" }]);
            using var handler = new LookalikeSlackDownloadHandler();
            using var factory = new SingleClientFactory(handler);
            using var ingress = new ChannelIngressStore(database);
            ChannelOutboundAssetStore assets = new(database, configuration);
            var worker = new SlackSocketModeWorker(attachments, credentials, ingress, assets, factory, NullLogger<SlackSocketModeWorker>.Instance);

            var chunks = new List<ReadInboundMediaChunk>();
            await foreach (ReadInboundMediaChunk chunk in worker.ReadInboundMediaAsync("slack-1", new ReadInboundMediaRequest { MediaId = "F1" }, CancellationToken.None).ConfigureAwait(true))
            {
                chunks.Add(chunk);
            }

            Assert.Empty(chunks);
            Assert.Contains(handler.RequestedUris, static uri => uri.Contains("https://slack.com/api/files.info", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.RequestedUris, static uri => uri.Contains("notslack.com", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public async Task SlackFileUploadDoesNotPostTheFileToALookalikeHost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-slack-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = "slack-1",
                ProviderKey = "slack",
                ExpectedSchemaVersion = "slack/v1",
                Settings =
                {
                    new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                    new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D1" },
                    new AttachmentSettingValue { FieldKey = "bot_user_id", StringValue = "U_BOT" },
                },
            });
            credentials.Replace("slack-1", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" }]);
            ChannelOutboundAssetStore outbound = new(database, configuration);
            byte[] bytes = "verified file"u8.ToArray();
            var metadata = new UploadOutboundAssetMetadata
            {
                Context = new ScopedRequestContext { ActorUserId = "owner-1", TenantId = "tenant-1" },
                AssetId = "asset-1",
                AttachmentId = "slack-1",
                ConversationId = "D1",
                SafeFileName = "report.txt",
                ContentType = "text/plain",
                DeclaredSizeBytes = bytes.Length,
                Sha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                IdempotencyKey = "file-1",
            };
            ChannelOutboundAssetUploadResult upload = await outbound.ReceiveAsync(metadata, SingleChunk(bytes), CancellationToken.None).ConfigureAwait(true);
            using var handler = new LookalikeSlackUploadHandler();
            using var factory = new SingleClientFactory(handler);
            using var ingress = new ChannelIngressStore(database);
            var worker = new SlackSocketModeWorker(attachments, credentials, ingress, outbound, factory, NullLogger<SlackSocketModeWorker>.Instance);
            ChannelOutboundFileAction action = new(upload.UploadId, "D1", null, "report.txt", "text/plain", bytes.Length, "Report", null);
            var request = new SendOutboundMessageRequest
            {
                Content = new OutboundContentEnvelope
                {
                    ActionType = "send_file",
                    PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelOutboundFileAction),
                },
            };

            SendOutboundMessageResponse result = await worker.SendOutboundMessageAsync("slack-1", request, CancellationToken.None).ConfigureAwait(true);

            Assert.False(result.Accepted);
            Assert.Equal("slack_upload_url_rejected", result.Detail);
            Assert.DoesNotContain(handler.RequestedUris, static uri => uri.Contains("notslack.com", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(handler.Bodies, static body => body.Contains("verified file", StringComparison.Ordinal));
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public async Task SlackEphemeralDeliveryUsesNativeUserScopedEndpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-channel-ephemeral-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration); database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = "slack-1", ProviderKey = "slack", ExpectedSchemaVersion = "slack/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Slack" }, new AttachmentSettingValue { FieldKey = "bot_user_id", StringValue = "B1" }, new AttachmentSettingValue { FieldKey = "slack_owner_user_id", StringValue = "U1" }, new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D1" } } });
            credentials.Replace("slack-1", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" }]);
            using var handler = new CapturingSlackHandler();
            using var factory = new SingleClientFactory(handler);
            using var ingress = new ChannelIngressStore(database);
            var worker = new SlackSocketModeWorker(attachments, credentials, ingress, new ChannelOutboundAssetStore(database, configuration), factory, NullLogger<SlackSocketModeWorker>.Instance);
            ChannelEphemeralMessageAction action = new("D1", "123.4", "U1", ChannelMessageTemplates.Error("Delivery failed", "Private failure details"));
            var request = new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "send_ephemeral_message", PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelEphemeralMessageAction) } };

            SendOutboundMessageResponse result = await worker.SendOutboundMessageAsync("slack-1", request, CancellationToken.None).ConfigureAwait(true);

            Assert.True(result.Accepted);
            Assert.EndsWith("/chat.postEphemeral", handler.RequestUri, StringComparison.Ordinal);
            Assert.Contains("\"user\":\"U1\"", handler.Body, StringComparison.Ordinal);
            Assert.Contains("\"thread_ts\":\"123.4\"", handler.Body, StringComparison.Ordinal);

            foreach (string destination in new[] { "U1", "C1", "G1", "D2", "unknown" })
            {
                ChannelEphemeralMessageAction wrongDestination = action with { ConversationId = destination };
                SendOutboundMessageResponse rejected = await worker.SendOutboundMessageAsync("slack-1",
                    new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "send_ephemeral_message", PayloadJson = JsonSerializer.Serialize(wrongDestination, ChannelContractsJsonContext.Default.ChannelEphemeralMessageAction) } },
                    CancellationToken.None).ConfigureAwait(true);
                Assert.False(rejected.Accepted);
                Assert.Equal("slack_private_dm_only", rejected.Detail);
            }

            ChannelEphemeralMessageAction wrongUser = action with { ExternalUserId = "U_OTHER" };
            SendOutboundMessageResponse rejectedWrongUser = await worker.SendOutboundMessageAsync("slack-1",
                new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "send_ephemeral_message", PayloadJson = JsonSerializer.Serialize(wrongUser, ChannelContractsJsonContext.Default.ChannelEphemeralMessageAction) } },
                CancellationToken.None).ConfigureAwait(true);
            Assert.False(rejectedWrongUser.Accepted);
            Assert.Equal("slack_private_dm_only", rejectedWrongUser.Detail);
        }
        finally { ChannelHostSqliteTestCleanup.ReleaseAndDelete(root); }
    }

    [Fact]
    public async Task SlackOutboundNoOpsAreAcceptedWithoutDestinationRejectionWarning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-channel-noop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration); database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = "slack-noop", ProviderKey = "slack", ExpectedSchemaVersion = "slack/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" }, new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D1" } } });
            credentials.Replace("slack-noop", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" }]);
            using var handler = new CapturingSlackHandler(); using var factory = new SingleClientFactory(handler); using var ingress = new ChannelIngressStore(database);
            var logger = new WarningCaptureLogger();
            var worker = new SlackSocketModeWorker(attachments, credentials, ingress, new ChannelOutboundAssetStore(database, configuration), factory, logger);

            SendOutboundMessageResponse result = await worker.SendOutboundMessageAsync("slack-noop",
                new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "acknowledge_callback", PayloadJson = "{}" } }, CancellationToken.None);

            Assert.True(result.Accepted);
            Assert.DoesNotContain(LogLevel.Warning, logger.Levels);
            Assert.Empty(handler.RequestUri);
        }
        finally { ChannelHostSqliteTestCleanup.ReleaseAndDelete(root); }
    }

    private sealed class WarningCaptureLogger : ILogger<SlackSocketModeWorker>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    [Fact]
    public async Task SlackModalUsesNativeViewAndSubmissionRetainsConversationBinding()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-channel-modal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            ChannelHostDatabase database = new(configuration); database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            _ = attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = "slack-1", ProviderKey = "slack", ExpectedSchemaVersion = "slack/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Slack" }, new AttachmentSettingValue { FieldKey = "bot_user_id", StringValue = "B1" }, new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D1" } } });
            credentials.Replace("slack-1", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "bot-token" }]);
            using var handler = new CapturingSlackHandler(); using var factory = new SingleClientFactory(handler); using var ingress = new ChannelIngressStore(database);
            var worker = new SlackSocketModeWorker(attachments, credentials, ingress, new ChannelOutboundAssetStore(database, configuration), factory, NullLogger<SlackSocketModeWorker>.Instance);
            ChannelModalAction action = new("trigger-1", "D1", null, "Approval context", "hitl:interaction-1:approved",
                [new("reason", ChannelSemanticActionTypes.Input, "Reason", "reason-token", Required: true)]);
            var request = new SendOutboundMessageRequest { Content = new OutboundContentEnvelope { ActionType = "open_modal", PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelModalAction) } };

            Assert.True((await worker.SendOutboundMessageAsync("slack-1", request, CancellationToken.None).ConfigureAwait(true)).Accepted);
            Assert.EndsWith("/views.open", handler.RequestUri, StringComparison.Ordinal);
            Assert.Contains("plain_text_input", handler.Body, StringComparison.Ordinal);

            using JsonDocument submission = JsonDocument.Parse("""{"type":"view_submission","user":{"id":"U1"},"view":{"id":"V1","callback_id":"hitl:interaction-1:approved","private_metadata":"{\"conversationId\":\"D1\",\"threadId\":null}","state":{"values":{"reason":{"reason":{"type":"plain_text_input","value":"because"}}}}}}""");
            ChannelInboundEventMessage? inbound = SlackSocketModeWorker.TryCreateInteractiveMessageForTest("slack-1", "owner-1", submission.RootElement, "envelope-1");
            Assert.NotNull(inbound);
            Assert.Equal("D1", inbound.ConversationId);
            Assert.Equal(string.Empty, inbound.ConversationThreadId);
            Assert.Equal("hitl:interaction-1:approved", inbound.CallbackData);
        }
        finally { ChannelHostSqliteTestCleanup.ReleaseAndDelete(root); }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleChunk(byte[] bytes) { yield return bytes; await Task.CompletedTask.ConfigureAwait(false); }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }

    private sealed class CapturingSlackHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"ts\":\"999.1\"}") };
        }
    }

    private sealed class LookalikeSlackUploadHandler : HttpMessageHandler
    {
        public List<string> RequestedUris { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string json = "{\"ok\":true,\"upload_url\":\"https://notslack.com/upload\",\"file_id\":\"F1\"}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class LookalikeSlackDownloadHandler : HttpMessageHandler
    {
        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);

            string json = "{\"ok\":true,\"file\":{\"name\":\"report.txt\",\"mimetype\":\"text/plain\",\"url_private_download\":\"https://notslack.com/steal\"}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static ChannelSemanticMessageAction Build(IReadOnlyList<ChannelSemanticBlock> blocks)
        => new("conversation", null, new(ChannelSemanticContractVersions.MessageDocument, "test", null, "Fallback", blocks));

    private static ChannelSemanticAction[] CanonicalHitlActions() =>
    [
        new("approve", ChannelSemanticActionTypes.Button, "Approve", "hitl:interaction-1:approve", ChannelSemanticStyles.Primary, Required: true),
        new("deny", ChannelSemanticActionTypes.Button, "Deny", "hitl:interaction-1:deny", ChannelSemanticStyles.Danger, Required: true),
        new("stop_run", ChannelSemanticActionTypes.Button, "Stop Run", "hitl:interaction-1:stop_run", ChannelSemanticStyles.Danger, Required: true),
    ];

    private static void AssertNativeAction(
        JsonObject button,
        string expectedLabel,
        string expectedCallback,
        string callbackProperty)
    {
        Assert.Equal(expectedLabel, button["text"]?["text"]?.GetValue<string>());
        Assert.Equal(expectedCallback, button[callbackProperty]?.GetValue<string>());
    }


    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Kautuka.ChannelHost.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

}
