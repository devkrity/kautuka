using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Infrastructure.Egress;
using Kautuka.NetworkAccess.Client;

namespace Kautuka.IntegrationTests;

public sealed class GatewayConnectTransportFixtureTests
{
#pragma warning disable CA2025 // The fixture awaits the server task after its client request.
    [Fact]
    public async Task ConnectUsesMutualTlsAndCapturedIdentityAndRefusalHasNoFallback()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Local mTLS fixture requires Linux TLS support.");
        using var caKey = ECDsa.Create();
        var caRequest = new CertificateRequest("CN=test-root", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var serverKey = ECDsa.Create();
        var serverRequest = new CertificateRequest("CN=envoy.test", serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("envoy.test");
        serverRequest.CertificateExtensions.Add(san.Build());
        using X509Certificate2 serverPublic = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1), [1, 2, 3, 4]);
        using X509Certificate2 serverWithKey = serverPublic.CopyWithPrivateKey(serverKey);
        using X509Certificate2 server = X509CertificateLoader.LoadPkcs12(serverWithKey.Export(X509ContentType.Pkcs12), null);
        using var clientKey = ECDsa.Create();
        var clientRequest = new CertificateRequest("CN=gateway", clientKey, HashAlgorithmName.SHA256);
        using X509Certificate2 clientPublic = clientRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1), [2, 3, 4, 5]);
        using X509Certificate2 clientWithKey = clientPublic.CopyWithPrivateKey(clientKey);
        using X509Certificate2 client = X509CertificateLoader.LoadPkcs12(clientWithKey.Export(X509ContentType.Pkcs12), null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var wrongName = new GatewayConnectTransport("127.0.0.1", port, "wrong-envoy.test", ca, client))
        {
            Task<string> rejectedHandshake = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token);
            using var rejectedRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
            await Assert.ThrowsAsync<HttpRequestException>(() => wrongName.SendAsync(rejectedRequest, new("user-1", null, null, "model", null), timeout.Token));
            try { await rejectedHandshake; throw new InvalidOperationException("Wrong server name was accepted."); }
            catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { }
        }
        using var transport = new GatewayConnectTransport("127.0.0.1", port, "envoy.test", ca, client, maximumScopes: 1);
        Task<string> serverTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://EXAMPLE.COM/path") { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var scope = new GatewayEgressScope("user-1", "agent-1", "connector-1", "model", "correlation-1");
        HttpRequestException denied = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(request, scope, timeout.Token));
        Assert.StartsWith("Network Access blocked this connection.", RemoteMcpFailureMessages.Describe(denied), StringComparison.Ordinal);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        string connect = await serverTask;
        Assert.StartsWith("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-user: user-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-agent: agent-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-connector: connector-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-purpose: model\r\n", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("x-kautuka-correlation:", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("GET /path", connect, StringComparison.Ordinal);
        Task<string> acceptedTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true, originRequests: 2);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/resource");
        using HttpResponseMessage response = await transport.SendAsync(httpRequest, scope with { Correlation = "correlation-2" }, timeout.Token);
        using var blocked = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(blocked, scope with { User = "different-user" }, timeout.Token));
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/another-resource");
        using HttpResponseMessage secondResponse = await transport.SendAsync(secondRequest, scope with { Correlation = "correlation-3" }, timeout.Token);
        Assert.Equal("ok", await secondResponse.Content.ReadAsStringAsync(timeout.Token));
        string secondConnect = await acceptedTask;
        Assert.StartsWith("CONNECT example.com:80 HTTP/1.1\r\n", secondConnect, StringComparison.Ordinal);
        Assert.DoesNotContain("x-kautuka-correlation:", secondConnect, StringComparison.Ordinal);
        response.Dispose();
        Task<string> lastTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token);
        using var lastRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(lastRequest, scope with { Purpose = "provider-test", Correlation = "test-token" }, timeout.Token));
        Assert.Contains("x-kautuka-correlation: test-token\r\n", await lastTask, StringComparison.Ordinal);

        using var outbound = new GatewayOutboundHttpTransport(transport);
        var provider = new OllamaAiClientProvider(checker: new FixtureNetworkAccessChecker(), transportFactory: outbound.CreateModelHandler);
        var settings = new JsonObject { ["base_url"] = "http://models.example.test" };
        HttpRequestException missingIdentity = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.DiscoverModelsAsync(settings, null, throwOnFailure: true, timeout.Token));
        Assert.Contains("requires a caller identity", missingIdentity.Message, StringComparison.Ordinal);

        Task<string> discoveryTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true,
            ollamaDiscovery: true);
        using (NetworkAccessContext.BeginScope("model-user", "model-agent", purpose: "ai-discovery", service: "ai-model", correlationId: "discovery-request"))
        {
            Assert.Single(await provider.DiscoverModelsAsync(settings, null, throwOnFailure: true, timeout.Token));
        }
        string discoveryConnect = await discoveryTask;
        Assert.StartsWith("CONNECT models.example.test:80 HTTP/1.1\r\n", discoveryConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-user: model-user\r\n", discoveryConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-agent: model-agent\r\n", discoveryConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-purpose: ai-discovery\r\n", discoveryConnect, StringComparison.Ordinal);
        Assert.DoesNotContain("x-kautuka-correlation:", discoveryConnect, StringComparison.Ordinal);

        var loginChecker = new FixtureNetworkAccessChecker();
        Task<string> loginTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true, originRequests: 2);
        using (NetworkAccessContext.BeginScope("system", "gateway", purpose: "oidc-login", service: "external-auth"))
        using (var authClient = new HttpClient(new NetworkAccessCheckHandler(loginChecker, outbound.CreateApplicationTransport()!)))
        {
            var authEndpoint = new Uri("http://auth.example.test/token");
            Assert.Equal("ok", await authClient.GetStringAsync(authEndpoint, timeout.Token));

            // Keep the tunnel alive and change the policy before the next send.
            loginChecker.Allowed = false;
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(() => authClient.GetStringAsync(authEndpoint, timeout.Token));
            Assert.False(loginTask.IsCompleted);

            loginChecker.Allowed = true;
            Assert.Equal("ok", await authClient.GetStringAsync(authEndpoint, timeout.Token));
        }
        string loginConnect = await loginTask;
        Assert.Contains("x-kautuka-user: system\r\n", loginConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-agent: gateway\r\n", loginConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-purpose: oidc-login\r\n", loginConnect, StringComparison.Ordinal);

        var mcpFactory = new McpHttpClientFactory(new FixtureNetworkAccessChecker(), outbound);
        var mcpEndpoint = new Uri("http://mcp.example.test/mcp", UriKind.Absolute);
        var mcpContext = new RemoteMcpRequestContext(mcpEndpoint, "mcp-connector", "mcp-user", false, [], [],
            static (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        using (NetworkAccessContext.BeginScope("mcp-user", "mcp-agent", correlationId: "mcp-request"))
        {
            foreach (bool allowPrivate in new[] { false, true })
            {
                Task<string> mcpTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true);
                using var mcpClient = new HttpClient(mcpFactory.CreateRequestHandler(allowPrivate, mcpContext));
                Assert.Equal("ok", await mcpClient.GetStringAsync(mcpEndpoint, timeout.Token));
                string mcpConnect = await mcpTask;
                Assert.StartsWith("CONNECT mcp.example.test:80 HTTP/1.1\r\n", mcpConnect, StringComparison.Ordinal);
                Assert.Contains("x-kautuka-user: mcp-user\r\n", mcpConnect, StringComparison.Ordinal);
                Assert.Contains("x-kautuka-agent: mcp-agent\r\n", mcpConnect, StringComparison.Ordinal);
                Assert.Contains("x-kautuka-connector: mcp-connector\r\n", mcpConnect, StringComparison.Ordinal);
                Assert.Contains("x-kautuka-purpose: remote-mcp\r\n", mcpConnect, StringComparison.Ordinal);
                Assert.Equal(!allowPrivate, mcpConnect.Contains("x-kautuka-public-only: true\r\n", StringComparison.Ordinal));
            }
        }

        Task<string> originTlsTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true, originTls: true);
        using var originTlsRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        Task<HttpResponseMessage> originRequestTask = transport.SendAsync(originTlsRequest, scope with { Correlation = "origin-tls" }, timeout.Token);
        Task firstOrigin = await Task.WhenAny(originRequestTask, originTlsTask);
        if (firstOrigin == originTlsTask)
        {
            await originTlsTask;
            listener.Stop(); // Reject any automatic retry after the failed origin TLS handshake.
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => originRequestTask);
        Assert.StartsWith("CONNECT example.com:443 HTTP/1.1\r\n", await originTlsTask, StringComparison.Ordinal);
    }

