#pragma warning disable CA2000, CA2007, CA2234, CA1849, IDE0005, xUnit1051

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Kautuka.ProviderSimulator;

namespace Kautuka.ProviderSimulator.Tests;

public sealed class ProviderSimulatorLoopbackTests
{
    [Theory]
    [InlineData("transient", HttpStatusCode.ServiceUnavailable)]
    [InlineData("truncated", HttpStatusCode.OK)]
    [InlineData("rate_limit", HttpStatusCode.TooManyRequests)]
    [InlineData("uncertain_effect", HttpStatusCode.ServiceUnavailable)]
    [InlineData("drop", HttpStatusCode.ServiceUnavailable)]
    public async Task EveryNamedImmediateFaultIsExecutedOverTheProviderNetwork(string kind, HttpStatusCode expected)
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/faults", new { target = "telegram/sendMessage", kind, count = 1, delayMilliseconds = 0 });
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("telegram/bottest/sendMessage", new { text = "not retained" });
        Assert.Equal(expected, response.StatusCode);
        if (kind == "truncated") Assert.Equal("{\"ok\":", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TimeoutFaultIsBoundedAndDoesNotBlockTheSimulatorProcess()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/faults", new { target = "slack/auth.test", kind = "timeout", count = 1, delayMilliseconds = 25 });
        Stopwatch started = Stopwatch.StartNew(); using HttpResponseMessage response = await host.Client.PostAsync("slack/api/auth.test", null); started.Stop();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.InRange(started.ElapsedMilliseconds, 15, 2_000);
    }

