using Grpc.Core;
using Grpc.Net.Client;

using Google.Protobuf;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos.EnvoyAuthorization;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using System.Net;
using System.Net.Sockets;

namespace Kautuka.IntegrationTests;

[CollectionDefinition("Envoy authorization host environment", DisableParallelization = true)]
public sealed class EnvoyAuthorizationHostSerialTests { }

[Collection("Envoy authorization host environment")]
public sealed class EnvoyAuthorizationServiceTests
{
    [Fact]
    public async Task CheckFailsClosedWhenGenerationHeaderIsMissing()
    {
        using var runtime = CreateRuntime();
        var service = new EnvoyAuthorizationService(runtime);

        CheckResponse response = await service.Check(ValidRequest(), new TestCallContext([]));

        Assert.Equal(7, response.Status.Code);
    }

    [Fact]
    public async Task CheckFailsClosedWhenGenerationHeaderIsDuplicated()
    {
        using var runtime = CreateRuntime();
        var service = new EnvoyAuthorizationService(runtime);

        CheckResponse response = await service.Check(ValidRequest(), new TestCallContext(
            [new Metadata.Entry("x-kautuka-nac-generation", "generation-a"), new Metadata.Entry("x-kautuka-nac-generation", "generation-a")]));

        Assert.Equal(7, response.Status.Code);
    }

    [Fact]
    public async Task CheckFailsClosedWhenPeerAddressIsMalformed()
    {
        using var runtime = CreateRuntime();
        var service = new EnvoyAuthorizationService(runtime);
        CheckRequest request = ValidRequest();
        request.Attributes.Source.Address.SocketAddress.Address = "not-an-ip";

        CheckResponse response = await service.Check(request, new TestCallContext(
            [new Metadata.Entry("x-kautuka-nac-generation", "generation-a")]));

        Assert.Equal(7, response.Status.Code);
    }

    [Fact]
    public async Task CheckFailsClosedWhenAuthorizationSnapshotIsUnavailable()
    {
        using var runtime = CreateRuntime();
        var service = new EnvoyAuthorizationService(runtime);

        CheckResponse response = await service.Check(ValidRequest(), new TestCallContext(
            [new Metadata.Entry("x-kautuka-nac-generation", "generation-a")]));

        Assert.Equal(7, response.Status.Code);
    }

    [Fact]
    public void EnvoyPeerServiceFieldUsesTheV3WireFieldNumber()
    {
        // Captured wire shape for envoy.service.auth.v3.Peer.service (field 2).
        Peer peer = Peer.Parser.ParseFrom([0x12, 0x03, (byte)'f', (byte)'o', (byte)'o']);

        Assert.Equal("foo", peer.Service);
        Assert.Equal([0x12, 0x03, (byte)'f', (byte)'o', (byte)'o'], peer.ToByteArray());
    }

    [Fact]
    public async Task HostedEndpointServesOnlyEnvoyCheckOverRestrictedHttp2UnixSocket()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Requires Linux Unix socket permission semantics.");
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "kautuka-envoy-auth-" + Guid.NewGuid().ToString("N"));
        int leakedPort = FindAvailablePort();
        const string endpointVariable = "ASPNETCORE_Kestrel__Endpoints__LeakedTcp__Url";
        string? previousEndpoint = Environment.GetEnvironmentVariable(endpointVariable);
        Environment.SetEnvironmentVariable(endpointVariable, $"http://127.0.0.1:{leakedPort}");
        using var runtime = new ContainerEgressRuntime(new PodmanCommandRunner(), new ContainerEgressOptions { Enabled = true },
            RuntimeImageAllowlist.Empty, root);
        await using var host = new EnvoyAuthorizationHost(runtime);
        string socketPath = runtime.AuthorizationSocketPath;
        try
        {
            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(socketPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
                File.GetUnixFileMode(socketPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(socketPath)!));
            using (var tcpProbe = new TcpClient())
                await Assert.ThrowsAnyAsync<SocketException>(async () =>
                    await tcpProbe.ConnectAsync(IPAddress.Loopback, leakedPort, TestContext.Current.CancellationToken));

            using var grpcHandler = CreateUnixSocketHandler(socketPath);
            using GrpcChannel channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = grpcHandler });
            var client = new Kautuka.Manager.Protos.EnvoyAuthorization.Authorization.AuthorizationClient(channel);
            var headers = new Metadata { { "x-kautuka-nac-generation", "generation-a" } };
            CheckResponse response = await client.CheckAsync(ValidRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(7, response.Status.Code); // Enabled runtime with no active snapshot denies.

            using var httpHandler = CreateUnixSocketHandler(socketPath);
            using var http = new HttpClient(httpHandler) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using HttpResponseMessage status = await http.GetAsync(new Uri("http://localhost/api/status"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable(endpointVariable, previousEndpoint);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static SocketsHttpHandler CreateUnixSocketHandler(string path) => new()
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static ContainerEgressRuntime CreateRuntime()
        => new(new PodmanCommandRunner(), new ContainerEgressOptions { Enabled = false }, RuntimeImageAllowlist.Empty, Path.GetTempPath());

    private static CheckRequest ValidRequest() => new()
    {
        Attributes = new AttributeContext
        {
            Source = new Peer { Address = new Address { SocketAddress = new Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress { Address = "10.0.0.2", PortValue = 40000 } } },
            Destination = new Peer { Address = new Address { SocketAddress = new Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress { Address = "93.184.216.34", PortValue = 443 } } },
            TlsSession = new TLSSession { Sni = "example.com" },
        },
    };

    private sealed class TestCallContext(Metadata headers) : ServerCallContext
    {
        private readonly Dictionary<object, object> userState = [];
        private Metadata responseTrailers = [];
        private Grpc.Core.Status status;
        private WriteOptions? writeOptions;
        protected override string MethodCore => "/envoy.service.auth.v3.Authorization/Check";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:12345";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override IDictionary<object, object> UserStateCore => userState;
        protected override Grpc.Core.Status StatusCore { get => status; set => status = value; }
        protected override WriteOptions? WriteOptionsCore { get => writeOptions; set => writeOptions = value; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
