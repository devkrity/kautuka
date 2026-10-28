using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Protos;
using Kautuka.ProviderSimulator;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

/// <summary>Exercises a production adapter's real HttpClient against the standalone simulator process.</summary>
public sealed class ChannelHostSimulatorNetworkTests
{
    [Fact]
    public async Task TelegramWorkerUsesSimulatorProfileOverLoopbackWithoutExternalEscape()
    {
#pragma warning disable CA2007 // await using disposal is intentionally tied to xUnit's test context.
        await using var simulator = await SimulatorProcess.StartAsync().ConfigureAwait(true);
#pragma warning restore CA2007
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:Channels:TransportProfile"] = "Simulator", ["Kautuka:Channels:SimulatorBaseAddress"] = simulator.Address.AbsoluteUri }).Build();
        var profile = new ChannelProviderTransportProfile(configuration, new TestEnvironment());
        using var handler = new ChannelProviderTransportRewriteHandler(profile) { InnerHandler = new HttpClientHandler() };
        using var client = new HttpClient(handler); var worker = new TelegramLongPollingWorker(null!, null!, null!, new SingleFactory(client), NullLogger<TelegramLongPollingWorker>.Instance);
        var health = await worker.GetHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal("healthy", health.Status); Assert.Throws<InvalidOperationException>(() => profile.Rewrite(new Uri("https://example.invalid/path")));
    }

    [Fact]
    public async Task TelegramWorkerPollsSimulatorAndPersistsAnInboundMessage()
    {
#pragma warning disable CA2007 // await using disposal is intentionally tied to xUnit's test context.
        await using var simulator = await SimulatorProcess.StartAsync().ConfigureAwait(true);
#pragma warning restore CA2007
        string root = Path.Combine(Path.GetTempPath(), "kautuka-telegram-simulator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root, ["Kautuka:Channels:TransportProfile"] = "Simulator", ["Kautuka:Channels:SimulatorBaseAddress"] = simulator.Address.AbsoluteUri }).Build();
            var database = new ChannelHostDatabase(configuration); database.EnsureReady(); var credentials = new ChannelRuntimeCredentialStore(); var attachments = new ChannelAttachmentStore(database, credentials);
            AddAttachment(attachments, "telegram-sim", "telegram", "telegram-owner"); credentials.Replace("telegram-sim", [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "test-token" }]);
            using var ingress = new ChannelIngressStore(database); using var handler = new ChannelProviderTransportRewriteHandler(new ChannelProviderTransportProfile(configuration, new TestEnvironment())) { InnerHandler = new HttpClientHandler() }; using var client = new HttpClient(handler);
            var worker = new TelegramLongPollingWorker(attachments, ingress, new ChannelOutboundAssetStore(database, configuration), new SingleFactory(client), NullLogger<TelegramLongPollingWorker>.Instance);
            await simulator.InjectAsync("telegram", new { update_id = 7, message = new { message_id = 1, chat = new { id = 44 }, from = new { id = 55 }, text = "simulated inbound" } });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)); Task run = worker.RunAsync(cancellation.Token);
            await using IAsyncEnumerator<ChannelInboundEventMessage> events = ingress.StreamAsync(ChannelIngressStore.GatewaySubscriberId, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            Assert.True(await events.MoveNextAsync().ConfigureAwait(true)); Assert.Equal("44", events.Current.ConversationId); Assert.Equal("55", events.Current.ExternalUserId); Assert.Equal("8", ingress.GetCursor("telegram", "telegram-bot:1")); await cancellation.CancelAsync().ConfigureAwait(true); await run.ConfigureAwait(true);
        }
        finally { ChannelHostSqliteTestCleanup.ReleaseAndDelete(root); }
    }

    private static IConfiguration CreateSimulatorConfiguration(string root, Uri address) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kautuka:ContainerStateRoot"] = root,
            ["Kautuka:Channels:TransportProfile"] = "Simulator",
            ["Kautuka:Channels:SimulatorBaseAddress"] = address.AbsoluteUri,
        }).Build();

    private static SendOutboundMessageRequest SendText(string conversationId) => new()
    {
        Content = new OutboundContentEnvelope
        {
            ActionType = "send_text",
            PayloadJson = JsonSerializer.Serialize(
                new ChannelTextMessageAction(conversationId, null, "simulator outbound"),
                ChannelContractsJsonContext.Default.ChannelTextMessageAction),
        },
    };

    private static void AddAttachment(ChannelAttachmentStore attachments, string id, string provider, string owner) =>
        _ = attachments.UpsertAttachment(new UpsertAttachmentRequest { AttachmentId = id, ProviderKey = provider, ExpectedSchemaVersion = provider + "/v1", Settings = { new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = owner }, new AttachmentSettingValue { FieldKey = "display_name", StringValue = provider + " simulator" } } });

    private sealed class SingleFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class TestEnvironment : IHostEnvironment { public string EnvironmentName { get; set; } = "Test"; public string ApplicationName { get; set; } = "test"; public string ContentRootPath { get; set; } = AppContext.BaseDirectory; public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider(); }
    private sealed class SimulatorProcess(Uri address, Process process) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public async Task InjectAsync(string provider, object payload)
        {
            using var client = new HttpClient { BaseAddress = Address }; client.DefaultRequestHeaders.Add("X-Kautuka-Simulator-Key", "test"); using HttpResponseMessage response = await client.PostAsJsonAsync("control/inject/" + provider, payload).ConfigureAwait(true); response.EnsureSuccessStatusCode();
        }
        public async Task ScheduleFaultAsync(string target, string kind)
        {
            using var client = new HttpClient { BaseAddress = Address };
            client.DefaultRequestHeaders.Add("X-Kautuka-Simulator-Key", "test");
            using HttpResponseMessage response = await client.PostAsJsonAsync("control/faults", new { target, kind, count = 1 }).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
        }
        public async Task<JsonDocument> EvidenceAsync()
        {
            using var client = new HttpClient { BaseAddress = Address }; client.DefaultRequestHeaders.Add("X-Kautuka-Simulator-Key", "test"); using HttpResponseMessage response = await client.GetAsync(new Uri(Address, "control/evidence")).ConfigureAwait(true); response.EnsureSuccessStatusCode(); return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(true)).ConfigureAwait(true);
        }
        public static async Task<SimulatorProcess> StartAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); Uri address = new($"http://127.0.0.1:{port}/");
            Process process = Process.Start(new ProcessStartInfo("dotnet", $"\"{typeof(SimulatorHost).Assembly.Location}\" --ProviderSimulator:ControlKey=test --ProviderSimulator:Listen={address}") { UseShellExecute = false, CreateNoWindow = true })!;
            using var http = new HttpClient(); for (int i = 0; i < 50; i++) { try { if ((await http.GetAsync(new Uri(address, "health")).ConfigureAwait(true)).IsSuccessStatusCode) return new(address, process); } catch (HttpRequestException) { } await Task.Delay(100).ConfigureAwait(true); }
            process.Kill(true); throw new TimeoutException("simulator_not_ready");
        }
        public async ValueTask DisposeAsync() { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(true); } process.Dispose(); }
    }
}
