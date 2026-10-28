using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class GatewayRuntimeManagerContainersTests
{
    [Fact]
    public async Task ManagerContainersReturnsManagerContainerPayload()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        AddAdminAuthentication(builder.Services);
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(new ManagerRuntimeContainersResponse
        {
            Ok = true,
            Message = "2 manager container(s) discovered.",
            Containers =
            {
                new ManagerRuntimeContainerMessage { Name = "kautuka-loki", Image = "docker.io/grafana/loki:latest", Status = "Up 10 minutes", Role = "sidecar" },
                new ManagerRuntimeContainerMessage { Name = "kautuka-agent-demo", Image = "localhost/kautuka-base:latest", Status = "Up 2 minutes", Role = "managed" },
            }
        }));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/diagnostics/manager/containers"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(json?["ok"]?.GetValue<bool>());
        Assert.Equal("2 manager container(s) discovered.", json?["message"]?.GetValue<string>());
        Assert.Equal("kautuka-loki", json?["containers"]?[0]?["name"]?.GetValue<string>());
        Assert.Equal("sidecar", json?["containers"]?[0]?["role"]?.GetValue<string>());
        Assert.Equal("localhost/kautuka-base:latest", json?["containers"]?[1]?["image"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManagerContainersReturnsUnavailablePayloadWhenManagerUnavailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        AddAdminAuthentication(builder.Services);
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(throwUnavailable: true));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/diagnostics/manager/containers"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(json?["ok"]?.GetValue<bool>());
        Assert.Equal("Manager runtime diagnostics unavailable.", json?["message"]?.GetValue<string>());
        Assert.Equal(0, json?["containers"]?.AsArray().Count ?? -1);
    }

    [Fact]
    public async Task PkgsProxyCacheStatsReturnsCliPayload()
    {
        var stats = new ManagerPkgsProxyCacheStatsResponse
        {
            Ok = true,
            Message = "Package proxy cache stats.",
            Packages = 86,
            Versions = 87,
            Artifacts = 165,
            TotalSizeBytes = 109554665,
            TotalHits = 1012,
        };
        stats.Ecosystems["npm"] = 1;
        stats.Ecosystems["pypi"] = 85;
        stats.Popular.Add(new ManagerPkgsProxyPopularPackageMessage
        {
            Ecosystem = "pypi",
            Name = "uv",
            Hits = 30,
            SizeBytes = 24884515,
        });
        stats.Recent.Add(new ManagerPkgsProxyRecentPackageMessage
        {
            Ecosystem = "npm",
            Name = "ms",
            Version = "2.1.3",
            CachedAt = "2026-09-18 13:36:26",
            SizeBytes = 2967,
        });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        AddAdminAuthentication(builder.Services);
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(statsResponse: stats));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/runtime/diagnostics/pkgs-proxy/stats"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(json?["ok"]?.GetValue<bool>());
        Assert.Equal(86, json?["packages"]?.GetValue<long>());
        Assert.Equal(165, json?["artifacts"]?.GetValue<long>());
        Assert.Equal(109554665, json?["totalSizeBytes"]?.GetValue<long>());
        Assert.Equal(1012, json?["totalHits"]?.GetValue<long>());
        Assert.Equal(1, json?["ecosystems"]?["npm"]?.GetValue<long>());
        Assert.Equal(85, json?["ecosystems"]?["pypi"]?.GetValue<long>());
        Assert.Equal("uv", json?["popular"]?[0]?["name"]?.GetValue<string>());
        Assert.Equal(30, json?["popular"]?[0]?["hits"]?.GetValue<long>());
        Assert.Equal("ms", json?["recent"]?[0]?["name"]?.GetValue<string>());
        Assert.Equal("2.1.3", json?["recent"]?[0]?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task PkgsProxyCacheStatsReturnsUnavailablePayloadWhenManagerUnavailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        AddAdminAuthentication(builder.Services);
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(throwUnavailable: true));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/runtime/diagnostics/pkgs-proxy/stats"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(json?["ok"]?.GetValue<bool>());
        Assert.Equal("Manager package proxy cache diagnostics unavailable.", json?["message"]?.GetValue<string>());
        Assert.Equal(0, json?["packages"]?.GetValue<long>());
        Assert.Equal(0, json?["popular"]?.AsArray().Count ?? -1);
        Assert.Equal(0, json?["recent"]?.AsArray().Count ?? -1);
    }

    private static void AddAdminAuthentication(IServiceCollection services)
    {
        services.AddAuthentication("TestAdmin")
            .AddScheme<AuthenticationSchemeOptions, TestAdminAuthenticationHandler>("TestAdmin", _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy("AdminDiagnostics", policy => policy.RequireRole("admin"));
    }

    private sealed class TestAdminAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            ClaimsPrincipal principal = new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-admin"), new Claim(ClaimTypes.Role, "admin")],
                Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class FakeManagerGrpcService : IManagerGrpcService
    {
        private readonly ManagerRuntimeContainersResponse? containersResponse;
        private readonly ManagerPkgsProxyCacheStatsResponse? statsResponse;
        private readonly bool throwUnavailable;

        public FakeManagerGrpcService(
            ManagerRuntimeContainersResponse? containersResponse = null,
            ManagerPkgsProxyCacheStatsResponse? statsResponse = null,
            bool throwUnavailable = false)
        {
            this.containersResponse = containersResponse;
            this.statsResponse = statsResponse;
            this.throwUnavailable = throwUnavailable;
        }

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = request.Message });

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerStatusResponse { Ok = true, CoreReady = true, AgentsPending = false, AgentsReady = true, Lifecycle = "agents_ready" });

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        {
            if (throwUnavailable)
            {
                throw new InvalidOperationException("Manager unavailable");
            }

            return Task.FromResult(containersResponse ?? new ManagerRuntimeContainersResponse { Ok = true, Message = "No manager containers found." });
        }

        public Task<ManagerPkgsProxyCacheStatsResponse> GetPkgsProxyCacheStatsAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        {
            if (throwUnavailable)
            {
                throw new InvalidOperationException("Manager unavailable");
            }

            return Task.FromResult(statsResponse ?? new ManagerPkgsProxyCacheStatsResponse { Ok = true, Message = "Package proxy cache stats." });
        }

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeChannelHostGrpcService : IChannelHostGrpcService
    {
        public ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChannelHostPingResponse
            {
                Providers =
                {
                    new ChannelProviderHealth { ProviderKey = "telegram", DisplayName = "Telegram", Status = "not_configured", Detail = "test" }
                }
            });

        public ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SyncOwnerTargetsResponse { Accepted = true, Detail = "noop" });

        public ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AcknowledgeIngressResponse { Acknowledged = true });

        public ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SetRuntimeCredentialsResponse { Accepted = true });

        public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }
}
