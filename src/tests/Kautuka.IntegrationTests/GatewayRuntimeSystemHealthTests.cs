using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class GatewayRuntimeSystemHealthTests
{
    [Fact]
    public async Task SystemHealthReturnsCoreNotReadyWhenManagerUnavailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(throwUnavailable: true));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/system-health"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("core_not_ready", json?["lifecycle"]?["status"]?.GetValue<string>());
        Assert.False(json?["lifecycle"]?["coreReady"]?.GetValue<bool>());
        Assert.Equal("initializeAgentRuntime", json?["actionHints"]?[0]?.GetValue<string>());
    }

    [Fact]
    public async Task SystemHealthReturnsAgentsPendingWhenManagerReadyButNoAgents()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = true,
            AgentsPending = true,
            AgentsReady = false,
            Lifecycle = "agents_pending",
            Message = "setup needed"
        }));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/system-health"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("agents_pending", json?["lifecycle"]?["status"]?.GetValue<string>());
        Assert.True(json?["lifecycle"]?["coreReady"]?.GetValue<bool>());
        Assert.True(json?["lifecycle"]?["agentsPending"]?.GetValue<bool>());
        Assert.Equal("applyAgentDsc", json?["actionHints"]?[0]?.GetValue<string>());
        Assert.Equal("openAgentSetup", json?["actionHints"]?[1]?.GetValue<string>());
    }

    [Fact]
    public async Task SystemHealthReturnsAgentsReadyWhenManagerAndAgentPlanesReady()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = true,
            AgentsPending = false,
            AgentsReady = true,
            Lifecycle = "agents_ready",
            Message = "ready"
        }));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/system-health"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("agents_ready", json?["lifecycle"]?["status"]?.GetValue<string>());
        Assert.True(json?["lifecycle"]?["agentsReady"]?.GetValue<bool>());
        Assert.Equal(0, json?["actionHints"]?.AsArray().Count ?? -1);
    }

    [Fact]
    public async Task AnonymousSystemHealthOmitsContainerDriftNotes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IManagerGrpcService>(new FakeManagerGrpcService(new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = true,
            AgentsPending = false,
            AgentsReady = true,
            Lifecycle = "agents_ready",
            Message = "ready",
            DriftActions = { "Connector user-other::mail was replaced because its environment does not match the spec." },
        }));
        builder.Services.AddSingleton<IChannelHostGrpcService>(new FakeChannelHostGrpcService());

        var app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var _ = app;

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/runtime/system-health"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain("user-other", body, StringComparison.Ordinal);
        JsonNode? drift = JsonNode.Parse(body)?["driftActions"];
        Assert.True(drift is null || drift.AsArray().Count == 0);
    }

    private sealed class FakeManagerGrpcService : IManagerGrpcService
    {
        private readonly ManagerStatusResponse? status;
        private readonly bool throwUnavailable;

        public FakeManagerGrpcService(ManagerStatusResponse? status = null, bool throwUnavailable = false)
        {
            this.status = status;
            this.throwUnavailable = throwUnavailable;
        }

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = request.Message });

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        {
            if (throwUnavailable)
            {
                throw new InvalidOperationException("Manager unavailable");
            }

            return Task.FromResult(status ?? new ManagerStatusResponse { Ok = false, CoreReady = false, AgentsPending = false, AgentsReady = false, Lifecycle = "core_not_ready", Message = "unavailable" });
        }

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true, Message = "No manager containers found." });

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
