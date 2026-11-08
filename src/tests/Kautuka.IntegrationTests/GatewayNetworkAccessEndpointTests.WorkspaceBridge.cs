using System.Text.Json;
using System.Reflection;
using System.Net.Http.Json;

using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos.EnvoyAuthorization;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Kautuka.IntegrationTests;

public sealed partial class GatewayNetworkAccessEndpointTests
{
    [Fact]
    public async Task ExtAuthzPendingRequestFlowsThroughGatewayHttpWorkspaceRpcAndBackToTheSameStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-gateway-network-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("The policy read and decision must not bootstrap Podman."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var identity = new NetworkAccessIdentity("agent", "owner", "agent-1", "connector", "owner::mail");
            typeof(ContainerEgressRuntime).GetField("authorization", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(runtime, new EgressAuthorizationSnapshot("generation-1",
                    new Dictionary<string, NetworkAccessIdentity> { ["10.247.91.2"] = identity }, []));
            var workspace = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
                new StubRuntimeHealthProbe(), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance, runtime);
            var manager = new RecordingManager(workspace);

            var envoy = new EnvoyAuthorizationService(runtime);
            CheckResponse denied = await envoy.Check(new CheckRequest
            {
                Attributes = new AttributeContext
                {
                    Source = new Peer
                    {
                        Address = new Address
                        {
                            SocketAddress = new Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress
                            { Address = "10.247.91.2", PortValue = 40444 }
                        }
                    },
                    Destination = new Peer
                    {
                        Address = new Address
                        {
                            SocketAddress = new Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress
                            { Address = "93.184.216.34", PortValue = 443 }
                        }
                    },
                    TlsSession = new TLSSession { Sni = "unknown.example.com" },
                },
            }, new AuthorizationCallContext(new Metadata { { "x-kautuka-nac-generation", "generation-1" } }));
            Assert.Equal(7, denied.Status.Code);

            await using WebApplication ownerApp = await CreateAsync(manager, new Guard());
            using HttpClient owner = ownerApp.GetTestClient();
            JsonDocument pending = await ReadJsonAsync(owner, "/api/network-access");
            JsonElement request = Assert.Single(pending.RootElement.GetProperty("requests").EnumerateArray());
            string requestId = request.GetProperty("id").GetString()!;
            Assert.Equal("unknown.example.com", request.GetProperty("destination").GetString());
            Assert.Equal("pending", request.GetProperty("status").GetString());
            Assert.Equal(1, pending.RootElement.GetProperty("pendingRequestCount").GetInt32());
            long revision = pending.RootElement.GetProperty("revision").GetInt64();

            await using (WebApplication otherApp = await CreateAsync(manager, new Guard(userId: "other")))
            using (HttpClient other = otherApp.GetTestClient())
            using (JsonDocument isolated = await ReadJsonAsync(other, "/api/network-access"))
                Assert.Empty(isolated.RootElement.GetProperty("requests").EnumerateArray());

            using HttpResponseMessage decision = await owner.PostAsJsonAsync(new Uri("/api/network-access/requests/" + requestId + "/decision", UriKind.Relative),
                new { allowed = true, expectedRevision = revision }, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, decision.StatusCode);
            using JsonDocument after = await ReadJsonAsync(owner, "/api/network-access");
            JsonElement resolved = Assert.Single(after.RootElement.GetProperty("requests").EnumerateArray());
            Assert.Equal("allowed", resolved.GetProperty("status").GetString());
            Assert.Contains(after.RootElement.GetProperty("rules").EnumerateArray(), rule =>
                rule.GetProperty("destination").GetString() == "unknown.example.com" && rule.GetProperty("allowed").GetBoolean());
            Assert.Equal(1, after.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(requestId, manager.Decision?.RequestId);
            Assert.Equal("owner", manager.Decision?.Actor?.UserId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class StubRuntimeHealthProbe : IManagerRuntimeHealthProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class AuthorizationCallContext(Metadata headers) : ServerCallContext
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
        protected override CancellationToken CancellationTokenCore => TestContext.Current.CancellationToken;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override IDictionary<object, object> UserStateCore => userState;
        protected override Grpc.Core.Status StatusCore { get => status; set => status = value; }
        protected override WriteOptions? WriteOptionsCore { get => writeOptions; set => writeOptions = value; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }

    private sealed class ManagerCallContext : ServerCallContext
    {
        public static readonly ManagerCallContext Instance = new();
        private readonly Dictionary<object, object> userState = [];
        private Metadata responseTrailers = [];
        private Grpc.Core.Status status;
        private WriteOptions? writeOptions;
        protected override string MethodCore => "/kautuka.manager.v1.ManagerService/NetworkAccess";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:12345";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => TestContext.Current.CancellationToken;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override IDictionary<object, object> UserStateCore => userState;
        protected override Grpc.Core.Status StatusCore { get => status; set => status = value; }
        protected override WriteOptions? WriteOptionsCore { get => writeOptions; set => writeOptions = value; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
