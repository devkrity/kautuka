using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Kautuka.ChannelHost.Grpc;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

#pragma warning disable CA2007 // Async fixture disposal intentionally retains xUnit's test context.
public sealed class TelegramPollingSupervisorTests
{
    private static readonly string[] distinctEventIds = ["telegram:1000:42", "telegram:1001:42"];
    [Fact]
    public async Task FiftyBotsPollIndependentlyAndAnActiveBotDoesNotWaitForFortyNineIdleBots()
    {
        await using var fixture = new Fixture(50);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 50).ConfigureAwait(true);
        fixture.Handler.Update("1000", 42);
        await WaitForAsync(() => fixture.Cursor("1000") == "43" && fixture.Handler.Count("1000") >= 2).ConfigureAwait(true);
        fixture.Handler.Update("1000", 43);
        await WaitForAsync(() => fixture.Cursor("1000") == "44" && fixture.Handler.Count("1000") >= 3).ConfigureAwait(true);
        Assert.All(Enumerable.Range(1001, 49), id => Assert.Equal(1, fixture.Handler.Count(id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(2, fixture.EventIds().Length);
        await fixture.StopAsync().ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.ActiveTotal);
    }

    [Fact]
    public async Task EqualUpdateIdsOnDifferentBotsPersistSeparatelyAndSameBotDeliveryDeduplicates()
    {
        await using var fixture = new Fixture(2);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Handler.Update("1000", 42);
        fixture.Handler.Update("1001", 42);
        await WaitForAsync(() => fixture.Cursor("1000") == "43" && fixture.Cursor("1001") == "43").ConfigureAwait(true);
        Assert.Equal(distinctEventIds, fixture.EventIds());
        fixture.Handler.Update("1000", 42);
        await WaitForAsync(() => fixture.Handler.Count("1000") >= 3).ConfigureAwait(true);
        Assert.Equal(2, fixture.EventIds().Length);
        fixture.Handler.Update("1000", 41);
        await WaitForAsync(() => fixture.Handler.Count("1000") >= 4).ConfigureAwait(true);
        Assert.Equal("43", fixture.Cursor("1000"));
        Assert.Equal(2, fixture.EventIds().Length);
    }

    [Fact]
    public async Task FiftySimultaneousBotUpdatesPersistWithIndependentCursors()
    {
        await using var fixture = new Fixture(50);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 50).ConfigureAwait(true);
        foreach (int id in Enumerable.Range(1000, 50)) fixture.Handler.Update(id.ToString(System.Globalization.CultureInfo.InvariantCulture), 42);
        await WaitForAsync(() => fixture.EventIds().Length == 50).ConfigureAwait(true);
        foreach (int id in Enumerable.Range(1000, 50)) Assert.Equal("43", fixture.Cursor(id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task RosterChangesAndCredentialRotationDoNotOverlapPollsOrResetTheSameBotCursor()
    {
        await using var fixture = new Fixture(2);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Handler.Update("1000", 42);
        await WaitForAsync(() => fixture.Cursor("1000") == "43").ConfigureAwait(true);
        fixture.Rotate(0, "1000:rotated-secret");
        await WaitForAsync(() => fixture.Handler.LastToken("1000") == "1000:rotated-secret").ConfigureAwait(true);
        Assert.Equal(43, fixture.Handler.LastOffset("1000"));
        Assert.Equal(1, fixture.Handler.MaxActive("1000"));
        fixture.Add(2, "1002:new-secret");
        await WaitForAsync(() => fixture.Handler.Active("1002") == 1).ConfigureAwait(true);
        fixture.RemoveCredentials(1);
        await WaitForAsync(() => fixture.Handler.Active("1001") == 0).ConfigureAwait(true);
        Assert.Equal(1, fixture.Handler.Active("1000"));
    }

    [Theory]
    [InlineData(true, 99)]
    [InlineData(false, 0)]
    public async Task LegacyCursorIsAdoptedOnlyForMatchingRecordedBotIdentity(bool matching, long expectedOffset)
    {
        await using var fixture = new Fixture(1);
        fixture.LegacyCursor(0, matching ? "1000" : "other-bot", "99");
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.Active("1000") == 1).ConfigureAwait(true);
        Assert.Equal(expectedOffset, fixture.Handler.LastOffset("1000"));
        fixture.Rotate(0, "2000:new-bot-secret");
        await WaitForAsync(() => fixture.Handler.Active("2000") == 1).ConfigureAwait(true);
        Assert.Equal(0, fixture.Handler.LastOffset("2000"));
    }

    [Fact]
    public async Task DuplicateBotOwnershipSuspendsOnlyThatBotAndResumesAfterConflictIsRemoved()
    {
        await using var fixture = new Fixture(2);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Add(2, "1000:another-secret");
        await WaitForAsync(() => fixture.Handler.Active("1000") == 0).ConfigureAwait(true);
        Assert.Equal("degraded", (await fixture.Worker.GetHealthAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Status);
        Assert.Equal(1, fixture.Handler.Active("1001"));
        fixture.RemoveCredentials(2);
        await WaitForAsync(() => fixture.Handler.Active("1000") == 1).ConfigureAwait(true);
        Assert.Equal(1, fixture.Handler.MaxActive("1000"));
    }

    [Fact]
    public async Task InvalidCredentialsCannotSuspendAnotherBotsVerifiedOwnership()
    {
        await using var fixture = new Fixture(2);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Add(2, "1000:invalid-secret");
        await WaitForAsync(() => fixture.Handler.InvalidVerifications > 0).ConfigureAwait(true);
        // Allow the next roster pass to process the rejected identity.
        await Task.Delay(1200, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.Handler.Update("1000", 42);
        await WaitForAsync(() => fixture.Cursor("1000") == "43").ConfigureAwait(true);
        Assert.Equal(1, fixture.Handler.Active("1001"));
        Assert.Equal(1, fixture.Handler.MaxActive("1000"));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    public async Task FailedBotBacksOffWithoutDelayingHealthyBot(int status)
    {
        await using var fixture = new Fixture(2);
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Handler.Fail("1000", status);
        fixture.Handler.Update("1001", 42);
        await WaitForAsync(() => fixture.Cursor("1001") == "43" && fixture.Handler.Count("1001") >= 2).ConfigureAwait(true);
        await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, fixture.Handler.Count("1000"));
        Assert.Equal("degraded", (await fixture.Worker.GetHealthAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Status);
    }

    [Fact]
    public async Task ResourceLimitFailureDoesNotAdvanceTheCursorOrTerminateOtherBotLoops()
    {
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        await using var fixture = new Fixture(2, governor);
        for (int i = 0; i < 300; i++) governor.EnforceProviderIngress("telegram", Fixture.Id(0));
        fixture.Start();
        await WaitForAsync(() => fixture.Handler.ActiveTotal == 2).ConfigureAwait(true);
        fixture.Handler.Update("1000", 42);
        fixture.Handler.Update("1001", 42);
        await WaitForAsync(() => fixture.Cursor("1001") == "43").ConfigureAwait(true);
        Assert.Null(fixture.Cursor("1000"));
        Assert.Single(fixture.EventIds());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "Polling condition did not become true.");
            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-poll-supervisor-" + Guid.NewGuid().ToString("N"));
        private readonly ChannelHostDatabase database;
        private readonly ChannelRuntimeCredentialStore credentials = new();
        private readonly ChannelAttachmentStore attachments;
        private readonly ChannelIngressStore ingress;
        private readonly Factory factory;
        private readonly CancellationTokenSource cancellation = new();
        private Task running = Task.CompletedTask;
        public Handler Handler { get; } = new();
        public TelegramLongPollingWorker Worker { get; }

        public Fixture(int bots, ChannelResourceGovernor? governor = null)
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
            database = new ChannelHostDatabase(configuration);
            database.EnsureReady();
            attachments = new ChannelAttachmentStore(database, credentials);
            ingress = new ChannelIngressStore(database);
            factory = new Factory(Handler);
            Worker = new TelegramLongPollingWorker(attachments, ingress, new ChannelOutboundAssetStore(database, configuration), factory, NullLogger<TelegramLongPollingWorker>.Instance, governor);
            for (int i = 0; i < bots; i++) Add(i, (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":test-secret");
        }

        public static string Id(int index) => $"telegram:owner-{index / 5}:agent:agent-{index}";
        public void Add(int index, string token)
        {
            attachments.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = Id(index),
                ProviderKey = "telegram",
                ExpectedSchemaVersion = "telegram/v1",
                Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-" + index / 5 }, new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "1" } },
                CredentialReferences = { new AttachmentCredentialReference { FieldKey = "bot_token", CredentialKey = Id(index) + ".token" } },
            });
            Rotate(index, token);
        }
        public void Rotate(int index, string token) => credentials.Replace(Id(index), [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = token }]);
        public void RemoveCredentials(int index) => credentials.Remove(Id(index));
        public void LegacyCursor(int index, string bot, string cursor)
        {
            ingress.SetCursor("telegram", Id(index), cursor);
            attachments.ApplySettingsPatch(Id(index), new System.Text.Json.Nodes.JsonObject { ["telegram_bot_id"] = bot });
        }
        public string? Cursor(string bot) => ingress.GetCursor("telegram", "telegram-bot:" + bot);
        public void Start() => running = Worker.RunAsync(cancellation.Token);
        public async Task StopAsync()
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        public string[] EventIds()
        {
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT event_id FROM channel_ingress_outbox ORDER BY event_id";
            using var reader = command.ExecuteReader();
            List<string> ids = [];
            while (reader.Read()) ids.Add(reader.GetString(0));
            return [.. ids];
        }
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            cancellation.Dispose();
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
        private sealed class Bot
        {
            public Channel<HttpResponseMessage> Responses { get; } = Channel.CreateUnbounded<HttpResponseMessage>();
            public int Active;
            public int MaximumActive;
            public int Requests;
            public string Token = "";
            public long Offset;
            public int LatestUpdate = -1;
        }
        private readonly ConcurrentDictionary<string, Bot> bots = new(StringComparer.Ordinal);
        private int invalidVerifications;
        public int InvalidVerifications => Volatile.Read(ref invalidVerifications);
        public int ActiveTotal => bots.Values.Sum(static bot => Volatile.Read(ref bot.Active));
        public int Active(string id) => bots.TryGetValue(id, out Bot? bot) ? Volatile.Read(ref bot.Active) : 0;
        public int Count(string id) => bots.TryGetValue(id, out Bot? bot) ? Volatile.Read(ref bot.Requests) : 0;
        public int MaxActive(string id) => bots.TryGetValue(id, out Bot? bot) ? Volatile.Read(ref bot.MaximumActive) : 0;
        public string LastToken(string id) => bots.TryGetValue(id, out Bot? bot) ? Volatile.Read(ref bot.Token) : "";
        public long LastOffset(string id) => bots.TryGetValue(id, out Bot? bot) ? Interlocked.Read(ref bot.Offset) : -1;
        private static string UpdateBody(int update)
            => $"{{\"ok\":true,\"result\":[{{\"update_id\":{update},\"message\":{{\"from\":{{\"id\":1}},\"chat\":{{\"id\":1,\"type\":\"private\"}},\"text\":\"hello\"}}}}]}}";
        public void Update(string id, int update)
        {
            Volatile.Write(ref bots[id].LatestUpdate, update);
            QueueResponse(id, HttpStatusCode.OK, UpdateBody(update));
        }
        public void Fail(string id, int status) => QueueResponse(id, (HttpStatusCode)status, "{\"ok\":false,\"parameters\":{\"retry_after\":2}}");

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Queued responses transfer to HttpClient; rejected writes and remaining responses are disposed by this handler.")]
        private void QueueResponse(string id, HttpStatusCode status, string body)
        {
            Bot bot = bots[id];
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (!bot.Responses.Writer.TryWrite(response)) response.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (Bot bot in bots.Values)
                {
                    bot.Responses.Writer.TryComplete();
                    while (bot.Responses.Reader.TryRead(out HttpResponseMessage? response)) response.Dispose();
                }
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string token = request.RequestUri!.AbsolutePath.Split('/')[1][3..];
            string id = token.Split(':')[0];
            if (request.RequestUri.AbsolutePath.EndsWith("/getMe", StringComparison.Ordinal))
            {
                if (token.EndsWith(":invalid-secret", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref invalidVerifications);
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"ok\":false}", Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"ok\":true,\"result\":{{\"id\":{id},\"is_bot\":true}}}}", Encoding.UTF8, "application/json") };
            }
            Bot bot = bots.GetOrAdd(id, static _ => new Bot());
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            Volatile.Write(ref bot.Token, token);
            Interlocked.Exchange(ref bot.Offset, body.RootElement.GetProperty("offset").GetInt64());
            Interlocked.Increment(ref bot.Requests);
            int active = Interlocked.Increment(ref bot.Active);
            int previous;
            while (active > (previous = Volatile.Read(ref bot.MaximumActive))
                && Interlocked.CompareExchange(ref bot.MaximumActive, active, previous) != previous) { }
            try
            {
                if (bot.Responses.Reader.TryRead(out HttpResponseMessage? queued)) return queued;
                int latest = Volatile.Read(ref bot.LatestUpdate);
                if (latest >= 0 && latest >= Interlocked.Read(ref bot.Offset))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UpdateBody(latest), Encoding.UTF8, "application/json") };
                return await bot.Responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { Interlocked.Decrement(ref bot.Active); }
        }
    }
}
