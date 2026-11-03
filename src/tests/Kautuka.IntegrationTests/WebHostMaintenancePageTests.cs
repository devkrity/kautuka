using Kautuka.WebHost;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Kautuka.IntegrationTests;

public sealed class WebHostMaintenancePageTests
{
    [Fact]
    public async Task BrowserGetsStaticPageWhileGatewayRequestsAreFenced()
    {
        bool maintenance = true;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using WebApplication app = builder.Build();
        app.UseWebHostMaintenancePage(() => maintenance);
        app.Run(context => context.Response.WriteAsync("running", TestContext.Current.CancellationToken));
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage page = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (int)page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Kautuka is updating", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        using HttpResponseMessage api = await client.PostAsync(new Uri("/api/action", UriKind.Relative), null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (int)api.StatusCode);
        Assert.Equal(string.Empty, await api.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status200OK, (int)health.StatusCode);
        maintenance = false;
        using HttpResponseMessage resumed = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("running", await resumed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }
}
