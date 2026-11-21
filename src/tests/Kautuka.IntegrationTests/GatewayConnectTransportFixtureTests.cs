using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Kautuka.Gateway.Infrastructure.Egress;

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
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(request, scope, timeout.Token));
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        string connect = await serverTask;
        Assert.StartsWith("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-user: user-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-agent: agent-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-connector: connector-1\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-purpose: model\r\n", connect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-correlation: correlation-1\r\n", connect, StringComparison.Ordinal);
        Assert.DoesNotContain("GET /path", connect, StringComparison.Ordinal);
        Task<string> acceptedTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token, accepted: true);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/resource");
        using HttpResponseMessage response = await transport.SendAsync(httpRequest, scope with { Correlation = "correlation-2" }, timeout.Token);
        using var blocked = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(blocked, scope with { Correlation = "correlation-3" }, timeout.Token));
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
        string secondConnect = await acceptedTask;
        Assert.StartsWith("CONNECT example.com:80 HTTP/1.1\r\n", secondConnect, StringComparison.Ordinal);
        Assert.Contains("x-kautuka-correlation: correlation-2\r\n", secondConnect, StringComparison.Ordinal);
        response.Dispose();
        Task<string> lastTask = ReceiveDeniedConnectAsync(listener, server, client, timeout.Token);
        using var lastRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(lastRequest, scope with { Correlation = "correlation-3" }, timeout.Token));
        Assert.Contains("x-kautuka-correlation: correlation-3\r\n", await lastTask, StringComparison.Ordinal);
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
    private static async Task<string> ReceiveDeniedConnectAsync(TcpListener listener, X509Certificate2 server, X509Certificate2 client, CancellationToken token, bool accepted = false, bool originTls = false)
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
                using var origin = new SslStream(tls, leaveInnerStreamOpen: true);
                try
                {
                    await origin.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = server }, token);
                    _ = await origin.ReadAsync(buffer, token);
                }
                catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            int matched = 0;
            while (matched < 4)
            {
                if (await tls.ReadAsync(buffer, token) == 0) throw new IOException("Origin request ended early.");
                matched = buffer[0] == "\r\n\r\n"u8[matched] ? matched + 1 : 0;
            }
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), token);
        }
        else await tls.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), token);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
