using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Kautuka.IntegrationTests;

public sealed class WebHostSlackAppRedirectTests
{
    [Fact]
    public async Task CreateAppRequestIsForwardedToTheAuthenticatedGatewayEndpoint()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        await using WebApplication app = builder.Build();
        app.Map("/api/{**path}", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("gateway_unavailable", TestContext.Current.CancellationToken);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/settings/channels/slack/create-app", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("gateway_unavailable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
