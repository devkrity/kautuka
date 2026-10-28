using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelHost;
using Kautuka.ChannelHost.Grpc;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class SlackSocketModeDispatchTests
{
    [Fact]
    public async Task BotEchoInOwnerDmIsAcknowledgedWithoutNoticeOrIngress()
    {
        using DispatchFixture fixture = new(paired: true);
        await fixture.SendAsync("events_api", """{"event_id":"bot-echo","event":{"type":"message","channel_type":"im","user":"U_BOT","bot_id":"B_BOT","channel":"D_OWNER","text":"echo"}}""");

        Assert.Equal(0, fixture.IngressCount);
        Assert.DoesNotContain(fixture.Http.Requests, request => request.Uri?.AbsolutePath == "/api/chat.postMessage");
        Assert.Equal("env-1", fixture.Socket.Messages.Single().RootElement.GetProperty("envelope_id").GetString());
    }

    [Fact]
    public async Task NonOwnerDmGetsOnlyOneNoticeAndNoIngress()
    {
        using DispatchFixture fixture = new(paired: true, attachmentId: "slack-notice-" + Guid.NewGuid().ToString("N"));
        const string payload = """{"event_id":"non-owner","event":{"type":"message","channel_type":"im","user":"U_OTHER","channel":"D_OTHER","text":"hello"}}""";

        await fixture.SendAsync("events_api", payload);
        await fixture.SendAsync("events_api", payload.Replace("non-owner", "non-owner-2", StringComparison.Ordinal));

        Assert.Equal(0, fixture.IngressCount);
        Assert.Equal(1, fixture.Http.Requests.Count(request => request.Uri?.AbsolutePath == "/api/chat.postMessage"));
    }

    [Fact]
    public async Task UnpairedMessagesOnlyAdmitTrimmedSixDigitPairingCode()
    {
        using DispatchFixture fixture = new(paired: false);
        await fixture.SendAsync("events_api", """{"event_id":"hello","event":{"type":"message","channel_type":"im","user":"U_OWNER","channel":"D_OWNER","text":"hello"}}""");
        Assert.Equal(0, fixture.IngressCount);

        await fixture.SendAsync("events_api", """{"event_id":"pair-code","event":{"type":"message","channel_type":"im","user":"U_OWNER","channel":"D_OWNER","text":" 123456 "}}""");
        Assert.Equal(1, fixture.IngressCount);
    }

    [Fact]
    public async Task NonDmSlashRefusalIsIncludedInSocketAckAndNotPostedByBot()
    {
        using DispatchFixture fixture = new(paired: true);
        await fixture.SendAsync("slash_commands", """{"user_id":"U_OWNER","channel_id":"C123","command":"/kautuka-sessions","text":""}""");

        JsonElement ack = fixture.Socket.Messages.Single().RootElement;
        Assert.Equal("env-1", ack.GetProperty("envelope_id").GetString());
        Assert.Equal("ephemeral", ack.GetProperty("payload").GetProperty("response_type").GetString());
        Assert.Contains("direct message", ack.GetProperty("payload").GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Http.Requests);
        Assert.Equal(0, fixture.IngressCount);
    }

    [Fact]
    public async Task NonOwnerBlockActionPostsEphemeralRefusalToResponseUrl()
    {
        using DispatchFixture fixture = new(paired: true);
        await fixture.SendAsync("interactive", """{"type":"block_actions","user":{"id":"U_OTHER"},"channel":{"id":"D_OTHER"},"response_url":"https://hooks.slack.com/response","actions":[{"action_id":"approve","value":"hitl:abc:approve"}]}""");

        CapturedHttpRequest request = Assert.Single(fixture.Http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("hooks.slack.com", request.Uri?.Host);
        Assert.Equal("ephemeral", request.Body.RootElement.GetProperty("response_type").GetString());
        Assert.Equal("env-1", fixture.Socket.Messages.Single().RootElement.GetProperty("envelope_id").GetString());
        Assert.True(fixture.Sequence.IndexOf("ack") < fixture.Sequence.IndexOf("http"));
        Assert.Equal(0, fixture.IngressCount);
    }

    [Fact]
    public async Task SocketFailureBackoffResetsAfterConnectionAndCleanServerDisconnect()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(2500));
        List<int> backoffAttempts = [];
        AdvancingTimeProvider clock = new();
        using DispatchFixture fixture = new(paired: true,
            socketConnector: (_, _) => Task.FromResult<WebSocket>(new RecordingWebSocket(
                ["{\"type\":\"hello\"}", "{\"type\":\"disconnect\"}"],
                onReceive: _ => clock.Advance(TimeSpan.FromSeconds(11)))),
            retryDelay: attempt =>
            {
                backoffAttempts.Add(attempt);
                return TimeSpan.FromSeconds(1);
            },
            connectionTimeProvider: clock);
        fixture.Http.ConnectionOpenResponses.Enqueue("{\"ok\":false,\"error\":\"temporarily_unavailable\"}");
        fixture.Http.ConnectionOpenResponses.Enqueue("{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link/?ticket=1\"}");
        fixture.Http.ConnectionOpenResponses.Enqueue("{\"ok\":false,\"error\":\"temporarily_unavailable\"}");

        await fixture.WorkerRunConnectionLoopAsync(cancellation.Token);

        Assert.True(backoffAttempts.Count >= 2);
        Assert.Equal(1, backoffAttempts[0]);
        Assert.Equal(1, backoffAttempts[1]);
        Assert.True(fixture.Http.Requests.Count(request => request.Uri?.AbsolutePath == "/api/apps.connections.open") >= 3);
    }

    [Fact]
    public async Task HelloThenImmediateDisconnectBacksOffBeforeReconnect()
    {
        using CancellationTokenSource cancellation = new();
        List<int> backoffAttempts = [];
        using DispatchFixture fixture = new(paired: true,
            socketConnector: static (_, _) => Task.FromResult<WebSocket>(new RecordingWebSocket(
                ["{\"type\":\"hello\"}", "{\"type\":\"disconnect\"}"])),
            retryDelay: attempt =>
            {
                backoffAttempts.Add(attempt);
                cancellation.Cancel();
                return TimeSpan.FromSeconds(1);
            });
        fixture.Http.ConnectionOpenResponses.Enqueue("{\"ok\":true,\"url\":\"wss://wss-primary.slack.com/link/?ticket=1\"}");

        await fixture.WorkerRunConnectionLoopAsync(cancellation.Token);

        Assert.Equal([1], backoffAttempts);
        Assert.Equal(1, fixture.Http.Requests.Count(request => request.Uri?.AbsolutePath == "/api/apps.connections.open"));
    }

    [Fact]
    public void TransientIdentitySyncFailureRetriesWithExponentialBackoff()
    {
        using DispatchFixture fixture = new(paired: true);
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-23T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        fixture.Worker.RecordIdentitySyncFailureForTest(fixture.AttachmentId, "xoxb-test", "ratelimited", now);
        DateTimeOffset firstRetry = Assert.IsType<DateTimeOffset>(fixture.Worker.GetIdentitySyncRetryAfterForTest(fixture.AttachmentId));

        Assert.True(fixture.Worker.ShouldDeferIdentitySyncForTest(fixture.AttachmentId, "xoxb-test", firstRetry.AddTicks(-1)));
        Assert.False(fixture.Worker.ShouldDeferIdentitySyncForTest(fixture.AttachmentId, "xoxb-test", firstRetry));

        fixture.Worker.RecordIdentitySyncFailureForTest(fixture.AttachmentId, "xoxb-test", null, firstRetry);
        DateTimeOffset secondRetry = Assert.IsType<DateTimeOffset>(fixture.Worker.GetIdentitySyncRetryAfterForTest(fixture.AttachmentId));
        Assert.Equal(TimeSpan.FromSeconds(10), secondRetry - firstRetry);
        Assert.False(fixture.Worker.ShouldDeferIdentitySyncForTest(fixture.AttachmentId, "xoxb-replaced", firstRetry));
    }

    [Fact]
    public async Task SupervisorRestartsACompletedConnectionTask()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource secondStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        using DispatchFixture fixture = new(paired: true,
            connectionLoopRunner: (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref starts) >= 2) secondStart.TrySetResult();
                return Task.CompletedTask;
            },
            supervisorPollDelay: TimeSpan.FromMilliseconds(10));

        Task supervisor = fixture.Worker.RunAsync(cancellation.Token);
        await secondStart.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor);
        Assert.True(starts >= 2);
    }

    [Fact]
    public async Task IngressRateLimitRpcExceptionIsAcknowledgedAndDoesNotStopSocketMessageHandling()
    {
        using DispatchFixture fixture = new(paired: true, withIngressGovernor: true);
        for (int index = 0; index < 302; index++)
        {
            string eventId = $"limited-{index}";
            string payload = JsonSerializer.Serialize(new { event_id = eventId, @event = new { type = "message", channel_type = "im", user = "U_OWNER", channel = "D_OWNER", text = "hello" } });
            await fixture.SendAsync("events_api", payload);
        }

        Assert.Equal(302, fixture.Socket.Messages.Count);
        Assert.Equal("env-1", fixture.Socket.Messages[^1].RootElement.GetProperty("envelope_id").GetString());
        Assert.Equal(300, fixture.IngressCount);
    }

    [Fact]
    public async Task FailedSocketOpenIsRetriedWithBoundedBackoff()
    {
        using DispatchFixture fixture = new(paired: true);
        fixture.Http.FailFirstConnectionOpen = true;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(3));
        await fixture.WorkerRunConnectionLoopAsync(cancellation.Token);

        Assert.InRange(fixture.Http.Requests.Count(request => request.Uri?.AbsolutePath == "/api/apps.connections.open"), 2, int.MaxValue);
        Assert.True(SlackSocketModeWorker.GetConnectionBackoffDelay(100) <= TimeSpan.FromSeconds(60));
    }

    private sealed class DispatchFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-slack-dispatch-" + Guid.NewGuid().ToString("N"));
        private readonly ChannelHostDatabase database;
        private readonly SlackSocketModeWorker worker;
        private readonly string attachmentId;
        private readonly string ownerId = "user-1";

        public DispatchFixture(bool paired, string? attachmentId = null, bool withIngressGovernor = false,
            Func<Uri, CancellationToken, Task<WebSocket>>? socketConnector = null,
            Func<int, TimeSpan>? retryDelay = null,
            Func<string, string, string, CancellationToken, Task>? connectionLoopRunner = null,
            TimeSpan? supervisorPollDelay = null,
            TimeProvider? connectionTimeProvider = null)
        {
            this.attachmentId = attachmentId ?? "slack-dispatch-" + Guid.NewGuid().ToString("N");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
            }).Build();
            database = new ChannelHostDatabase(configuration);
            database.EnsureReady();
            ChannelRuntimeCredentialStore credentials = new();
            ChannelAttachmentStore attachments = new(database, credentials);
            JsonObject settings = new()
            {
                ["owner_user_id"] = ownerId,
                ["slack_dm_channel_id"] = "D_OWNER",
                ["bot_user_id"] = "U_BOT",
                ["bot_id"] = "B_BOT",
            };
            if (paired) settings["slack_owner_user_id"] = "U_OWNER";
            UpsertAttachmentRequest upsert = new() { AttachmentId = this.attachmentId, ProviderKey = "slack", ExpectedSchemaVersion = "slack/v1" };
            upsert.Settings.AddRange(ChannelAttachmentStore.ToSettingValues(settings));
            _ = attachments.UpsertAttachment(upsert);
            credentials.Replace(this.attachmentId, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "xoxb-test" }, new RuntimeCredentialValue { FieldKey = "app_token", SecretValue = "xapp-test" }]);
            Ingress = new ChannelIngressStore(database);
            Sequence = [];
            Http = new RecordingHttpFactory(Sequence);
            Socket = new RecordingWebSocket(sequence: Sequence);
            worker = new SlackSocketModeWorker(attachments, credentials, Ingress, new ChannelOutboundAssetStore(database, configuration), Http,
                NullLogger<SlackSocketModeWorker>.Instance,
                withIngressGovernor ? new ChannelResourceGovernor(TimeProvider.System) : null,
                socketConnector: socketConnector,
                connectionBackoffDelay: retryDelay,
                connectionLoopRunner: connectionLoopRunner,
                supervisorPollInterval: supervisorPollDelay,
                connectionTimeProvider: connectionTimeProvider);
        }

        public string AttachmentId => attachmentId;
        public Task WorkerRunConnectionLoopAsync(CancellationToken cancellationToken)
            => worker.RunConnectionLoopSafeAsync(attachmentId, ownerId, "xapp-test", cancellationToken);

        public SlackSocketModeWorker Worker => worker;
        public ChannelIngressStore Ingress { get; }
        public RecordingHttpFactory Http { get; }
        public RecordingWebSocket Socket { get; }
        public List<string> Sequence { get; }
        public int IngressCount
        {
            get
            {
                using SqliteConnection connection = database.OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='channel_ingress_outbox'";
                if ((long)(command.ExecuteScalar() ?? 0L) == 0) return 0;
                command.CommandText = "SELECT COUNT(*) FROM channel_ingress_outbox";
                return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public async Task SendAsync(string type, string payload)
        {
            MethodInfo? method = typeof(SlackSocketModeWorker).GetMethod("HandleWebSocketMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.True(method!.GetParameters()[3].ParameterType.IsAssignableFrom(Socket.GetType()), "Socket Mode handler must accept a fake WebSocket for protocol-level tests.");
            using JsonDocument payloadDocument = JsonDocument.Parse(payload);
            string envelope = JsonSerializer.Serialize(new { type, envelope_id = "env-1", payload = payloadDocument.RootElement });
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(envelope));
            Task task = (Task)method.Invoke(worker, [attachmentId, ownerId, stream, Socket, CancellationToken.None])!;
            await task;
        }

        public void Dispose()
        {
            Ingress.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly Queue<string> incoming;
        private readonly List<string>? sequence;
        private readonly Action<string>? onReceive;
        private WebSocketState state = WebSocketState.Open;

        public RecordingWebSocket(IEnumerable<string>? incoming = null, List<string>? sequence = null, Action<string>? onReceive = null)
        {
            this.incoming = new Queue<string>(incoming ?? []);
            this.sequence = sequence;
            this.onReceive = onReceive;
        }

        public List<JsonDocument> Messages { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (incoming.Count == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            byte[] bytes = Encoding.UTF8.GetBytes(incoming.Dequeue());
            onReceive?.Invoke(Encoding.UTF8.GetString(bytes));
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true));
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Messages.Add(JsonDocument.Parse(buffer));
            sequence?.Add("ack");
            return Task.CompletedTask;
        }
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class RecordingHttpFactory(List<string> sequence) : IHttpClientFactory
    {
        private readonly List<string> sequence = sequence;
        public List<CapturedHttpRequest> Requests { get; } = [];
        public Queue<string> ConnectionOpenResponses { get; } = new();
        private int failFirstConnectionOpen;
        public bool FailFirstConnectionOpen { get => Volatile.Read(ref failFirstConnectionOpen) != 0; set => Volatile.Write(ref failFirstConnectionOpen, value ? 1 : 0); }
        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: true);
        private sealed class Handler(RecordingHttpFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string bodyText = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add(new CapturedHttpRequest(request.Method, request.RequestUri, JsonDocument.Parse(bodyText)));
                owner.sequence.Add("http");
                if (request.RequestUri?.AbsolutePath == "/api/apps.connections.open" && owner.ConnectionOpenResponses.TryDequeue(out string? openResponse))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(openResponse, Encoding.UTF8, "application/json") };
                if (request.RequestUri?.AbsolutePath == "/api/apps.connections.open"
                    && Interlocked.Exchange(ref owner.failFirstConnectionOpen, 0) != 0)
                    throw new HttpRequestException("simulated Slack connection-open failure");
                string response = request.RequestUri?.AbsolutePath == "/api/apps.connections.open"
                    ? "{\"ok\":false,\"error\":\"temporarily_unavailable\"}"
                    : request.RequestUri?.AbsolutePath == "/api/users.info"
                    ? "{\"ok\":true,\"user\":{\"profile\":{\"display_name\":\"Owner\"}}}"
                    : "{\"ok\":true,\"ts\":\"123.4\"}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }
        }
    }

    private sealed record CapturedHttpRequest(HttpMethod Method, Uri? Uri, JsonDocument Body);
}
