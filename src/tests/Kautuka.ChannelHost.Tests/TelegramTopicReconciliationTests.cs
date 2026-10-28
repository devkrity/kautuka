using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

public sealed class TelegramTopicReconciliationTests
{
    [Fact]
    public async Task SessionCardReplacementDeletesPreviousCardWithoutPinning()
    {
        using var fixture = new Fixture();
        var factsExecutor = fixture.SessionFactsExecutor;

        async Task<SendOutboundMessageResponse> Publish(long revision)
        {
            var selected = new ChannelSelectedSessionFact(ChannelFactContractVersions.SelectedSession,
                "12345", null, "session-1", "Dice", "Assistant", revision, "agent", "agent-1");
            var facts = new ChannelSessionFactsEnvelope(ChannelFactContractVersions.SessionFactsEnvelope,
                "12345", null, "summary", 0, true, revision, selected,
                new ChannelActiveSessionsFact(ChannelFactContractVersions.ActiveSessions, "12345", null, revision, []),
                new ChannelQueueFact(ChannelFactContractVersions.Queue, "12345", null, revision, 0, []), []);
            var request = new SendOutboundMessageRequest
            {
                MessageId = "session-facts-" + revision,
                IdempotencyKey = "session-facts-" + revision,
                Content = new OutboundContentEnvelope
                {
                    ActionType = "session_facts",
                    PayloadJson = JsonSerializer.Serialize(facts, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope),
                },
            };
            return await factsExecutor.ExecuteAsync("telegram:owner:default", fixture.Worker, request,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        Assert.True((await Publish(1)).Accepted);
        Assert.True((await Publish(2)).Accepted);

        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method is "pinChatMessage" or "unpinChatMessage");
        var delete = Assert.Single(fixture.Handler.Requests, request => request.Method == "deleteMessage");
        Assert.Equal(999, delete.Body["message_id"]!.GetValue<long>());
    }

    [Fact]
    public async Task SessionFactsNoLongerRenameTelegramTopics()
    {
        using var fixture = new Fixture();
        Assert.Equal(ProvisioningStatus.Succeeded, (await fixture.ProvisionAsync("one", "two")).Status);
        var action = new ChannelTopicRenameAction("12345", "101", "agent:one", "Assistant › Dice");
        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default", new SendOutboundMessageRequest
        {
            Content = new OutboundContentEnvelope { ActionType = "rename_topic", PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelTopicRenameAction) },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(result.Accepted);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method == "editForumTopic");
    }

