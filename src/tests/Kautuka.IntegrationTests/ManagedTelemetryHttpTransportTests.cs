using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics.CodeAnalysis;

using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Telemetry;

using OpenTelemetry.Exporter;

namespace Kautuka.IntegrationTests;

public sealed class ManagedTelemetryHttpTransportTests
{
    [Theory]
    [InlineData("https://kautuka-telemetry.internal/api/default/v1/logs")]
    [InlineData("http://other.invalid/api/default/v1/logs")]
    [InlineData("http://kautuka-telemetry.internal:81/api/default/v1/logs")]
    [InlineData("http://kautuka-telemetry.internal/admin")]
    [InlineData("http://kautuka-telemetry.internal/api/default/v1/logs?token=probe")]
    public void RejectsNonManagedExporterEndpoints(string endpoint)
    {
        var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
        Assert.Throws<InvalidOperationException>(() => ManagedTelemetryHttpTransport.Configure(options,
            new Uri(endpoint), Path.Combine(Path.GetTempPath(), "unused-telemetry.sock")));
    }

    [Fact]
    public async Task AnotherAuthorityCannotFallBackToTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using HttpClient client = ManagedTelemetryHttpTransport.CreateClient(Path.Combine(Path.GetTempPath(), "absent-telemetry.sock"));
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{port}/"),
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
            Assert.False(listener.Pending());
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(204)]
    [InlineData(302)]
    public async Task UsesUnixSocketAndDoesNotFollowRedirects(int status)
    {
        Assert.SkipUnless(Socket.OSSupportsUnixDomainSockets, "Requires Unix domain sockets.");
        string path = Path.Combine(Path.GetTempPath(), "kt-" + Guid.NewGuid().ToString("N")[..8]);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            Task<string> server = RespondAsync(listener, status, port);
            var endpoint = new Uri(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/logs");
            var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
            ManagedTelemetryHttpTransport.Configure(options, endpoint, path);
            using HttpClient client = options.HttpClientFactory();
            using HttpResponseMessage response = await client.PostAsync(endpoint, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("POST /api/default/v1/logs HTTP/1.1", await server.ConfigureAwait(true));
            Assert.False(tcp.Pending());
        }
        finally
        {
            tcp.Stop();
            listener.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SendsOnlyPersistedIngestionCredentialToFixedOtlpRoute()
    {
        Assert.SkipUnless(Socket.OSSupportsUnixDomainSockets, "Requires Unix domain sockets.");
        string root = Path.Combine(Path.GetTempPath(), "kt-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "connect.sock");
        string authorizationPath = Path.Combine(root, "ingestion.authorization");
        const string authorization = "Basic a2F1dHVrYS10ZWxlbWV0cnlAa2F1dHVrYS5sb2NhbDppbmdlc3Rpb24tdG9rZW4=";
        await File.WriteAllTextAsync(authorizationPath, authorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            Task<(string Request, string? Authorization)> server = RespondWithAuthorizationAsync(listener);
            var endpoint = new Uri(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/logs");
            var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
            ManagedTelemetryHttpTransport.Configure(options, endpoint, path, authorizationPath);
            using HttpClient client = options.HttpClientFactory();
            using HttpResponseMessage response = await client.PostAsync(endpoint, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            (string request, string? sentAuthorization) = await server.ConfigureAwait(true);
            Assert.Equal("POST /api/default/v1/logs HTTP/1.1", request);
            Assert.Equal(authorization, sentAuthorization);
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Verifies the synchronous Send path used by the OpenTelemetry metrics exporter.")]
    public async Task SynchronousSendUsesRotatedIngestionCredentialOverUnixSocket()
    {
        Assert.SkipUnless(Socket.OSSupportsUnixDomainSockets, "Requires Unix domain sockets.");
        string root = Path.Combine(Path.GetTempPath(), "kt-auth-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "connect.sock");
        string authorizationPath = Path.Combine(root, "ingestion.authorization");
        const string firstAuthorization = "Basic Zmlyc3Q6dG9rZW4=";
        const string secondAuthorization = "Basic c2Vjb25kOnRva2Vu=";
        await File.WriteAllTextAsync(authorizationPath, firstAuthorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(2);
            var endpoint = new Uri(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/metrics");
            var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
            ManagedTelemetryHttpTransport.Configure(options, endpoint, path, authorizationPath);
            using HttpClient client = options.HttpClientFactory();

            Task<(string Request, string? Authorization)> firstServer = RespondWithAuthorizationAsync(listener);
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (HttpResponseMessage response = client.Send(request, TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(firstAuthorization, (await firstServer.ConfigureAwait(false)).Authorization);

            await File.WriteAllTextAsync(authorizationPath, secondAuthorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Task<(string Request, string? Authorization)> secondServer = RespondWithAuthorizationAsync(listener);
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (HttpResponseMessage response = client.Send(request, TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(secondAuthorization, (await secondServer.ConfigureAwait(false)).Authorization);

            File.Delete(authorizationPath);
            using var missing = new HttpRequestMessage(HttpMethod.Post, endpoint);
            Assert.Throws<HttpRequestException>(() => client.Send(missing, TestContext.Current.CancellationToken));
            Assert.False(listener.Poll(100_000, SelectMode.SelectRead));
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotReadOrSendIngestionCredentialToAdminRoute()
    {
        Assert.SkipUnless(Socket.OSSupportsUnixDomainSockets, "Requires Unix domain sockets.");
        string root = Path.Combine(Path.GetTempPath(), "kt-auth-deny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "connect.sock");
        string authorizationPath = Path.Combine(root, "ingestion.authorization");
        await File.WriteAllTextAsync(authorizationPath, "invalid-but-must-not-be-read", TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using HttpClient client = ManagedTelemetryHttpTransport.CreateClient(path, authorizationPath);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(
                new Uri(KautukaTelemetryDefaults.ManagedHostEndpoint + "/ingestion-tokens"),
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
            Assert.False(listener.Poll(100_000, SelectMode.SelectRead));
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CoreConfigureLoadsRotatableTokenFileAndRestrictsCredentialedPost()
    {
        string root = Path.Combine(Path.GetTempPath(), "kt-core-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string authorizationPath = Path.Combine(root, "ingestion.authorization");
        const string authorization = "Basic a2F1dHVrYS10ZWxlbWV0cnlAa2F1dHVrYS5sb2NhbDppbmdlc3Rpb24tdG9rZW4=";
        await File.WriteAllTextAsync(authorizationPath, authorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var innerHandler = new CapturingHttpMessageHandler();
        try
        {
            var endpoint = new Uri("http://10.90.0.1:5080/api/default/v1/logs");
            var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
            ManagedTelemetryHttpTransport.ConfigureForTesting(options, endpoint, authorizationPath, () => innerHandler);
            using HttpClient client = options.HttpClientFactory();
            using HttpResponseMessage response = await client.PostAsync(endpoint, null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(authorization, innerHandler.Authorization);
            Assert.Equal(endpoint, innerHandler.RequestUri);

            await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(
                new Uri("http://10.90.0.2:5080/api/default/v1/logs"), null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(endpoint, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            Assert.Equal(1, innerHandler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Verifies the synchronous Send path used by the OpenTelemetry metrics exporter.")]
    public async Task CoreSynchronousSendRechecksEndpointAndReadsRotatedCredential()
    {
        string root = Path.Combine(Path.GetTempPath(), "kt-core-auth-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string authorizationPath = Path.Combine(root, "ingestion.authorization");
        const string firstAuthorization = "Basic Zmlyc3Q6dG9rZW4=";
        const string secondAuthorization = "Basic c2Vjb25kOnRva2Vu=";
        await File.WriteAllTextAsync(authorizationPath, firstAuthorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var innerHandler = new CapturingHttpMessageHandler();
        try
        {
            var endpoint = new Uri("http://10.90.0.1:5080/api/default/v1/metrics");
            var options = new OtlpExporterOptions { Protocol = OtlpExportProtocol.HttpProtobuf };
            ManagedTelemetryHttpTransport.ConfigureForTesting(options, endpoint, authorizationPath, () => innerHandler);
            using HttpClient client = options.HttpClientFactory();
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (HttpResponseMessage response = client.Send(request, TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(firstAuthorization, innerHandler.Authorization);

            await File.WriteAllTextAsync(authorizationPath, secondAuthorization, TestContext.Current.CancellationToken).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            using (HttpResponseMessage response = client.Send(request, TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(secondAuthorization, innerHandler.Authorization);

            using var wrongOrigin = new HttpRequestMessage(HttpMethod.Post, "http://10.90.0.2:5080/api/default/v1/metrics");
            Assert.Throws<HttpRequestException>(() => client.Send(wrongOrigin, TestContext.Current.CancellationToken));
            Assert.Equal(2, innerHandler.RequestCount);

            File.Delete(authorizationPath);
            using var missing = new HttpRequestMessage(HttpMethod.Post, endpoint);
            Assert.Throws<HttpRequestException>(() => client.Send(missing, TestContext.Current.CancellationToken));
            Assert.Equal(2, innerHandler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> RespondAsync(Socket listener, int status, int port)
    {
        using Socket accepted = await listener.AcceptAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var stream = new NetworkStream(accepted, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string first = await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false) ?? string.Empty;
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))) { }
        string redirect = status == 302 ? $"Location: http://127.0.0.1:{port}/\r\n" : string.Empty;
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n{redirect}Content-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return first;
    }

    private static async Task<(string Request, string? Authorization)> RespondWithAuthorizationAsync(Socket listener)
    {
        using Socket accepted = await listener.AcceptAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        using var stream = new NetworkStream(accepted, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string request = await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false) ?? string.Empty;
        string? authorization = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false)))
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                authorization = line["Authorization:".Length..].Trim();
        }
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, TestContext.Current.CancellationToken).ConfigureAwait(false);
        return (request, authorization);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal Uri? RequestUri { get; private set; }
        internal string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
