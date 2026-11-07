using System.Net;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed partial class AuthEndpointActivationTests
{
    [Fact]
    public async Task BoundMailEndpointsRequireOriginalBrowserAndCompleteOnlyOnce()
    {
        using var handler = new BoundMailBrokerHandler();
        var obsolete = new GoogleMailConnectorTests.MemoryTokens { FailDeleteOnceForKey = "KAUTUKA_GOOGLE_MAIL_DPOP_KEY" };
        obsolete.Values["KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"] = "old-refresh";
        obsolete.Values["KAUTUKA_GOOGLE_MAIL_DPOP_KEY"] = "old-key";
        obsolete.Values["KAUTUKA_GOOGLE_MAIL_DPOP_NONCE"] = "old-nonce";
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton(new ConnectorAuthCatalog([GoogleMailConnectorTests.BrokerProfile()]));
            services.AddSingleton<IConnectorRefreshTokenStore>(obsolete);
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(new BoundMailMemoryStorage());
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(),
                provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        fixture.AddMailConnector("kautuka-google-mail");
        string bearer = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/connectors/kautuka-google-mail/oauth/bound-begin", new { }, applianceOrigin, bearer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        string cookie = started.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_bound_mail_", StringComparison.Ordinal)).Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!, tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        string nonce = new('A', 43), wrong = new('B', 43);
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage early = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);
        using HttpResponseMessage wrongTab = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = wrong, nonce }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongTab.StatusCode);
        using HttpResponseMessage wrongOrigin = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce }, "https://attacker.example", bearer, headers);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOrigin.StatusCode);
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        using HttpResponseMessage cleanupFailed = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, cleanupFailed.StatusCode);
        Assert.False(obsolete.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN"));
        Assert.True(obsolete.Values.ContainsKey("KAUTUKA_GOOGLE_MAIL_DPOP_KEY"));
        using HttpResponseMessage complete = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Empty(obsolete.Values);
        using HttpResponseMessage replay = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(1, handler.ExchangeCount);
        Assert.Equal(1, handler.Acknowledgements);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RevocationDuringOldCredentialCleanupCompensatesActivatedFlow(bool cleanupFails, bool cancelFlow)
    {
        using var handler = new BoundMailBrokerHandler();
        var oldCredentials = new GoogleMailConnectorTests.MemoryTokens { BlockDeleteForKey = "KAUTUKA_GOOGLE_MAIL_DPOP_KEY" };
        if (cleanupFails) oldCredentials.FailDeleteOnceForKey = "KAUTUKA_GOOGLE_MAIL_DPOP_KEY";
        var storage = new BoundMailMemoryStorage();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton(new ConnectorAuthCatalog([GoogleMailConnectorTests.BrokerProfile()]));
            services.AddSingleton<IConnectorRefreshTokenStore>(oldCredentials);
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(storage);
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        fixture.AddMailConnector("kautuka-google-mail");
        string bearer = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/connectors/kautuka-google-mail/oauth/bound-begin", new { }, applianceOrigin, bearer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        string cookie = started.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_bound_mail_", StringComparison.Ordinal)).Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!, tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce = new string('A', 43) }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        Task<HttpResponseMessage> completion = PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        await oldCredentials.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (cancelFlow)
        {
            using HttpResponseMessage canceled = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-cancel", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
            Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
        }
        else fixture.RevokeSessions(fixture.GetUserId("admin"));
        oldCredentials.ContinueDelete.TrySetResult();
        using HttpResponseMessage result = await completion;
        Assert.Equal(cleanupFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(1, handler.Disconnects);
        using var stateStore = new BrokerMailCredentialStore(storage);
        Assert.Null((await stateStore.ReadAsync(fixture.GetUserId("admin"), "kautuka-google-mail", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task RevokedUserCannotActivateMailAfterBlockedBrokerExchange()
    {
        using var handler = new BoundMailBrokerHandler { BlockExchange = true };
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton(new ConnectorAuthCatalog([GoogleMailConnectorTests.BrokerProfile()]));
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(new BoundMailMemoryStorage());
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(),
                provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        fixture.AddMailConnector("kautuka-google-mail");
        string bearer = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/connectors/kautuka-google-mail/oauth/bound-begin", new { }, applianceOrigin, bearer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        string cookie = started.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_bound_mail_", StringComparison.Ordinal)).Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!, tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce = new string('A', 43) }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        Task<HttpResponseMessage> completion = PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        await handler.ExchangeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        fixture.RevokeSessions(fixture.GetUserId("admin"));
        handler.ContinueExchange.TrySetResult();
        using HttpResponseMessage result = await completion;
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, handler.Acknowledgements);
    }

    [Fact]
    public async Task CancellingDuringAcknowledgementRevokesTheNewConnection()
    {
        using var handler = new BoundMailBrokerHandler { BlockAcknowledgement = true };
        var sessions = new BoundMailSessionStore();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton(new ConnectorAuthCatalog([GoogleMailConnectorTests.BrokerProfile()]));
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(new BoundMailMemoryStorage());
            services.AddSingleton(sessions);
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(),
                provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        fixture.AddMailConnector("kautuka-google-mail");
        string bearer = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/connectors/kautuka-google-mail/oauth/bound-begin", new { }, applianceOrigin, bearer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        string cookie = started.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_bound_mail_", StringComparison.Ordinal)).Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!, tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce = new string('A', 43) }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        Task<HttpResponseMessage> completion = PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        await handler.AcknowledgementStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task<HttpResponseMessage> cancellation = PostExternalAsync(fixture, "/api/connectors/oauth/bound-cancel", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        string userId = fixture.GetUserId("admin");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (sessions.TryGet(state, userId, applianceOrigin, cookie.Split('=')[1], tab, DateTimeOffset.UtcNow, out _)
            && DateTimeOffset.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(sessions.TryGet(state, userId, applianceOrigin, cookie.Split('=')[1], tab, DateTimeOffset.UtcNow, out _));
        handler.ContinueAcknowledgement.TrySetResult();
        using HttpResponseMessage completeResult = await completion;
        using HttpResponseMessage cancelResult = await cancellation;
        Assert.Equal(HttpStatusCode.Unauthorized, completeResult.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelResult.StatusCode);
        Assert.Equal(1, handler.Disconnects);
        Assert.True(handler.AcknowledgementCommittedBeforeDisconnect);
    }

    [Theory]
    [InlineData("new_account_limit", 429)]
    [InlineData("active_account_limit", 409)]
    [InlineData("insufficient_scope", 403)]
    public async Task BoundMailCompletionForwardsAdmissionDenial(string denial, int status)
    {
        using var handler = new BoundMailBrokerHandler { AdmissionDenial = denial };
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton(new ConnectorAuthCatalog([GoogleMailConnectorTests.BrokerProfile()]));
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(new BoundMailMemoryStorage());
            services.AddSingleton(provider => new BrokerInstallationRegistration(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), new HttpClient(handler, false)));
            services.AddSingleton(provider => new BrokerHandoffClient(provider.GetRequiredService<BrokerInstallationIdentityProvider>(), provider.GetRequiredService<BrokerInstallationRegistration>(), new HttpClient(handler, false)));
        });
        fixture.AddMailConnector("kautuka-google-mail");
        string bearer = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage started = await PostExternalAsync(fixture, "/api/connectors/kautuka-google-mail/oauth/bound-begin", new { }, applianceOrigin, bearer);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        string cookie = started.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("kautuka_bound_mail_", StringComparison.Ordinal)).Split(';')[0];
        using JsonDocument payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string state = payload.RootElement.GetProperty("state").GetString()!, tab = payload.RootElement.GetProperty("tabHandle").GetString()!;
        var headers = new Dictionary<string, string> { ["Cookie"] = cookie };
        using HttpResponseMessage bound = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-bind", new { state, tabHandle = tab, nonce = new string('A', 43) }, applianceOrigin, bearer, headers);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        using HttpResponseMessage completed = await PostExternalAsync(fixture, "/api/connectors/oauth/bound-complete", new { state, tabHandle = tab }, applianceOrigin, bearer, headers);
        Assert.Equal((HttpStatusCode)status, completed.StatusCode);
        Assert.Equal(denial, completed.Headers.GetValues(status == 403 ? "Kautuka-Mail-Error" : "Kautuka-Admission-Denied").Single());
        if (status == 429) Assert.Equal(TimeSpan.FromDays(1), completed.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task BrokerSettingsRequireAdminAndFenceSwitchUntilRestartOrCancellation()
    {
        var storage = new BoundMailMemoryStorage();
        using var mail = new BrokerMailCredentialStore(storage);
        var sessions = new BoundMailSessionStore();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(storage);
            services.AddSingleton(mail);
            services.AddSingleton(sessions);
        });
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        fixture.CreateUser("broker-member", "Testing1", isActive: true);
        string member = await fixture.LoginAsAsync("broker-member", "Testing1");
        const string path = "/api/settings/system/authentication-broker";
        var replacement = new { origin = "https://selfhost.example", googleSocialClientId = "custom.apps.googleusercontent.com", microsoftSocialClientId = (string?)null };
        using HttpResponseMessage denied = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, member, replacement);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using HttpResponseMessage invalid = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "http://selfhost.example", googleSocialClientId = "id", microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        string adminId = fixture.GetUserId("admin");
        fixture.CreateUser("inactive-broker-admin", "Testing1", isActive: false);
        fixture.SetRole("inactive-broker-admin", "admin");
        fixture.SetPasswordlessForTest(adminId, true);
        using HttpResponseMessage noLocalAdmin = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin, replacement);
        Assert.Equal(HttpStatusCode.Conflict, noLocalAdmin.StatusCode);
        using HttpResponseMessage noLocalAdminIds = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "https://auth.kautuka.test", googleSocialClientId = "new-id", microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, noLocalAdminIds.StatusCode);
        Assert.False(sessions.IsBrokerSwitchPending);
        using HttpResponseMessage restoreWithoutLocalAdmin = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "https://auth.kautuka.test", googleSocialClientId = (string?)null, microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, restoreWithoutLocalAdmin.StatusCode);
        fixture.SetPasswordlessForTest(adminId, false);
        string id = new('A', 43);
        var credential = new BrokerMailCredential("google", id, id, 1, "kmf1." + new string('x', 40), ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.test");
        await mail.StageActivationAsync("user", "mail", new(credential, id), CancellationToken.None);
        using HttpResponseMessage blocked = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin, replacement);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.False(sessions.IsBrokerSwitchPending);
        await mail.DiscardActivationAsync("user", "mail", id, CancellationToken.None);
        using HttpResponseMessage saved = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin, replacement);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using JsonDocument savedBody = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("https://selfhost.example", savedBody.RootElement.GetProperty("pending").GetProperty("origin").GetString());
        Assert.True(savedBody.RootElement.GetProperty("switchPending").GetBoolean());
        GatewaySystemSettings reloaded = fixture.ReloadState().SystemSettings;
        Assert.Equal("https://selfhost.example", reloaded.AuthenticationBrokerOrigin);
        Assert.Equal("custom.apps.googleusercontent.com", reloaded.AuthenticationBrokerGoogleClientId);
        Assert.Equal(string.Empty, reloaded.AuthenticationBrokerMicrosoftClientId);
        var restarted = await fixture.ReloadBrokerSnapshotAsync();
        Assert.Equal("https://selfhost.example", restarted.Host);
        Assert.Equal("custom.apps.googleusercontent.com", restarted.Google);
        Assert.False(restarted.Microsoft);
        var fallback = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Kautuka:Auth:Google:ClientId"] = "hosted-google", ["Kautuka:Auth:Microsoft:ClientId"] = "hosted-microsoft" }).Build();
        var restartedProviders = Kautuka.Gateway.Domain.Auth.ExternalLoginProviders.Create(fallback,
            ConnectorAuthEndpoint.Parse(reloaded.AuthenticationBrokerOrigin), reloaded);
        Assert.Equal("https://selfhost.example", restartedProviders.Host);
        Assert.Equal("custom.apps.googleusercontent.com", restartedProviders.GoogleClientId);
        Assert.False(restartedProviders.Microsoft);
        Assert.True(sessions.IsBrokerSwitchPending);
        Assert.Null(sessions.BeginStart("user", "mail"));
        Assert.False(sessions.TryBeginBrokerRotation());
        using HttpResponseMessage canceled = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "https://auth.kautuka.test", googleSocialClientId = (string?)null, microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        Assert.False(sessions.IsBrokerSwitchPending);
    }


    [Fact]
    public async Task BrokerSettingsWriteFailureKeepsFenceAndRollsBackWholeBundle()
    {
        var sessions = new BoundMailSessionStore();
        var config = new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1000", ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test" };
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(config, null, null, services =>
        {
            services.AddSingleton<IBrokerInstallationIdentityStore>(new BoundMemoryIdentityStore());
            services.AddSingleton<IBrokerMailCredentialStorage>(new BoundMailMemoryStorage());
            services.AddSingleton(sessions);
        });
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        const string path = "/api/settings/system/authentication-broker";
        using HttpResponseMessage previous = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "https://previous.example", googleSocialClientId = "previous-id", microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, previous.StatusCode);
        GatewaySystemSettings before = fixture.ReloadState().SystemSettings;
        fixture.SetBrokerSaveFailure(true);
        var next = new { origin = "https://selfhost.example", googleSocialClientId = "custom-id", microsoftSocialClientId = (string?)null };
        using HttpResponseMessage failed = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin, next);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(sessions.IsBrokerSwitchPending);
        Assert.Null(sessions.BeginStart("user", "mail"));
        using HttpResponseMessage status = await fixture.SendAuthenticatedAsync(HttpMethod.Get, path, admin);
        using JsonDocument statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, statusBody.RootElement.GetProperty("pending").ValueKind);
        Assert.True(statusBody.RootElement.GetProperty("switchPending").GetBoolean());
        fixture.SetBrokerSaveFailure(false);
        GatewaySystemSettings after = fixture.ReloadState().SystemSettings;
        Assert.Equal(before.AuthenticationBrokerOrigin, after.AuthenticationBrokerOrigin);
        Assert.Equal(before.AuthenticationBrokerGoogleClientId, after.AuthenticationBrokerGoogleClientId);
        Assert.Equal(before.AuthenticationBrokerMicrosoftClientId, after.AuthenticationBrokerMicrosoftClientId);
        using HttpResponseMessage restore = await fixture.SendAuthenticatedAsync(HttpMethod.Put, path, admin,
            new { origin = "https://auth.kautuka.test", googleSocialClientId = (string?)null, microsoftSocialClientId = (string?)null });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.False(sessions.IsBrokerSwitchPending);
    }

    private sealed class BoundMailMemoryStorage : IBrokerMailCredentialStorage
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. values.Keys]);
        public Task<string?> ReadAsync(string key, CancellationToken ct) => Task.FromResult(values.GetValueOrDefault(key));
        public Task WriteAsync(string key, string value, CancellationToken ct) { values[key] = value; return Task.CompletedTask; }
    }

    private sealed class BoundMailBrokerHandler : HttpMessageHandler
    {
        public int ExchangeCount { get; private set; }
        public int Acknowledgements { get; private set; }
        public bool BlockExchange { get; init; }
        public bool BlockAcknowledgement { get; init; }
        public string? AdmissionDenial { get; init; }
        public int Disconnects { get; private set; }
        private bool active;
        public bool AcknowledgementCommittedBeforeDisconnect { get; private set; }
        public TaskCompletionSource ExchangeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueExchange { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AcknowledgementStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/installation/flow") return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"flowId\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}") };
            if (path == "/installation/mail-exchange")
            {
                ExchangeCount++;
                if (AdmissionDenial is not null)
                {
                    var denied = new HttpResponseMessage(AdmissionDenial == "new_account_limit" ? HttpStatusCode.TooManyRequests
                        : AdmissionDenial == "insufficient_scope" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict);
                    denied.Headers.TryAddWithoutValidation(AdmissionDenial == "insufficient_scope" ? "Kautuka-Mail-Error" : "Kautuka-Admission-Denied", AdmissionDenial);
                    if (AdmissionDenial == "new_account_limit") denied.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(1));
                    return denied;
                }
                if (BlockExchange) { ExchangeStarted.TrySetResult(); await ContinueExchange.Task.WaitAsync(ct); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"provider\":\"google\",\"refreshToken\":\"kmf1.{new string('x', 40)}\",\"generation\":1}}") };
            }
            if (path == "/installation/mail-status") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(active ? "{\"active\":true}" : "{\"active\":false}") };
            if (path == "/installation/mail-ack")
            {
                Acknowledgements++;
                active = true; // The broker commits before its response is delayed.
                if (BlockAcknowledgement) { AcknowledgementStarted.TrySetResult(); await ContinueAcknowledgement.Task.WaitAsync(ct); }
            }
            if (path == "/installation/mail-disconnect") { Disconnects++; AcknowledgementCommittedBeforeDisconnect = active; active = false; }
            if (path == "/installation/mail-cancel" && active) return new HttpResponseMessage(HttpStatusCode.Conflict);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
