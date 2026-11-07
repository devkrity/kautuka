using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class ArtifactHttpTransportTests
{
    [Fact]
    public async Task RepeatedArtifactRequestsUseFreshVerifiedTlsTunnels()
    {
        string root = Path.Combine(Path.GetTempPath(), "ka-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "proxy.sock");
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=artifact.kautuka.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("artifact.kautuka.test");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Match the existing TLS tests: Windows Schannel needs an imported key handle.
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(20));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(8);
        int connections = 0;
        var serving = new List<Task>();
        async Task ServeAsync(Socket accepted)
        {
            using var stream = new NetworkStream(accepted, ownsSocket: true);
            try
            {
                string connect = await ReadHeadersAsync(stream, stop.Token);
                Assert.StartsWith("CONNECT artifact.kautuka.test:443 HTTP/1.1", connect, StringComparison.Ordinal);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop.Token);
                using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    string headers = await ReadHeadersAsync(tls, stop.Token);
                    if (headers.Length == 0) return;
                    // A healthy keep-alive origin makes connection reuse
                    // observable: the transport must still open a new tunnel.
                    await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"u8.ToArray(), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) when (stop.IsCancellationRequested) { }
        }
        Task accepting = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Socket socket = await listener.AcceptAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                    serving.Add(ServeAsync(socket));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, TestContext.Current.CancellationToken);
        try
        {
            using SocketsHttpHandler handler = KautukaArtifactHttpTransport.Create(path);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                cert is X509Certificate2 peer && !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
                && peer.RawData.AsSpan().SequenceEqual(certificate.RawData);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            for (int i = 0; i < 4; i++)
                Assert.Equal("ok", await client.GetStringAsync(new Uri("https://artifact.kautuka.test/release"), stop.Token));
            Assert.Equal(4, Volatile.Read(ref connections));
        }
        finally
        {
            await stop.CancelAsync();
            await accepting;
            await Task.WhenAll(serving);
            listener.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] next = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(next, cancellationToken) == 0) return bytes.Count == 0 ? string.Empty : throw new IOException("Incomplete request headers.");
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Request headers exceeded the test bound.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingProxyCannotFallBackToAReachableLoopbackDestination(int selection)
    {
        string root = Path.Combine(Path.GetTempPath(), "ka-" + Guid.NewGuid().ToString("N")[..8]);
        string socket = Path.Combine(root, "missing.sock");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kautuka:Mode"] = "prod",
            ["ContainerEgress:ArtifactSocketPath"] = selection == 1 ? socket : null,
            ["ContainerEgress:Enabled"] = selection == 2 ? "true" : "false",
            ["Kautuka:ContainerStateRoot"] = root,
        });
        try
        {
            if (selection == 3)
            {
                Directory.CreateDirectory(root);
                string settings = Path.Combine(root, "manager.json");
                string contract = Path.Combine(root, "service-scope.json");
                await File.WriteAllTextAsync(settings, new JsonObject
                {
                    ["CoreServices"] = new JsonObject { ["Enabled"] = true },
                    ["ContainerEgress"] = new JsonObject { ["Enabled"] = true, ["ArtifactSocketPath"] = socket },
                }.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(contract, "{\"applicationServices\":\"containers\"}", TestContext.Current.CancellationToken).ConfigureAwait(false);
                KautukaInstalledDeploymentConfiguration.Add(configuration, settings, contract);
            }
            using SocketsHttpHandler handler = selection == 0 ? KautukaArtifactHttpTransport.Create(socket) : KautukaArtifactHttpTransport.CreateForConfiguration(configuration);
            Assert.True(handler.UseProxy);
            Assert.False(handler.Proxy!.IsBypassed(new Uri("https://127.0.0.1:" + port)));
            Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(new Uri("https://127.0.0.1:" + port), TestContext.Current.CancellationToken));
            Assert.False(listener.Pending());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