    [Fact]
    public async Task SlackAuthTestAdvertisesAllScopesRequiredForReadiness()
    {
        await using var host = await LoopbackSimulator.StartAsync();

        using HttpResponseMessage response = await host.Client.PostAsync("slack/api/auth.test", null);

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.TryGetValues("x-oauth-scopes", out IEnumerable<string>? values));
        string scopes = Assert.Single(values);
        foreach (string required in new[] { "app_mentions:read", "channels:history", "chat:write", "files:write", "groups:history", "im:history", "mpim:history", "reactions:write" })
        {
            Assert.Contains(required, scopes, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TelegramOffsetsDuplicatesFaultsAndPersistedRestartRunAcrossLoopback()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/inject/telegram", new { update_id = 8, message = new { text = "redacted" }, _simulatorDuplicate = true });
        await host.ControlAsync("control/inject/telegram", new { update_id = 4, _simulatorOutOfOrder = true });
        using HttpResponseMessage updates = await host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 5 });
        updates.EnsureSuccessStatusCode(); string updatesBody = await updates.Content.ReadAsStringAsync(); Assert.Contains("\"update_id\":8", updatesBody, StringComparison.Ordinal); Assert.DoesNotContain("\"update_id\":4", updatesBody, StringComparison.Ordinal);
        await host.ControlAsync("control/faults", new { target = "telegram/sendMessage", kind = "rate_limit", count = 1 });
        using HttpResponseMessage limited = await host.Client.PostAsJsonAsync("telegram/bottest/sendMessage", new { text = "secret" }); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.Equal("1", limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        await host.RestartAsync();
        using HttpResponseMessage afterRestart = await host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 8 }); Assert.Contains("\"update_id\":8", await afterRestart.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        JsonDocument evidence = await host.EvidenceAsync(); string serialized = evidence.RootElement.GetRawText(); Assert.DoesNotContain("redacted", serialized, StringComparison.Ordinal); Assert.DoesNotContain("secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramAssignsLongUpdateIdentityWhenControlIngressOmitsIt()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/inject/telegram", new { callback_query = new { id = "opaque-action" } });

        using HttpResponseMessage updates = await host.Client.PostAsJsonAsync(
            "telegram/bottest/getUpdates",
            new { offset = 0L });

        updates.EnsureSuccessStatusCode();
        using JsonDocument payload = JsonDocument.Parse(
            await updates.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, payload.RootElement.GetProperty("result")[0].GetProperty("update_id").GetInt64());
    }

    [Fact]
    public async Task TelegramGetUpdatesHonorsAnEmptyLongPollTimeout()
    {
        await using var host = await LoopbackSimulator.StartAsync();

        Stopwatch elapsed = Stopwatch.StartNew();
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 1L, timeout = 1 });
        elapsed.Stop();

        response.EnsureSuccessStatusCode();
        Assert.InRange(elapsed.ElapsedMilliseconds, 700, 4_000);
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(payload.RootElement.GetProperty("result").EnumerateArray());
    }

    [Fact]
    public async Task TelegramGetUpdatesReturnsTheMatchingUpdateWhenControlIngressWakesTheLongPoll()
    {
        await using var host = await LoopbackSimulator.StartAsync();

        Task<HttpResponseMessage> pending = host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 7L, timeout = 10 });
        await host.ControlAsync("control/inject/telegram", new { update_id = 7L, message = new { text = "redacted" } });
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"update_id\":7", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramGetUpdatesCancelsWithoutLeavingTheSimulatorBlocked()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 1L, timeout = 30 }, cancelled.Token));

        using HttpResponseMessage readiness = await host.Client.GetAsync("health");
        readiness.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TelegramResetReplacesTheWakeSignalAndDropsPreResetUpdates()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/inject/telegram", new { update_id = 4L });

        Task<HttpResponseMessage> pending = host.Client.PostAsJsonAsync("telegram/bottest/getUpdates", new { offset = 5L, timeout = 10 });
        await host.ControlAsync("control/reset", new { });
        await host.ControlAsync("control/inject/telegram", new { update_id = 5L });
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"update_id\":5", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"update_id\":4", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlackSocketExercisesHelloEnvelopeAckAndConsumedSurfaceRejection()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        await host.ControlAsync("control/inject/slack", new { type = "event_callback", event_id = "E1" });
        using var slack = new ClientWebSocket(); await slack.ConnectAsync(host.WebSocket("slack/socket"), CancellationToken.None);
        Assert.Contains("hello", await ReceiveAsync(slack), StringComparison.Ordinal); Assert.Contains("envelope_id", await ReceiveAsync(slack), StringComparison.Ordinal); await slack.SendAsync(Encoding.UTF8.GetBytes("{\"envelope_id\":\"sim-1\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        using HttpResponseMessage rejected = await host.Client.PostAsJsonAsync("slack/api/not.consumed", new { }); Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        JsonDocument evidence = await host.EvidenceAsync(); Assert.Contains(evidence.RootElement.GetProperty("transcript").EnumerateArray(), e => e.GetProperty("provider").GetString() == "slack");
    }

    [Fact]
    public async Task EvidenceExposesOpaqueHitlActionsWithoutRetainingProviderMessageContent()
    {
        await using var host = await LoopbackSimulator.StartAsync();
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "telegram/bottest/sendMessage",
            new
            {
                text = "approval details must remain redacted",
                reply_markup = new
                {
                    inline_keyboard = new[]
                    {
                        new[] { new { text = "Approve", callback_data = "hitl:interaction-42:approve" } },
                    },
                },
            });
        response.EnsureSuccessStatusCode();

        JsonDocument evidence = await host.EvidenceAsync();
        string serialized = evidence.RootElement.GetRawText();
        Assert.Contains("hitl:interaction-42:approve", serialized, StringComparison.Ordinal);
        Assert.Equal(
            "101",
            evidence.RootElement.GetProperty("transcript")[0].GetProperty("transportMessageId").GetString());
        Assert.DoesNotContain("approval details must remain redacted", serialized, StringComparison.Ordinal);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket)
    {
        byte[] bytes = new byte[4096]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); WebSocketReceiveResult result = await socket.ReceiveAsync(bytes, timeout.Token); return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }

    private sealed class LoopbackSimulator : IAsyncDisposable
    {
        private readonly int port; private readonly string key = "simulator-test-key"; private readonly string stateFile; private Process? process;
        private LoopbackSimulator(int port, string stateFile)
        {
            this.port = port;
            this.stateFile = stateFile;
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            Client.DefaultRequestHeaders.Add("X-Kautuka-Simulator-Key", key);
        }
        public HttpClient Client { get; }
        public static async Task<LoopbackSimulator> StartAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var host = new LoopbackSimulator(port, Path.Combine(Path.GetTempPath(), "kautuka-simulator-tests", Guid.NewGuid().ToString("N"), "state.json")); await host.LaunchAsync(); return host;
        }
        public Uri WebSocket(string path) => new($"ws://127.0.0.1:{port}/{path}");
        public async Task ControlAsync(string path, object body) { using HttpResponseMessage response = await Client.PostAsJsonAsync(path, body); response.EnsureSuccessStatusCode(); }
        public async Task<JsonDocument> EvidenceAsync() { using HttpResponseMessage response = await Client.GetAsync("control/evidence"); response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStreamAsync()); }
        public async Task RestartAsync() { await StopAsync(); await LaunchAsync(); }
        private async Task LaunchAsync()
        {
            string assembly = typeof(SimulatorHost).Assembly.Location;
            process = Process.Start(new ProcessStartInfo("dotnet", $"\"{assembly}\" --ProviderSimulator:ControlKey={key} --ProviderSimulator:Listen=http://127.0.0.1:{port} --ProviderSimulator:StateFile=\"{stateFile}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            for (int attempt = 0; attempt != 50; ++attempt) { try { using HttpResponseMessage ready = await Client.GetAsync("health"); if (ready.IsSuccessStatusCode) return; } catch (HttpRequestException) { } await Task.Delay(100); }
            throw new TimeoutException("simulator_not_ready");
        }
        private async Task StopAsync() { if (process is null || process.HasExited) return; process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); process = null; }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Client.Dispose();
            string directory = Path.GetDirectoryName(stateFile)!;
            if (!Directory.Exists(directory))
            {
                return;
            }

            // Windows can keep state.json.tmp open briefly after kill; best-effort cleanup.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
                }
            }
        }
    }
}
