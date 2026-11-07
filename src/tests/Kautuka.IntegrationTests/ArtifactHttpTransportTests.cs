using System.Net;
using System.Net.Sockets;

using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class ArtifactHttpTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MissingProxyCannotFallBackToAReachableLoopbackDestination(int selection)
    {
        string socket = Path.Combine(Path.GetTempPath(), "ka-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kautuka:Mode"] = "prod",
            ["ContainerEgress:ArtifactSocketPath"] = selection == 1 ? socket : null,
            ["ContainerEgress:Enabled"] = selection == 2 ? "true" : "false",
            ["Kautuka:ContainerStateRoot"] = Path.Combine(Path.GetTempPath(), "ka-" + Guid.NewGuid().ToString("N")[..8]),
        }).Build();
        using SocketsHttpHandler handler = selection == 0 ? KautukaArtifactHttpTransport.Create(socket) : KautukaArtifactHttpTransport.CreateForConfiguration(configuration);
        Assert.True(handler.UseProxy);
        Assert.False(handler.Proxy!.IsBypassed(new Uri("https://127.0.0.1:" + port)));
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(new Uri("https://127.0.0.1:" + port), TestContext.Current.CancellationToken));
        Assert.False(listener.Pending());
    }
}
