using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceRecoveryPageTests
{
    [Fact]
    public async Task RecoveryPageUsesTheBrowserPortAndReleasesItForWebHost()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        occupied.Stop();
        var page = new ApplianceRecoveryPage(port);
        var retry = new DateTimeOffset(2026, 11, 22, 6, 0, 0, TimeSpan.Zero);
        var status = new ApplianceRecoveryMessage("Your helpers are paused because Kautuka couldn’t verify network protection.", retry);
        CancellationToken token = TestContext.Current.CancellationToken;
        try
        {
            await page.StartAsync(() => status, token);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage recovery = await client.GetAsync(new Uri("/", UriKind.Relative), token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, recovery.StatusCode);
            Assert.True(recovery.Headers.CacheControl?.NoStore);
            Assert.Equal("text/html", recovery.Content.Headers.ContentType?.MediaType);
            string html = await recovery.Content.ReadAsStringAsync(token);
            Assert.Contains("verify network protection", html, StringComparison.Ordinal);
            Assert.Contains("06:00:00 UTC", html, StringComparison.Ordinal);
            Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.Ordinal);
            string probe = html.Split("<script>", StringSplitOptions.None)[1].Split("</script>", StringSplitOptions.None)[0];
            string hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(probe.ReplaceLineEndings("\n"))));
            Assert.Contains($"script-src 'sha256-{hash}'; connect-src 'self'", recovery.Headers.GetValues("Content-Security-Policy").Single(),
                StringComparison.Ordinal);

            status = status with { RetryUtc = null, NextStep = "Ask your appliance administrator to review the recovery status." };
            using var navigation = new HttpRequestMessage(HttpMethod.Get, "/workspace/my-helper");
            navigation.Headers.Accept.ParseAdd("text/html");
            navigation.Headers.Host = $"localhost:{port}";
            using HttpResponseMessage manual = await client.SendAsync(navigation, token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, manual.StatusCode);
            html = await manual.Content.ReadAsStringAsync(token);
            Assert.Contains("Ask your appliance administrator", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<p id=\"next\">Kautuka will retry", html, StringComparison.Ordinal);

            using var poll = new HttpRequestMessage(HttpMethod.Get, "/health");
            poll.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage polled = await client.SendAsync(poll, token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, polled.StatusCode);
            Assert.Equal("application/json", polled.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Ask your appliance administrator", await polled.Content.ReadAsStringAsync(token), StringComparison.Ordinal);

            foreach (string endpoint in new[] { "/health", "/alive", "/api/setup" })
            {
                using HttpResponseMessage unavailable = await client.GetAsync(new Uri(endpoint, UriKind.Relative), token);
                Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
            }
            using var mutation = new HttpRequestMessage(HttpMethod.Post, "/");
            using HttpResponseMessage rejectedPost = await client.SendAsync(mutation, token);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, rejectedPost.StatusCode);
            using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/");
            foreignHost.Headers.Host = $"other.example:{port}";
            using HttpResponseMessage rejectedHost = await client.SendAsync(foreignHost, token);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedHost.StatusCode);
        }
        finally { await page.StopAsync(); }

        using var publisher = new TcpListener(IPAddress.Loopback, port);
        publisher.Start();
        Assert.Equal(port, ((IPEndPoint)publisher.LocalEndpoint).Port);
    }
}
