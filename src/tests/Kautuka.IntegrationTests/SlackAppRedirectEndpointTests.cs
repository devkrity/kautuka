using System.Net;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Endpoints.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class SlackAppRedirectEndpointTests
{
    [Fact]
    public async Task CreateAppUrlRequiresAuthenticationAndUsesOwnerNameInManifest()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGatewayAuthGuard>(new TestAuthGuard());

        await using WebApplication app = builder.Build();
        app.MapGatewaySettingsEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage anonymousResponse = await client.GetAsync(
            new Uri("/api/settings/channels/slack/create-app", UriKind.Relative),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        client.DefaultRequestHeaders.Add("X-Test-User", "owner");
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/settings/channels/slack/create-app", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string appUrl = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("https://api.slack.com/apps?new_app=1&manifest_json=", appUrl, StringComparison.Ordinal);
        Assert.Contains("Kautuka (Owner Name)", Uri.UnescapeDataString(appUrl), StringComparison.Ordinal);
    }

    private sealed class TestAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            bool authorized = context.Request.Headers.ContainsKey("X-Test-User");
            user = authorized ? new GatewayUser("user-1", "owner", "", "admin", true, null, "Owner Name") : null;
            return authorized;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = null;
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = null;
            return null;
        }
    }
}