    [Fact]
    public async Task TelegramNoLongerExecutesPinActions()
    {
        using var fixture = new Fixture();
        ChannelMessagePinAction pin = new("12345", "55");
        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default", new SendOutboundMessageRequest
        {
            Content = new OutboundContentEnvelope
            {
                ActionType = "pin_message",
                PayloadJson = JsonSerializer.Serialize(pin, ChannelContractsJsonContext.Default.ChannelMessagePinAction),
            },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.Accepted);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method is "pinChatMessage" or "unpinChatMessage");
    }

    [Theory]
    [InlineData(200, "", true)]
    [InlineData(400, "message is not modified", true)]
    [InlineData(400, "message to edit not found", false)]
    [InlineData(500, "temporary failure", false)]
    [InlineData(429, "too many requests", false)]
    public async Task ExplicitSemanticEditNeverSendsOrDeletesMessages(int status, string description, bool accepted)
    {
        using var fixture = new Fixture();
        fixture.Handler.EditStatus = (HttpStatusCode)status;
        if (status != 200) fixture.Handler.EditBody = JsonSerializer.Serialize(new { ok = false, error_code = status, description });
        var action = new ChannelSemanticMessageAction("12345", "101", ChannelMessageTemplates.AgentResponse("Queue"), TransportMessageId: "55");
        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default",
            new SendOutboundMessageRequest
            {
                Content = new OutboundContentEnvelope
                {
                    ActionType = "edit_semantic_message",
                    PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)
                }
            },
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(accepted, result.Accepted);
        var edit = Assert.Single(fixture.Handler.Requests, request => request.Method == "editMessageText");
        Assert.Equal(55, edit.Body["message_id"]!.GetValue<long>());
        Assert.Null(edit.Body["message_thread_id"]);
        Assert.NotNull(edit.Body["reply_markup"]!["inline_keyboard"]);
        Assert.Null(edit.Body["reply_markup"]!["keyboard"]);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Method is "sendRichMessage" or "deleteMessage");
        if (description == "message to edit not found") Assert.Equal("message_not_found", result.Detail);
    }

    [Fact]
    public async Task LegacyApprovalCardThatCannotBeEditedIsReplacedWithResolvedRichMessage()
    {
        using var fixture = new Fixture();
        fixture.Handler.EditStatus = HttpStatusCode.BadRequest;
        fixture.Handler.EditBody = "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message can't be edited\"}";
        var action = new ChannelSemanticMessageAction("12345", null,
            ChannelMessageTemplates.ApprovalResolution("Approval for run_skill_script", "approved", "by admin") with
            {
                TerminalState = "approved",
            }, TransportMessageId: "55");

        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default",
            new SendOutboundMessageRequest
            {
                Content = new OutboundContentEnvelope
                {
                    ActionType = ChannelOutboundActionTypes.EditSemanticMessage,
                    PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
                },
            }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.Accepted);
        Assert.NotNull(result.TransportMessageId);
        Assert.Collection(fixture.Handler.Requests,
            request => Assert.Equal("editMessageText", request.Method),
            request => Assert.Equal("deleteMessage", request.Method),
            request => Assert.Equal("sendRichMessage", request.Method));
        Assert.Contains("approved", fixture.Handler.Requests[^1].Body["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"result\":true}", true, DeliveryStatus.Delivered)]
    [InlineData(400, "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: query is too old and response timeout expired or query ID is invalid\"}", false, DeliveryStatus.Rejected)]
    [InlineData(200, "{\"ok\":false,\"error_code\":400}", false, DeliveryStatus.Rejected)]
    [InlineData(503, "unavailable", false, DeliveryStatus.RetryableFailure)]
    [InlineData(200, "not json", false, DeliveryStatus.RetryableFailure)]
    [InlineData(200, "[]", false, DeliveryStatus.RetryableFailure)]
    [InlineData(200, "{\"ok\":true,\"result\":false}", false, DeliveryStatus.RetryableFailure)]
    [InlineData(429, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":2}}", false, DeliveryStatus.RetryableFailure)]
    public async Task CallbackAcknowledgementRequiresTelegramAcceptance(int status, string body, bool accepted, DeliveryStatus expectedStatus)
    {
        using var fixture = new Fixture();
        fixture.Handler.CallbackStatus = (HttpStatusCode)status;
        fixture.Handler.CallbackBody = body;
        var action = new ChannelCallbackAckAction("private-callback-id", "Approve");
        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default", new SendOutboundMessageRequest
        {
            Content = new OutboundContentEnvelope { ActionType = "acknowledge_callback", PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelCallbackAckAction) },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(accepted, result.Accepted);
        Assert.Equal(expectedStatus, result.DeliveryStatus);
        Assert.Contains(fixture.Logs.Messages, message => message.Contains($"httpStatus={status}", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.Messages, message => message.Contains("main-token", StringComparison.Ordinal) || message.Contains("private-callback-id", StringComparison.Ordinal));
        if (!accepted) Assert.StartsWith("telegram_callback_", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentReplyUsesRichMessageEndpointInMainChat()
    {
        using var fixture = new Fixture();
        var action = new ChannelSemanticMessageAction("12345", "101", ChannelMessageTemplates.AgentResponse("**Formatted reply**", "Agent"));
        SendOutboundMessageResponse result = await fixture.Worker.SendOutboundMessageAsync("telegram:owner:default", new SendOutboundMessageRequest
        {
            Content = new OutboundContentEnvelope { ActionType = "send_semantic_message", PayloadJson = JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction) },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(result.Accepted);
        Assert.Equal("999", result.TransportMessageId);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("sendRichMessage", request.Method);
        Assert.Null(request.Body["message_thread_id"]);
        Assert.Contains("**Formatted reply**", request.Body["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisioningDoesNotCreateOrEditTopics()
    {
        using var fixture = new Fixture();
        Assert.Equal(ProvisioningStatus.Succeeded, (await fixture.ProvisionAsync("one", "two").ConfigureAwait(true)).Status);
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.CreateAttempts);
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method is "createForumTopic" or "editForumTopic");
    }

    [Fact]
    public async Task RepeatedProvisioningDoesNotCreateTopicSideEffects()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.CreateAttempts);
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method == "editForumTopic");
    }

    [Fact]
    public async Task ConcurrentProvisioningDoesNotCreateTopics()
    {
        using var fixture = new Fixture();
        await Task.WhenAll(fixture.ProvisionAsync("one", "two"), fixture.ProvisionAsync("one", "two")).ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.CreateAttempts);
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method == "createForumTopic");
    }

    [Fact]
    public async Task RemovingAgentDoesNotCreateOrDeleteTelegramTopics()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        await fixture.ProvisionAsync("one").ConfigureAwait(true);
        Assert.Empty(fixture.Settings()["target_threads"]!.AsObject());
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method is "createForumTopic" or "deleteForumTopic");
    }

    [Fact]
    public async Task IncomingMessageInLegacyTopicIsSuppressedAndRedirectedToMainChat()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        using JsonDocument unmanagedUpdate = JsonDocument.Parse("""{"update_id":1,"message":{"chat":{"id":12345},"message_thread_id":999}}""");
        Assert.True(await fixture.Worker.TryHandleRetiredTopicMessageForTestAsync("telegram:owner:default", unmanagedUpdate.RootElement,
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        using JsonDocument managedUpdate = JsonDocument.Parse("""{"update_id":2,"message":{"chat":{"id":12345},"message_thread_id":101}}""");
        Assert.True(await fixture.Worker.TryHandleRetiredTopicMessageForTestAsync("telegram:owner:default", managedUpdate.RootElement,
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(2, fixture.Handler.Requests.Count(item => item.Method == "sendMessage"));
        Assert.All(fixture.Handler.Requests.Where(item => item.Method == "sendMessage"), item => Assert.Null(item.Body["message_thread_id"]));
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method == "deleteForumTopic");
    }

    [Fact]
    public async Task FailedLegacyTopicDeletionRemainsTrackedForRetryButNeverActive()
    {
        using var fixture = new Fixture();
        fixture.SetLegacyTopics(new JsonObject { ["agent:one"] = "101" });
        fixture.Handler.RejectTopicDeletion = true;

        Assert.Equal(ProvisioningStatus.Succeeded, (await fixture.ProvisionAsync("one")).Status);
        JsonObject afterFailure = fixture.Settings();
        Assert.Empty(afterFailure["target_threads"]!.AsObject());
        Assert.Equal("101", afterFailure["retired_target_threads"]!["agent:one"]!.GetValue<string>());

        fixture.Handler.RejectTopicDeletion = false;
        Assert.Equal(ProvisioningStatus.Succeeded, (await fixture.ProvisionAsync("one")).Status);
        Assert.Empty(fixture.Settings()["retired_target_threads"]!.AsObject());
        Assert.Equal(2, fixture.Handler.Requests.Count(item => item.Method == "deleteForumTopic"));
    }

    [Fact]
    public async Task SettingsSavePreservesHostCheckpointsAndDestinationChangeInvalidatesThem()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        fixture.AddAttachment("telegram:owner:default", "main-token");
        Assert.Empty(fixture.Settings()["target_threads"]!.AsObject());
        fixture.AddAttachment("telegram:owner:default", "main-token", "new-chat");
        Assert.Null(fixture.Settings()["target_threads"]);
        Assert.Null(fixture.Settings()["retired_target_threads"]);
    }

    [Fact]
    public async Task BotIdentityChangeDoesNotCreateTopics()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        fixture.Handler.BotId = 456;
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.CreateAttempts);
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method == "createForumTopic");
    }

    [Fact]
    public async Task MovingAgentToOwnBotDoesNotCreateOrDeleteTopics()
    {
        using var fixture = new Fixture();
        await fixture.ProvisionAsync("one", "two").ConfigureAwait(true);
        fixture.AddAttachment("telegram:owner:agent:two", "agent-token");
        await fixture.Worker.SyncOwnerTargetsAsync(new SyncOwnerTargetsRequest
        {
            OwnerUserId = "owner",
            Targets = { new ChannelTargetMessage { TargetKey = "agent:one", DisplayName = "Agent: one" }, new ChannelTargetMessage { TargetKey = "agent:two", DisplayName = "Agent: two" } },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain(fixture.Handler.Requests, item => item.Method is "createForumTopic" or "deleteForumTopic");
    }

    [Fact]
    public async Task OwnerSyncKeepsAgentBotAndWorkflowBotRostersSeparate()
    {
        using var fixture = new Fixture();
        fixture.AddAttachment("telegram:owner:agent:two", "agent-token");
        fixture.AddAttachment("telegram:owner:workflows", "workflow-token");
        await fixture.Worker.SyncOwnerTargetsAsync(new SyncOwnerTargetsRequest
        {
            OwnerUserId = "owner",
            Targets = { new ChannelTargetMessage { TargetKey = "agent:one", DisplayName = "One" }, new ChannelTargetMessage { TargetKey = "agent:two", DisplayName = "Two" } },
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string MainDescription(string token) => fixture.Handler.Requests.Single(item => item.Url.Contains(token, StringComparison.Ordinal) && item.Method == "setMyDescription").Body["description"]!.GetValue<string>();
        Assert.Contains("agent:one", MainDescription("main-token"), StringComparison.Ordinal);
        Assert.DoesNotContain("agent:two", MainDescription("main-token"), StringComparison.Ordinal);
        Assert.Contains("agent:two", MainDescription("agent-token"), StringComparison.Ordinal);
        Assert.DoesNotContain("agent:one", MainDescription("agent-token"), StringComparison.Ordinal);
        Assert.Contains("No agent targets", MainDescription("workflow-token"), StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private const string attachmentId = "telegram:owner:default";
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-topic-review-" + Guid.NewGuid().ToString("N"));
        private readonly ChannelAttachmentStore attachments;
        private readonly ChannelRuntimeCredentialStore credentials = new();
        private readonly Factory factory;
        private readonly ChannelIngressStore ingress;
        public Handler Handler { get; } = new();
        public TelegramLongPollingWorker Worker { get; private set; }
        public ChannelSessionFactsOutboundExecutor SessionFactsExecutor { get; private set; }
        public DiagnosticLogger Logs { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            var database = new ChannelHostDatabase(configuration);
            database.EnsureReady();
            attachments = new ChannelAttachmentStore(database, credentials);
            SessionFactsExecutor = new ChannelSessionFactsOutboundExecutor(attachments,
                new ChannelSessionFactsViewStore(database), NullLogger<ChannelSessionFactsOutboundExecutor>.Instance);
            AddAttachment(attachmentId, "main-token");
            factory = new Factory(Handler);
            ingress = new ChannelIngressStore(database);
            Worker = new TelegramLongPollingWorker(attachments, ingress, new ChannelOutboundAssetStore(database, configuration), factory, Logs);
        }

        public void AddAttachment(string id, string token, string chatId = "12345")
        {
            attachments.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = id,
                ProviderKey = "telegram",
                ExpectedSchemaVersion = "telegram/v1",
                Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner" }, new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = chatId } },
                CredentialReferences = { new AttachmentCredentialReference { FieldKey = "bot_token", CredentialKey = id + ".token" } },
            });
            credentials.Replace(id, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = token }]);
        }

        public void RestartWorker()
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            var database = new ChannelHostDatabase(configuration);
            Worker = new TelegramLongPollingWorker(new ChannelAttachmentStore(database, credentials), ingress,
                new ChannelOutboundAssetStore(database, configuration), factory, Logs);
        }

        public Task<ProvisionAttachmentResponse> ProvisionAsync(params string[] agents)
        {
            var request = new ProvisionAttachmentRequest { AttachmentId = attachmentId, BotName = "Kautuka" };
            request.Targets.AddRange(agents.Select(id => new ChannelTargetMessage { TargetKey = "agent:" + id, DisplayName = "Agent: " + id }));
            return Worker.ProvisionAttachmentAsync(attachmentId, request, TestContext.Current.CancellationToken);
        }

        public JsonObject Settings()
        {
            Assert.True(attachments.TryGetTelegramRegistrationForProvisioning(attachmentId, out TelegramAttachmentRegistration? registration));
            return JsonNode.Parse(registration!.Settings.GetRawText())!.AsObject();
        }

        public void SetLegacyTopics(JsonObject topicMap)
            => attachments.ApplySettingsPatch(attachmentId, new JsonObject
            {
                ["target_threads"] = topicMap,
                ["target_thread_labels"] = new JsonObject { ["agent:one"] = "Agent: one" },
            });

        public void Dispose()
        {
            factory.Dispose();
            ingress.Dispose();
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler);
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Method, string Url, JsonObject Body)> Requests { get; } = [];
        public int CreateAttempts { get; private set; }
        public bool FailSecondCreate { get; set; }
        public bool RejectTopicDeletion { get; set; }
        public bool TopicsEnabled { get; set; } = true;
        public int BotId { get; set; } = 123;
        public HttpStatusCode CallbackStatus { get; set; } = HttpStatusCode.OK;
        public string CallbackBody { get; set; } = "{\"ok\":true,\"result\":true}";
        public HttpStatusCode EditStatus { get; set; } = HttpStatusCode.OK;
        public string? EditBody { get; set; }
        private int nextTopic = 100;
        private int nextMessageId = 999;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            string method = request.RequestUri.Segments[^1];
            JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            Requests.Add((method, url, body));
            if (method == "answerCallbackQuery")
                return new HttpResponseMessage(CallbackStatus) { Content = new StringContent(CallbackBody, Encoding.UTF8, "application/json") };
            if (method == "editMessageText" && EditBody is not null)
                return new HttpResponseMessage(EditStatus) { Content = new StringContent(EditBody, Encoding.UTF8, "application/json") };
            if (method == "deleteForumTopic" && RejectTopicDeletion)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"ok\":false,\"error_code\":500,\"description\":\"temporary failure\"}", Encoding.UTF8, "application/json")
                };
            if (method == "createForumTopic")
            {
                CreateAttempts++;
                if (FailSecondCreate && CreateAttempts == 2)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("temporary failure") };
                return Success($"{{\"message_thread_id\":{++nextTopic}}}");
            }
            return Success(method switch
            {
                "getChat" => "{\"type\":\"private\"}",
                "getMe" => $"{{\"id\":{BotId},\"username\":\"{(url.Contains("agent-token", StringComparison.Ordinal) ? "agent_two_bot" : "main_bot")}\",\"has_topics_enabled\":{(TopicsEnabled ? "true" : "false")}}}",
                "sendMessage" or "sendRichMessage" => $"{{\"message_id\":{nextMessageId++}}}",
                "editMessageText" => $"{{\"message_id\":{body["message_id"]}}}",
                _ => "true",
            });
        }

        private static HttpResponseMessage Success(string result)
            => new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
    }

    private sealed class DiagnosticLogger : ILogger<TelegramLongPollingWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
