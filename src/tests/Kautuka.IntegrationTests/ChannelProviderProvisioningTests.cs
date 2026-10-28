using System.Net;
using System.Net.WebSockets;
using System.Text;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelProviderProvisioningTests
{
    [Fact]
    public async Task SlackProvisioningValidatesBothTokensAndRequiredScopes()
    {
        using var fixture = new ProviderFixture();
        fixture.AddAttachment("slack-1", "slack", ("bot_token", "xoxb-test"), ("app_token", "xapp-test"));
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"user_id\":\"U1\",\"bot_id\":\"B1\",\"team_id\":\"T1\",\"app_id\":\"A1\"}",
            ("x-oauth-scopes", string.Join(',', SlackSetupManifest.RequiredBotScopes)));
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"user_id\":\"U1\",\"bot_id\":\"B1\",\"team_id\":\"T1\",\"app_id\":\"A1\"}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"bot\":{\"app_id\":\"A1\"}}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link\"}");
        var worker = new SlackSocketModeWorker(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http, NullLogger<SlackSocketModeWorker>.Instance);

        ProvisionAttachmentResponse response = await worker.ProvisionAttachmentAsync("slack-1", Request("slack-1"), CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Succeeded, response.Status);
        Assert.Contains("socket_token_verified", response.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("xoxb-test", response.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-test", response.Detail, StringComparison.Ordinal);
        Assert.Contains(fixture.Http.Requests, request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open");

        byte[] file = "scope-verified file"u8.ToArray();
        ChannelOutboundAssetUploadResult uploaded = await fixture.OutboundAssets.ReceiveAsync(new UploadOutboundAssetMetadata
        {
            Context = new ScopedRequestContext { ActorUserId = "user-1", TenantId = "tenant-1" },
            AssetId = "scope-asset",
            AttachmentId = "slack-1",
            ConversationId = "D1",
            SafeFileName = "scope.txt",
            ContentType = "text/plain",
            DeclaredSizeBytes = file.Length,
            Sha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)),
            IdempotencyKey = "scope-file",
        }, SingleChunk(file), CancellationToken.None);
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"upload_url\":\"https://files.slack.com/upload\",\"file_id\":\"F1\"}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");
        SendOutboundMessageResponse fileResponse = await worker.SendOutboundMessageAsync("slack-1", new SendOutboundMessageRequest
        {
            Content = new OutboundContentEnvelope
            {
                ActionType = "send_file",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new ChannelOutboundFileAction(uploaded.UploadId, "D1", null, "scope.txt", "text/plain", file.Length, null, null),
                    ChannelContractsJsonContext.Default.ChannelOutboundFileAction),
            },
        }, CancellationToken.None);
        Assert.True(fileResponse.Accepted, fileResponse.Detail);
    }

    [Fact]
    public async Task SlackProvisioningReturnsActionableMissingScopeWithoutOpeningSocket()
    {
        using var fixture = new ProviderFixture();
        fixture.AddAttachment("slack-1", "slack", ("bot_token", "xoxb-test"), ("app_token", "xapp-test"));
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true}", ("x-oauth-scopes", "chat:write"));
        var worker = new SlackSocketModeWorker(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http, NullLogger<SlackSocketModeWorker>.Instance);

        ProvisionAttachmentResponse response = await worker.ProvisionAttachmentAsync("slack-1", Request("slack-1"), CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Failed, response.Status);
        Assert.Equal("This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.", response.Detail);
        Assert.DoesNotContain(fixture.Http.Requests, request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open");
    }

    [Fact]
    public async Task SlackProvisioningSanitizesProviderTransportFailures()
    {
        using var fixture = new ProviderFixture();
        fixture.AddAttachment("channel-1", "slack", ("bot_token", "xoxb-test"), ("app_token", "xapp-test"));
        fixture.Http.EnqueueException(new HttpRequestException("secret provider diagnostic"));

        ProvisionAttachmentResponse response = await new SlackSocketModeWorker(
                fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http, NullLogger<SlackSocketModeWorker>.Instance)
            .ProvisionAttachmentAsync("channel-1", Request("channel-1"), CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Failed, response.Status);
        Assert.Equal("provider_unreachable", response.Detail);
        Assert.DoesNotContain("diagnostic", response.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SlackProvisioningRejectsASecondWorkspaceMemberWithoutThrowingOrOpeningSocket()
    {
        using var fixture = new ProviderFixture();
        fixture.AddAttachment("slack-user-1", "slack", "user-1", ("bot_token", "xoxb-one"), ("app_token", "xapp-one"));
        fixture.AddAttachment("slack-user-2", "slack", "user-2", ("bot_token", "xoxb-two"), ("app_token", "xapp-two"));
        fixture.EnqueueSlackIdentity("U1", "B1", "T1", "A1");
        var worker = new SlackSocketModeWorker(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http, NullLogger<SlackSocketModeWorker>.Instance);

        ProvisionAttachmentResponse winner = await worker.ProvisionAttachmentAsync("slack-user-1", Request("slack-user-1"), CancellationToken.None);
        fixture.EnqueueSlackIdentity("U1", "B1", "T1", "A1");
        ProvisionAttachmentResponse sameOwner = await worker.ProvisionAttachmentAsync("slack-user-1", Request("slack-user-1"), CancellationToken.None);
        int opensBeforeConflict = fixture.Http.Requests.Count(request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open");
        fixture.EnqueueSlackIdentity("U2", "B2", "T1", "A2", includeSocketOpen: false);
        ProvisionAttachmentResponse loser = await worker.ProvisionAttachmentAsync("slack-user-2", Request("slack-user-2"), CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Succeeded, winner.Status);
        Assert.Equal(ProvisioningStatus.Succeeded, sameOwner.Status);
        Assert.Equal(ProvisioningStatus.Failed, loser.Status);
        Assert.Equal("slack_workspace_in_use", loser.Detail);
        Assert.Equal(2, opensBeforeConflict);
        Assert.Equal(opensBeforeConflict, fixture.Http.Requests.Count(request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open"));
        Assert.False(fixture.Attachments.TryGetActiveAttachment("slack-user-2", out _));

        Assert.True(fixture.Attachments.DeactivateAttachment("slack-user-1"));
        fixture.EnqueueSlackIdentity("U2", "B2", "T1", "A2");
        ProvisionAttachmentResponse afterWinnerDeactivated = await worker.ProvisionAttachmentAsync("slack-user-2", Request("slack-user-2"), CancellationToken.None);
        Assert.True(afterWinnerDeactivated.Status == ProvisioningStatus.Succeeded, afterWinnerDeactivated.Detail);
        Assert.True(fixture.Attachments.TryGetActiveAttachment("slack-user-2", out _));
    }

    [Fact]
    public async Task TransientIdentitySyncFailureRetriesAndThenConnects()
    {
        using var fixture = new ProviderFixture();
        const string attachmentId = "slack-transient-retry";
        fixture.AddAttachment(attachmentId, "slack", ("bot_token", "xoxb-test"), ("app_token", "xapp-test"));
        fixture.Attachments.MarkProvisioned(attachmentId, "provisioned", "test");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"ratelimited\"}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"user_id\":\"U1\",\"bot_id\":\"B1\",\"team_id\":\"T1\"}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"bot\":{\"app_id\":\"A1\"}}");
        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link\"}");
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
        SlackSocketModeWorker worker = new(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http,
            NullLogger<SlackSocketModeWorker>.Instance,
            socketConnector: (_, _) =>
            {
                connected.TrySetResult();
                return Task.FromResult<WebSocket>(new BlockingWebSocket());
            },
            supervisorPollInterval: TimeSpan.FromMilliseconds(5),
            identitySyncRetryDelay: _ => TimeSpan.FromMilliseconds(20));

        Task run = worker.RunAsync(cancellation.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(fixture.Http.Requests.Count(request => request.RequestUri?.AbsolutePath == "/api/auth.test") >= 2);
        Assert.Contains(fixture.Http.Requests, request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open");
    }

    [Fact]
    public async Task WorkspaceConflictIsStickyUntilReprovisionSucceedsWithTheSameToken()
    {
        using var fixture = new ProviderFixture();
        const string attachmentId = "slack-conflict-recovered";
        fixture.AddAttachment(attachmentId, "slack", ("bot_token", "xoxb-same-token"), ("app_token", "xapp-test"));
        SlackSocketModeWorker worker = new(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http,
            NullLogger<SlackSocketModeWorker>.Instance);
        worker.RecordIdentitySyncFailureForTest(attachmentId, "xoxb-same-token", "slack_workspace_in_use");
        Assert.True(worker.ShouldDeferIdentitySyncForTest(attachmentId, "xoxb-same-token", DateTimeOffset.UtcNow.AddDays(1)));

        fixture.EnqueueSlackIdentity("U1", "B1", "T1", "A1");
        ProvisionAttachmentResponse reprovisioned = await worker.ProvisionAttachmentAsync(attachmentId, Request(attachmentId), CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Succeeded, reprovisioned.Status);
        Assert.False(worker.ShouldDeferIdentitySyncForTest(attachmentId, "xoxb-same-token", DateTimeOffset.UtcNow));

        fixture.Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link\"}");
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
        SlackSocketModeWorker reconnectingWorker = new(fixture.Attachments, fixture.RuntimeCredentials, fixture.Ingress, fixture.OutboundAssets, fixture.Http,
            NullLogger<SlackSocketModeWorker>.Instance,
            socketConnector: (_, _) =>
            {
                connected.TrySetResult();
                return Task.FromResult<WebSocket>(new BlockingWebSocket());
            });
        Task run = reconnectingWorker.RunConnectionLoopSafeAsync(attachmentId, "user-1", "xapp-test", cancellation.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await run;
        Assert.True(fixture.Http.Requests.Count(request => request.RequestUri?.AbsolutePath == "/api/apps.connections.open") >= 2);
    }

    private static ProvisionAttachmentRequest Request(string attachmentId) => new()
    {
        AttachmentId = attachmentId,
        Context = new ScopedRequestContext { RequestId = "setup-test" },
    };

    private sealed class ProviderFixture : IDisposable
    {
        private readonly string directory;
        private readonly ChannelHostDatabase database;

        public ProviderFixture()
        {
            directory = Path.Combine(Path.GetTempPath(), "kautuka-provider-setup-tests", Guid.NewGuid().ToString("N"));
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = directory })
                .Build();
            database = new ChannelHostDatabase(configuration);
            database.EnsureReady();
            RuntimeCredentials = new ChannelRuntimeCredentialStore();
            Attachments = new ChannelAttachmentStore(database, RuntimeCredentials);
            Ingress = new ChannelIngressStore(database);
            OutboundAssets = new ChannelOutboundAssetStore(database, configuration);
            Http = new QueueHttpClientFactory();
        }

        public ChannelRuntimeCredentialStore RuntimeCredentials { get; }
        public ChannelAttachmentStore Attachments { get; }
        public ChannelIngressStore Ingress { get; }
        public ChannelOutboundAssetStore OutboundAssets { get; }
        public QueueHttpClientFactory Http { get; }

        public void AddAttachment(string id, string provider, params (string Key, string Value)[] credentials)
            => AddAttachment(id, provider, "user-1", credentials);

        public void AddAttachment(string id, string provider, string ownerUserId, params (string Key, string Value)[] credentials)
        {
            UpsertAttachmentRequest request = new()
            {
                AttachmentId = id,
                ProviderKey = provider,
                ExpectedSchemaVersion = provider + "/v1",
                CredentialReferences = { credentials.Select(item => new AttachmentCredentialReference { FieldKey = item.Key, CredentialKey = "test." + item.Key }) },
            };
            request.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = ownerUserId });
            if (provider == "slack") request.Settings.Add(new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D1" });
            UpsertAttachmentResponse response = Attachments.UpsertAttachment(request);
            RuntimeCredentials.Replace(response.Attachment.AttachmentId,
                [.. credentials.Select(item => new RuntimeCredentialValue { FieldKey = item.Key, SecretValue = item.Value })]);
            if (provider == "slack") Attachments.MarkProvisioningStarted(id, "setup-test");
        }

        public void EnqueueSlackIdentity(string userId, string botId, string teamId, string appId, bool includeSocketOpen = true)
        {
            string identity = $$"""{"ok":true,"user_id":"{{userId}}","bot_id":"{{botId}}","team_id":"{{teamId}}","app_id":"{{appId}}"}""";
            Http.Enqueue(HttpStatusCode.OK, identity, ("x-oauth-scopes", string.Join(',', SlackSetupManifest.RequiredBotScopes)));
            Http.Enqueue(HttpStatusCode.OK, identity);
            Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"bot\":{\"app_id\":\"" + appId + "\"}}");
            if (includeSocketOpen) Http.Enqueue(HttpStatusCode.OK, "{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link\"}");
        }

        public void Dispose()
        {
            Ingress.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleChunk(byte[] bytes)
    {
        yield return bytes;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class QueueHttpClientFactory : IHttpClientFactory
    {
        private readonly Queue<Func<HttpResponseMessage>> responses = new();
        public List<CapturedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
            => responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                foreach ((string name, string value) in headers) response.Headers.TryAddWithoutValidation(name, value);
                return response;
            });

        public void EnqueueException(HttpRequestException exception)
            => responses.Enqueue(() => throw exception);

        public HttpClient CreateClient(string name)
            => new(new Handler(this), disposeHandler: true) { BaseAddress = new Uri("https://example.invalid") };

        private sealed class Handler(QueueHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add(new CapturedRequest(request.Method, request.RequestUri, body));
                return owner.responses.Dequeue().Invoke();
            }
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? RequestUri, string Body);

    private sealed class BlockingWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
