using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Manager.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class GatewayNetworkAccessEndpointTests
{
    [Fact]
    public async Task AuditUsesAuthenticatedOwnerAndBoundedPaging()
    {
        var manager = new RecordingManager();
        await using WebApplication app = await CreateAsync(manager, new Guard());
        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/network-access/audit?before=17&limit=25&userId=victim&isAdmin=true"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("owner", manager.Actor?.UserId);
        Assert.False(manager.Actor?.IsAdmin);
        Assert.Equal(17, manager.Audit?.Before);
        Assert.Equal(25, manager.Audit?.Limit);
    }

    [Theory]
    [InlineData("before=-1")]
    [InlineData("limit=101")]
    public async Task InvalidAuditPagingDoesNotCallManager(string query)
    {
        var manager = new RecordingManager();
        await using WebApplication app = await CreateAsync(manager, new Guard());
        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/network-access/audit?" + query), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(manager.Actor);
    }

    private static readonly JsonSerializerOptions json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadUsesAuthenticatedActorInsteadOfQueryParameters(bool admin)
    {
        var manager = new RecordingManager();
        await using WebApplication app = await CreateAsync(manager, new Guard(admin: admin));
        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/network-access?userId=victim&isAdmin=true"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("owner", manager.Actor?.UserId);
        Assert.Equal(admin, manager.Actor?.IsAdmin);
    }

    [Fact]
    public async Task AnonymousReadDoesNotCallManager()
    {
        var manager = new RecordingManager();
        await using WebApplication app = await CreateAsync(manager, new Guard(authenticated: false));
        using HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("http://localhost/api/network-access"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(manager.Actor);
    }

    [Theory]
    [InlineData("victim", "agent")]
    [InlineData("owner", "core")]
    [InlineData("system", "management")]
    public async Task OrdinaryUserCannotWriteOtherOwnerOrSystemPolicy(string owner, string scope)
    {
        var manager = new RecordingManager();
        await using WebApplication app = await CreateAsync(manager, new Guard());
        using HttpResponseMessage response = await app.GetTestClient().PutAsJsonAsync("/api/network-access/rules",
            new { identity = new { scopeType = scope, userId = owner, agentId = "agent", role = "connector", componentId = "mail" }, destination = "graph.microsoft.com", port = 443, allowed = true, expectedRevision = 7 }, json, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(manager.Actor);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(StatusCode.Aborted, HttpStatusCode.Conflict)]
    [InlineData(StatusCode.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task DecisionUsesSessionIdentityAndPreservesConflictOrHiddenOwnership(StatusCode rpcStatus, HttpStatusCode expected)
    {
        var manager = new RecordingManager { Error = rpcStatus };
        await using WebApplication app = await CreateAsync(manager, new Guard());
        using HttpResponseMessage response = await app.GetTestClient().PostAsJsonAsync("/api/network-access/requests/request-1/decision",
            new { allowed = true, expectedRevision = 7, actor = new { userId = "victim", isAdmin = true } }, json, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("owner", manager.Actor?.UserId);
        Assert.False(manager.Actor?.IsAdmin);
        Assert.Equal(7, manager.Decision?.ExpectedRevision);
        Assert.Equal("request-1", manager.Decision?.RequestId);
    }

    private static async Task<WebApplication> CreateAsync(RecordingManager manager, Guard guard)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IGatewayAuthGuard>(guard);
        builder.Services.AddSingleton<IManagerGrpcService>(manager);
        WebApplication app = builder.Build();
        app.MapGatewayNetworkAccessEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class Guard(bool authenticated = true, bool admin = false) : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = authenticated ? new GatewayUser("owner", "tester", "unused", admin ? "admin" : "user", true, null, null) : null;
            return authenticated;
        }
        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user) => TryAuthorize(context, out user) ? null : Results.Unauthorized();
        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user) => TryAuthorize(context, out user) && admin ? null : Results.StatusCode(403);
    }

    private sealed class RecordingManager : IManagerGrpcService
    {
        public NetworkAccessActorMessage? Actor { get; private set; }
        public NetworkAccessDecisionRequest? Decision { get; private set; }
        public NetworkAuditReadRequest? Audit { get; private set; }
        public StatusCode? Error { get; init; }
        public Task<NetworkAuditResponse> GetNetworkAuditAsync(NetworkAuditReadRequest request, CancellationToken cancellationToken = default)
        {
            Actor = request.Actor;
            Audit = request;
            return Task.FromResult(new NetworkAuditResponse());
        }
        private Task<NetworkAccessPolicyResponse> Result(NetworkAccessActorMessage actor)
        {
            Actor = actor;
            return Error is { } status ? Task.FromException<NetworkAccessPolicyResponse>(new RpcException(new Status(status, "test response")))
                : Task.FromResult(new NetworkAccessPolicyResponse { Enabled = true, ActivationState = "active" });
        }
        public Task<NetworkAccessPolicyResponse> GetNetworkAccessPolicyAsync(NetworkAccessReadRequest request, CancellationToken cancellationToken = default) => Result(request.Actor);
        public Task<NetworkAccessPolicyResponse> SetNetworkAccessRuleAsync(NetworkAccessRuleRequest request, CancellationToken cancellationToken = default) => Result(request.Actor);
        public Task<NetworkAccessPolicyResponse> DecideNetworkAccessRequestAsync(NetworkAccessDecisionRequest request, CancellationToken cancellationToken = default)
        {
            Decision = request;
            return Result(request.Actor);
        }
        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
