using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.UpdateService.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using UpdateServiceStatusResponse = Kautuka.UpdateService.Protos.UpdateStatusResponse;

namespace Kautuka.IntegrationTests;

public sealed class GatewayRuntimeUpdateEndpointTests
{
    [Fact]
    public async Task UpdateStatusEndpointReturnsManagerPayload()
    {
        var updateService = new RecordingUpdateServiceGrpcService();
        await using WebApplication app = await CreateAppAsync(updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/runtime/update-status"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("Current", json?["state"]?.GetValue<string>());
        Assert.Equal("stable", json?["effectiveReleaseChannel"]?.GetValue<string>());
        Assert.Equal("Blocked", json?["eligibilityState"]?.GetValue<string>());
        Assert.Equal("NewerStableRequired", json?["eligibilityReason"]?.GetValue<string>());
        Assert.Equal("1.2.3", json?["currentRelease"]?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task RefreshUpdateEndpointInvokesDiscoveryRefresh()
    {
        var updateService = new RecordingUpdateServiceGrpcService();
        await using WebApplication app = await CreateAppAsync(updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("http://localhost/api/runtime/update-status/refresh"),
            content: null,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, updateService.RefreshCalls);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("Available", json?["state"]?.GetValue<string>());
    }

    [Fact]
    public async Task RevertUpdateEndpointInvokesRollbackAction()
    {
        var updateService = new RecordingUpdateServiceGrpcService();
        await using WebApplication app = await CreateAppAsync(updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("http://localhost/api/runtime/update-status/revert"),
            content: null,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, updateService.RevertCalls);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("Applying", json?["state"]?.GetValue<string>());
        Assert.Equal("1.2.2", json?["lastKnownGoodRelease"]?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyUpdateEndpointPreservesOperationFailureDetails()
    {
        var updateService = new RecordingUpdateServiceGrpcService
        {
            ApplyException = new InvalidOperationException("No prepared update is available.")
        };

        await using WebApplication app = await CreateAppAsync(updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("http://localhost/api/runtime/update-status/apply"),
            content: null,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(json?["ok"]?.GetValue<bool>() ?? true);
        Assert.Equal("update-apply-failed", json?["message"]?.GetValue<string>());
        Assert.Equal("Current", json?["state"]?.GetValue<string>());
        Assert.Equal("No prepared update is available.", json?["lastError"]?.GetValue<string>());
        Assert.Equal("1.2.3", json?["currentRelease"]?["version"]?.GetValue<string>());
    }

    [Fact]
    public async Task UpdateStatusEndpointPreservesUnavailableDetails()
    {
        var updateService = new RecordingUpdateServiceGrpcService
        {
            GetStatusException = new RpcException(new Status(StatusCode.Unavailable, "mTLS handshake failed"))
        };

        await using WebApplication app = await CreateAppAsync(updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/runtime/update-status"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(json?["ok"]?.GetValue<bool>() ?? true);
        Assert.Equal("update-status-unavailable", json?["message"]?.GetValue<string>());
        Assert.Equal("Unavailable", json?["state"]?.GetValue<string>());
        Assert.Equal("Unavailable: mTLS handshake failed", json?["lastError"]?.GetValue<string>());
    }

    private static async Task<WebApplication> CreateAppAsync(RecordingUpdateServiceGrpcService updateService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IGatewayAuthGuard, AllowAllAuthGuard>();
        builder.Services.AddSingleton<IUpdateServiceGrpcService>(updateService);

        WebApplication app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return app;
    }

    private sealed class AllowAllAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        private static GatewayUser BuildUser() => new(
            Id: "user-1",
            Username: "tester",
            Password: "pwd",
            Role: "admin",
            IsActive: true,
            Email: null,
                DisplayName: null);
    }

    private sealed class RecordingUpdateServiceGrpcService : IUpdateServiceGrpcService
    {
        public Exception? GetStatusException { get; init; }
        public Exception? ApplyException { get; init; }
        public int RefreshCalls { get; private set; }
        public int RevertCalls { get; private set; }

        public Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateServiceBoolResponse { Ok = true, Message = request.Message });

        public Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CatalogSnapshotResponse { Ok = true, Message = request.Message });

        public Task<UpdateServiceStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => GetStatusException is null
                ? Task.FromResult(CreateStatus("Current"))
                : Task.FromException<UpdateServiceStatusResponse>(GetStatusException);

        public Task<UpdateServiceStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(CreateStatus("Available"));
        }

        public Task<UpdateServiceStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateStatus("Current"));

        public Task<UpdateServiceStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => ApplyException is null
                ? Task.FromResult(CreateStatus("Applying"))
                : Task.FromException<UpdateServiceStatusResponse>(ApplyException);

        public Task<UpdateServiceStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        {
            RevertCalls++;
            return Task.FromResult(CreateStatus("Applying"));
        }

        private static UpdateServiceStatusResponse CreateStatus(string state)
            => new()
            {
                Ok = true,
                Message = $"runtime-{state.ToUpperInvariant()}",
                WorkflowState = state,
                EffectiveReleaseChannel = "stable",
                EligibilityState = "Blocked",
                EligibilityReason = "NewerStableRequired",
                ManagerVersion = "1.2.3",
                HostReady = true,
                ActiveExecCount = 0,
                CurrentRelease = new UpdateReleaseReferenceMessage
                {
                    ReleaseId = "release-current",
                    Version = "1.2.3",
                    Channel = "stable",
                    ReleaseFingerprint = "fp-current",
                    Tag = "v1.2.3"
                },
                LastKnownGoodRelease = new UpdateReleaseReferenceMessage
                {
                    ReleaseId = "release-previous",
                    Version = "1.2.2",
                    Channel = "stable",
                    ReleaseFingerprint = "fp-previous",
                    Tag = "v1.2.2"
                }
            };
    }
}
