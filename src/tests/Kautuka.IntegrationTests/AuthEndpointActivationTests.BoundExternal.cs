using System.Net;
using System.Text.Json;

using Kautuka.Gateway.Infrastructure.Connectors;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed partial class AuthEndpointActivationTests
{
    [Fact]
    public async Task BoundSocialLoginRequiresOriginalCookieAndTabAndCompletesOnlyOnce()
    {
        var identities = new ScriptedIdentityClient();
        using var handler = new BoundBrokerHandler();
        var configuration = new Dictionary<string, string?>
        {
            ["Kautuka:Auth:AttemptLimit"] = "1000",
            ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test",
            ["Kautuka:Auth:Google:ClientId"] = googleAudience,
        };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(configuration, identities, null, services =>
        {
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(),
                provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/auth/external/bound-start", new { provider = "google" }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal("no-store", started.Headers.CacheControl?.ToString());
        string cookie = started.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!;
        string tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        string nonce = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string setup = fixture.SetupToken;
        var correct = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage nullState = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state = (string?)null, tabHandle = tab, nonce }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.Unauthorized, nullState.StatusCode);
        using HttpResponseMessage wrongTab = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state, tabHandle = nonce, nonce }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongTab.StatusCode);
        using HttpResponseMessage wrongCookie = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state, tabHandle = tab, nonce }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCookie.StatusCode);
        using HttpResponseMessage wrongOrigin = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state, tabHandle = tab, nonce }, "https://attacker.example", null, correct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOrigin.StatusCode);
        using HttpResponseMessage early = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = setup }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);
        using HttpResponseMessage legacy = await PostExternalAsync(fixture, "/api/auth/external/complete", new { state, code = "stolen", brokerTicket = "x" }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.Unauthorized, legacy.StatusCode);
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state, tabHandle = tab, nonce }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        using HttpResponseMessage pending = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = setup }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        handler.IdentityToken = identities.Sign(GoogleClaims("owner@example.com", "owner-subject", handler.Nonce!, "Owner"));
        using HttpResponseMessage completed = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = setup }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        string first = await completed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using HttpResponseMessage repeated = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = setup }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using JsonDocument original = JsonDocument.Parse(first);
        using JsonDocument replay = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original.RootElement.GetProperty("accessToken").GetString(), replay.RootElement.GetProperty("accessToken").GetString());
        string initialRefresh = completed.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_refresh_token=", StringComparison.Ordinal)).Split(';')[0];
        using HttpResponseMessage refreshed = await PostExternalAsync(fixture, "/api/auth/refresh", new { }, applianceOrigin, null,
            new Dictionary<string, string> { ["Cookie"] = initialRefresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        string rotatedRefresh = refreshed.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_refresh_token=", StringComparison.Ordinal)).Split(';')[0];
        using HttpResponseMessage afterRotation = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = setup }, applianceOrigin, null,
            new Dictionary<string, string> { ["Cookie"] = cookie + "; " + rotatedRefresh });
        Assert.Equal(HttpStatusCode.OK, afterRotation.StatusCode);
        Assert.False(afterRotation.Headers.TryGetValues("Set-Cookie", out _));
        using HttpResponseMessage refreshAgain = await PostExternalAsync(fixture, "/api/auth/refresh", new { }, applianceOrigin, null,
            new Dictionary<string, string> { ["Cookie"] = rotatedRefresh });
        Assert.Equal(HttpStatusCode.OK, refreshAgain.StatusCode);
        using HttpResponseMessage changedSetup = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state, tabHandle = tab, setupToken = "different" }, applianceOrigin, null, correct);
        Assert.Equal(HttpStatusCode.Unauthorized, changedSetup.StatusCode);
        Assert.Equal(2, handler.ExchangeCalls);
    }

    [Fact]
    public async Task ConcurrentSocialStartsUseIndependentCookies()
    {
        using var handler = new BoundBrokerHandler();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test", ["Kautuka:Auth:Google:ClientId"] = googleAudience };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, new ScriptedIdentityClient(), null, services =>
        {
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        HttpResponseMessage[] starts = await Task.WhenAll(
            PostExternalAsync(fixture, "/api/auth/external/bound-start", new { provider = "google" }, applianceOrigin, null),
            PostExternalAsync(fixture, "/api/auth/external/bound-start", new { provider = "google" }, applianceOrigin, null));
        using HttpResponseMessage first = starts[0];
        using HttpResponseMessage second = starts[1];
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        string firstCookie = first.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        string secondCookie = second.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.NotEqual(firstCookie.Split('=')[0], secondCookie.Split('=')[0]);
        using JsonDocument one = JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using JsonDocument two = JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state1 = one.RootElement.GetProperty("state").GetString()!, tab1 = one.RootElement.GetProperty("tabHandle").GetString()!;
        string state2 = two.RootElement.GetProperty("state").GetString()!, tab2 = two.RootElement.GetProperty("tabHandle").GetString()!;
        var swapped = new Dictionary<string, string> { ["Cookie"] = firstCookie.Split('=')[0] + "=" + secondCookie.Split('=')[1] };
        using HttpResponseMessage rejected = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state = state1, tabHandle = tab1, nonce = new string('A', 43) }, applianceOrigin, null, swapped);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var both = new Dictionary<string, string> { ["Cookie"] = firstCookie + "; " + secondCookie };
        using HttpResponseMessage bound1 = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state = state1, tabHandle = tab1, nonce = new string('A', 43) }, applianceOrigin, null, both);
        using HttpResponseMessage bound2 = await PostExternalAsync(fixture, "/api/auth/external/bind-browser", new { state = state2, tabHandle = tab2, nonce = new string('A', 43) }, applianceOrigin, null, both);
        Assert.Equal(HttpStatusCode.NoContent, bound1.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, bound2.StatusCode);
        using HttpResponseMessage pending1 = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state = state1, tabHandle = tab1, setupToken = fixture.SetupToken }, applianceOrigin, null, both);
        using HttpResponseMessage pending2 = await PostExternalAsync(fixture, "/api/auth/external/bound-complete", new { state = state2, tabHandle = tab2, setupToken = fixture.SetupToken }, applianceOrigin, null, both);
        Assert.Equal(HttpStatusCode.Accepted, pending1.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, pending2.StatusCode);
    }

    [Fact]
    public async Task AdminCanRotateBrokerInstallationKeyFromLoopback()
    {
        using var handler = new BoundBrokerHandler();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        var store = new BoundMemoryIdentityStore();
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton<IBrokerInstallationIdentityStore>(store);
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
        }, IPAddress.Loopback);
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/internal/broker/rotate-installation-key", admin);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public void CustomBrokerDoesNotInheritHostedSocialClientIds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Kautuka:Auth:Google:ClientId"] = "hosted-google", ["Kautuka:Auth:Microsoft:ClientId"] = "hosted-microsoft" }).Build();
        var system = new GatewaySystemSettings { AuthenticationBrokerOrigin = "https://selfhost.example", AuthenticationBrokerGoogleClientId = "selfhost-google", AuthenticationBrokerMicrosoftClientId = "" };
        var providers = Kautuka.Gateway.Domain.Auth.ExternalLoginProviders.Create(configuration,
            Kautuka.Gateway.Domain.Connectors.ConnectorAuthEndpoint.Parse(system.AuthenticationBrokerOrigin), system);
        Assert.Equal("selfhost-google", providers.GoogleClientId);
        Assert.Null(providers.MicrosoftClientId);
        Assert.False(providers.Microsoft);
    }

    private sealed class BoundMemoryIdentityStore : IBrokerInstallationIdentityStore
    {
        private string? value;
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(value);
        public Task WriteAsync(string newValue, CancellationToken cancellationToken) { value = newValue; return Task.CompletedTask; }
    }

    private sealed class BoundBrokerHandler : HttpMessageHandler
    {
        public string? Nonce { get; private set; }
        public string? IdentityToken { get; set; }
        public int ExchangeCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/installation/flow")
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Nonce = body.RootElement.GetProperty("nonce").GetString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"flowId\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}") };
            }
            if (path == "/installation/social-exchange")
            {
                ExchangeCalls++;
                return IdentityToken is null ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id_token = IdentityToken })) };
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
