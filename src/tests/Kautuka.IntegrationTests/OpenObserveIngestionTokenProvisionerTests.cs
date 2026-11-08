using System.Net.Sockets;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class OpenObserveIngestionTokenProvisionerTests
{
    [Fact]
    public async Task ProvisionsNamedTokenUsingOnlyFixedUdsGetAndPostRoutes()
    {
        Assert.SkipUnless(Socket.OSSupportsUnixDomainSockets, "Requires Unix domain sockets.");
        string root = Path.Combine(Path.GetTempPath(), "kt-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string socketPath = Path.Combine(root, "connect.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(2);
            const string password = "manager-root-test-secret-42!";
            Task<(string[] Requests, string[] Authorizations)> server = RespondToTokenProvisioningAsync(listener, password);

            string exporterHeader = await OpenObserveIngestionTokenProvisioner.EnsureAsync(
                socketPath,
                password,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            (string[] requests, string[] authorizations) = await server.ConfigureAwait(true);
            Assert.Equal(new[]
            {
                "GET /api/default/ingestion-tokens HTTP/1.1",
                "POST /api/default/ingestion-tokens HTTP/1.1",
            }, requests);
            string rootHeader = OpenObserveCredentialStore.CreateBasicAuthorizationHeader(OpenObserveCredentialStore.RootUserEmail, password);
            Assert.All(authorizations, header => Assert.Equal(rootHeader, header));
            Assert.Equal(
                OpenObserveCredentialStore.CreateBasicAuthorizationHeader(OpenObserveCredentialStore.IngestionTokenName, "ingestion-only-token"),
                exporterHeader);
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(socketPath)) File.Delete(socketPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string[] Requests, string[] Authorizations)> RespondToTokenProvisioningAsync(Socket listener, string rootPassword)
    {
        var requests = new string[2];
        var authorizations = new string[2];
        string expectedRootHeader = OpenObserveCredentialStore.CreateBasicAuthorizationHeader(OpenObserveCredentialStore.RootUserEmail, rootPassword);
        for (int index = 0; index < requests.Length; index++)
        {
            using Socket accepted = await listener.AcceptAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var stream = new NetworkStream(accepted, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            requests[index] = await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false) ?? string.Empty;
            string? authorization = null;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(false)))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    authorization = line["Authorization:".Length..].Trim();
            }
            authorizations[index] = authorization ?? string.Empty;
            string body = index == 0 ? "{\"data\":[]}" : "{\"data\":{\"token\":\"ingestion-only-token\"}}";
            string response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: "
                + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        Assert.All(authorizations, header => Assert.Equal(expectedRootHeader, header));
        return (requests, authorizations);
    }
}