#pragma warning restore CA2025
    private static async Task<string> ReceiveDeniedConnectAsync(TcpListener listener, X509Certificate2 server, X509Certificate2 client, CancellationToken token, bool accepted = false, bool originTls = false, bool ollamaDiscovery = false, int originRequests = 1)
    {
        using TcpClient socket = await listener.AcceptTcpClientAsync(token);
        using var tls = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString() == client.GetCertHashString());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = server, ClientCertificateRequired = true }, token);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[1];
        while (bytes.Length < 16384)
        {
            if (await tls.ReadAsync(buffer, token) == 0) throw new IOException("CONNECT ended early.");
            bytes.WriteByte(buffer[0]);
            if (bytes.Length >= 4 && bytes.GetBuffer().AsSpan((int)bytes.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        if (accepted)
        {
            await tls.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
            if (originTls)
            {
                Assert.Equal(1, await tls.ReadAsync(buffer, token));
                Assert.Equal((byte)0x16, buffer[0]); // TLS handshake record, never a plaintext HTTP method.
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            for (int request = 0; request < originRequests; request++)
            {
                using var originHeaders = new MemoryStream();
                while (originHeaders.Length < 16384)
                {
                    if (await tls.ReadAsync(buffer, token) == 0) throw new IOException("Origin request ended early.");
                    originHeaders.WriteByte(buffer[0]);
                    if (originHeaders.Length >= 4 && originHeaders.GetBuffer().AsSpan((int)originHeaders.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                string body = "ok";
                if (ollamaDiscovery)
                {
                    Assert.StartsWith("GET /api/tags HTTP/1.1\r\n", Encoding.ASCII.GetString(originHeaders.ToArray()), StringComparison.Ordinal);
                    body = """{"models":[{"name":"fixture-model"}]}""";
                }
                string connection = request == originRequests - 1 ? "close" : "keep-alive";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(FormattableString.Invariant(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: {connection}\r\n\r\n{body}")), token);
            }
        }
        else await tls.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), token);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private sealed class FixtureNetworkAccessChecker : INetworkAccessChecker
    {
        public bool Allowed { get; set; } = true;

        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
            => Task.FromResult(Allowed
                ? NetworkAccessCheckResult.CreateAllowed("fixture")
                : NetworkAccessCheckResult.CreateDenied("fixture"));
    }
}
