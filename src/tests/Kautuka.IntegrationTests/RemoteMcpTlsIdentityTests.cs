using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpTlsIdentityTests
{
    [Fact]
    public void ExaPrefersPublicIpv4ButAllowsIpv6Fallback()
    {
        IPAddress ipv6 = IPAddress.Parse("2001:db8::2");
        IPAddress ipv4 = IPAddress.Parse("203.0.113.9");
        IPAddress[] dualStack = [ipv6, ipv4];

        Assert.Equal(ipv6, ProbeNetworkPolicy.SelectAddress(dualStack, allowPrivate: false, preferIpv4: false));
        Assert.Equal(ipv4, ProbeNetworkPolicy.SelectAddress(dualStack, allowPrivate: false, preferIpv4: true));
        Assert.Equal(ipv6, ProbeNetworkPolicy.SelectAddress([ipv6], allowPrivate: false, preferIpv4: true));
        Assert.Equal("Exa free access needs a direct IPv4 connection. Add an Exa API key or use a network with public IPv4 access.",
            RemoteMcpFailureMessages.Describe(new HttpRequestException("IPv4 is unavailable", new SocketException((int)SocketError.NetworkUnreachable)), keylessExa: true));
    }

    [Fact]
    public void RemoteMcpFailuresUsePlainOwnerMessages()
    {
        Assert.Equal("The remote MCP address could not be found.", RemoteMcpFailureMessages.Describe(
            new HttpRequestException("lookup failed", new SocketException((int)SocketError.HostNotFound))));
        Assert.Contains("secure connection", RemoteMcpFailureMessages.Describe(new AuthenticationException("certificate failed")), StringComparison.Ordinal);
        Assert.Contains("not allowed", RemoteMcpFailureMessages.Describe(new HttpRequestException("This destination is not allowed by connector network policy.")), StringComparison.Ordinal);
        Assert.Contains("not allowed", RemoteMcpFailureMessages.Describe(new HttpRequestException("This destination isn't allowed by connector network policy.")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", false)]
    public async Task PinnedSocketPreservesHostnameSniAndCertificateIdentity(string hostname, bool validIdentity)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using RSA rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(san.Build());
        using X509Certificate2 generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // Windows Schannel needs an imported key handle, not CreateSelfSigned's ephemeral handle.
        // DefaultKeySet (without PersistKeySet) releases the test key with certificate disposal.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? serverName = null;
        string? receivedRequest = null;
        Task server = ServeAsync();
        using SocketsHttpHandler handler = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivate: true);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        var originalConnect = handler.ConnectCallback!;
        string? checkedHostname = null;
        IPAddress? connectedAddress = null;
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            checkedHostname = context.DnsEndPoint.Host;
            Stream stream = await originalConnect(context, cancellationToken);
            connectedAddress = ((IPEndPoint)((NetworkStream)stream).Socket.RemoteEndPoint!).Address;
            return stream;
        };
        SslPolicyErrors? certificateErrors = null;
        // Trust only the local test's self-signed chain. Normal hostname verification remains active.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, errors) =>
        {
            certificateErrors = errors;
            return (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None;
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var uri = new Uri($"https://{hostname}:{port}/mcp");
            if (validIdentity)
            {
                using HttpResponseMessage response = await client.GetAsync(uri, token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("localhost", serverName);
                Assert.False(certificateErrors!.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(uri, token));
                Assert.True(certificateErrors!.Value.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch));
            }
            await server.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(hostname, checkedHostname);
            Assert.True(IPAddress.IsLoopback(connectedAddress!));
            if (validIdentity) Assert.Contains($"Host: localhost:{port}", receivedRequest, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        async Task ServeAsync()
        {
            using TcpClient socket = await listener.AcceptTcpClientAsync(token);
            await using var tls = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync((_, hello, _, _) =>
                {
                    serverName = hello.ServerName;
                    return ValueTask.FromResult(new SslServerAuthenticationOptions { ServerCertificate = certificate });
                }, null, token);
                if (!validIdentity) return;
                var bytes = new byte[4096];
                int count = await tls.ReadAsync(bytes, token);
                receivedRequest = Encoding.ASCII.GetString(bytes, 0, count);
                await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
            }
            catch (AuthenticationException) when (!validIdentity) { }
            catch (IOException) when (!validIdentity) { }
        }
    }
}
