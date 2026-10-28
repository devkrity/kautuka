using System.Net;

using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed partial class AuthEndpointActivationTests
{
    [Fact]
    public async Task ManagementRoutesRequireAdminEvenOnLoopback()
    {
        var publisher = new ManagementPublisher();
        await using var fixture = await AuthApiFixture.CreateAsync(null, null, null,
            services => services.AddSingleton<IWorkspaceDesiredStatePublisher>(publisher), IPAddress.Loopback);
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        fixture.CreateUser("management-member", "Testing1", isActive: true);
        string member = await fixture.LoginAsAsync("management-member", "Testing1");
        int initialPublications = publisher.Publications;

        foreach (string path in new[] { "/api/internal/shutdown", "/api/internal/runtime/desired-state/rehydrate" })
        {
            using HttpResponseMessage anonymous = await fixture.SendAnonymousAsync(HttpMethod.Post, path, null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using HttpResponseMessage ordinary = await fixture.SendAuthenticatedAsync(HttpMethod.Post, path, member);
            Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
            Assert.False(fixture.IsStopping);
            Assert.Equal(initialPublications, publisher.Publications);
        }

        using HttpResponseMessage rehydrate = await fixture.SendAuthenticatedAsync(HttpMethod.Post,
            "/api/internal/runtime/desired-state/rehydrate", admin);
        Assert.Equal(HttpStatusCode.Accepted, rehydrate.StatusCode);
        Assert.Equal(initialPublications + 1, publisher.Publications);
        Assert.False(fixture.IsStopping);

        using HttpResponseMessage shutdown = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/internal/shutdown", admin);
        Assert.Equal(HttpStatusCode.Accepted, shutdown.StatusCode);
        Assert.True(fixture.IsStopping);
    }

    [Fact]
    public async Task ManagementRoutesStillRejectRemoteAdmin()
    {
        var publisher = new ManagementPublisher();
        await using var fixture = await AuthApiFixture.CreateAsync(null, null, null,
            services => services.AddSingleton<IWorkspaceDesiredStatePublisher>(publisher), IPAddress.Parse("192.0.2.4"));
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        int initialPublications = publisher.Publications;
        foreach (string path in new[] { "/api/internal/shutdown", "/api/internal/runtime/desired-state/rehydrate" })
        {
            using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(HttpMethod.Post, path, admin);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.False(fixture.IsStopping);
        Assert.Equal(initialPublications, publisher.Publications);
    }

    private sealed class ManagementPublisher : IWorkspaceDesiredStatePublisher
    {
        public int Publications { get; private set; }

        public DesiredStatePublishResult TryPublishCurrent(string reason)
        {
            Publications++;
            return DesiredStatePublishResult.Succeeded;
        }
    }
}
