using System.Net;
using System.Net.Sockets;
using System.Text;

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
}
