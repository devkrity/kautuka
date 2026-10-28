using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Endpoints.Runtime;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class GatewayRuntimeUsageEndpointTests
{
    [Fact]
    public async Task UsageRunsEndpointRestrictsNonAdminRequestsToSelfScope()
    {
        var usageStore = new CapturingUsageQueryStore();
        var (app, _) = await BuildTestAppAsync(usageStore, isAdmin: false).ConfigureAwait(true);
        using var _ = app;

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("http://localhost/api/runtime/usage/runs?scope=all&userId=user-2&limit=25"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(usageStore.LastListRunsFilter);
        Assert.Equal("user-1", usageStore.LastListRunsFilter!.UserId);
        Assert.Equal(25, usageStore.LastListRunsFilter.Limit);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("self", json?["scope"]?.GetValue<string>());
    }

    [Fact]
    public async Task UsageSummaryEndpointDefaultsAdminsToAllScope()
    {
        var usageStore = new CapturingUsageQueryStore();
        var (app, _) = await BuildTestAppAsync(usageStore, isAdmin: true).ConfigureAwait(true);
        using var _ = app;

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("http://localhost/api/runtime/usage/summary?groupBy=user"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(usageStore.LastTotalsFilter);
        Assert.Null(usageStore.LastTotalsFilter!.UserId);
        Assert.Equal("user", usageStore.LastGroupBy);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("all", json?["scope"]?.GetValue<string>());
        Assert.Equal("user", json?["groupBy"]?.GetValue<string>());
    }

    [Fact]
    public async Task UsageSummaryEndpointRejectsInvalidDates()
    {
        var usageStore = new CapturingUsageQueryStore();
        var (app, _) = await BuildTestAppAsync(usageStore, isAdmin: true).ConfigureAwait(true);
        using var _ = app;

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("http://localhost/api/runtime/usage/summary?fromDate=2026-99-01"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UsageSummaryEndpointAcceptsTimestampRange()
    {
        var usageStore = new CapturingUsageQueryStore();
        var (app, _) = await BuildTestAppAsync(usageStore, isAdmin: true).ConfigureAwait(true);
        using var _ = app;

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("http://localhost/api/runtime/usage/summary?fromTime=2026-05-10T00:00:00Z&toTime=2026-05-10T12:00:00Z"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2026-05-10T00:00:00.0000000+00:00", usageStore.LastTotalsFilter?.FromTimestamp);
        Assert.Equal("2026-05-10T12:00:00.0000000+00:00", usageStore.LastTotalsFilter?.ToTimestamp);
    }

    private static async Task<(WebApplication App, CapturingUsageQueryStore Store)> BuildTestAppAsync(
        CapturingUsageQueryStore usageStore,
        bool isAdmin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IGatewayAuthGuard>(new AllowAllAuthGuard(isAdmin));
        builder.Services.AddSingleton<IGatewayUsageQueryStore>(usageStore);

        WebApplication app = builder.Build();
        app.MapGatewayRuntimeEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return (app, usageStore);
    }

    private sealed class AllowAllAuthGuard(bool isAdmin) : IGatewayAuthGuard
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

        private GatewayUser BuildUser() => new(
            Id: "user-1",
            Username: "tester",
            Password: "pwd",
            Role: isAdmin ? "admin" : "user",
            IsActive: true,
            Email: null,
                DisplayName: isAdmin ? "Admin User" : "Standard User");
    }

    private sealed class CapturingUsageQueryStore : IGatewayUsageQueryStore
    {
        public GatewayUsageQueryFilter? LastListRunsFilter { get; private set; }
        public GatewayUsageQueryFilter? LastTotalsFilter { get; private set; }
        public string? LastGroupBy { get; private set; }

        public IReadOnlyList<GatewayUsageRunListItem> ListRuns(GatewayUsageQueryFilter filter)
        {
            LastListRunsFilter = filter;
            return
            [
                new GatewayUsageRunListItem(
                    "run-1",
                    "session-1",
                    filter.UserId,
                    "tester",
                    "Test User",
                    "agent-a",
                    "Ops Agent",
                    "model-a",
                    "gpt-test",
                    "provider-a",
                    "Provider A",
                    "completed",
                    "chat",
                    DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),
                    DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1).ToString("O"),
                    DateTimeOffset.UtcNow.ToString("O"),
                    450,
                    120,
                    30,
                    70,
                    100,
                    1,
                    1,
                    1,
                    0,
                    "none",
                    null,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ];
        }

        public long CountRuns(GatewayUsageQueryFilter filter) => 1;

        public GatewayUsageTotalsPayload GetTotals(GatewayUsageQueryFilter filter)
        {
            LastTotalsFilter = filter;
            return new GatewayUsageTotalsPayload(4, 6, 4, 6, 2, 100, 200, 300, 9000, 150, null, null, null);
        }

        public IReadOnlyList<GatewayUsageSummaryBucket> GetBuckets(GatewayUsageQueryFilter filter, string groupBy, int limit)
        {
            LastGroupBy = groupBy;
            return
            [
                new GatewayUsageSummaryBucket(
                    "user-1",
                    "Admin User",
                    new GatewayUsageTotalsPayload(4, 6, 4, 6, 2, 100, 200, 300, 9000, 150, null, null, null))
            ];
        }
    }
}
