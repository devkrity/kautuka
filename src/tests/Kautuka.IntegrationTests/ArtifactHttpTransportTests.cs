using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class ArtifactHttpTransportTests
{
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
