using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Auth;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Kautuka.IntegrationTests;

public sealed partial class AuthEndpointActivationTests
{
    private const string applianceOrigin = "https://appliance.example";
    private const string googleAudience = "google-login-client";
    private const string microsoftAudience = "microsoft-login-client";

    [Fact]
    public void ExternalLoginSessionsExpireAndRespectTheActiveSessionCap()
    {
        var sessions = new ExternalLoginSessionStore();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string cookie = ExternalLoginSessionStore.CreateCookieBinding();
        for (int index = 0; index < ExternalLoginSessionStore.MaximumSessions + 1; index++)
        {
            sessions.Save(index.ToString(System.Globalization.CultureInfo.InvariantCulture), CreateSession(now.AddMinutes(5), cookie));
        }
        Assert.False(sessions.TryRemove("0", cookie, now, out _));
        Assert.True(sessions.TryRemove("1", cookie, now, out ExternalLoginSession? newest));
        Assert.NotNull(newest);

        sessions.Save("expired", CreateSession(now.AddSeconds(-1), cookie));
        Assert.False(sessions.TryRemove("expired", cookie, now, out _));
    }

    [Fact]
    public async Task ExternalLoginRejectsMissingOrDifferentBrowserCookieBeforeCodeExchange()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null);
        (string missingCookieState, string missingCookieNonce) = await StartAsync(fixture, "google");
        identities.Use("missing-cookie", identities.Sign(GoogleClaims("owner@example.com", "owner-subject", missingCookieNonce, "Owner")));
        using HttpResponseMessage missingCookie = await PostExternalAsync(fixture, "/api/auth/external/complete",
            new { code = "missing-cookie", state = missingCookieState, brokerTicket = "test-broker-ticket" }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.Unauthorized, missingCookie.StatusCode);
        Assert.Null(identities.LastExchange);

        (string otherCookieState, string otherCookieNonce) = await StartAsync(fixture, "google");
        identities.Use("other-cookie", identities.Sign(GoogleClaims("owner@example.com", "owner-subject", otherCookieNonce, "Owner")));
        using HttpResponseMessage otherCookie = await PostExternalAsync(fixture, "/api/auth/external/complete",
            new { code = "other-cookie", state = otherCookieState, brokerTicket = "test-broker-ticket" }, applianceOrigin, null,
            new Dictionary<string, string> { ["Cookie"] = "kautuka_external_login=not-the-browser-binding" });
        Assert.Equal(HttpStatusCode.Unauthorized, otherCookie.StatusCode);
        Assert.Null(identities.LastExchange);

        (string sameBrowserState, string sameBrowserNonce) = await StartAsync(fixture, "google");
        identities.Use("same-browser", identities.Sign(GoogleClaims("owner@example.com", "owner-subject", sameBrowserNonce, "Owner")));
        using HttpResponseMessage sameBrowser = await CompleteAsync(fixture, "same-browser", sameBrowserState, setupToken: fixture.SetupToken);
        Assert.Equal(HttpStatusCode.OK, sameBrowser.StatusCode);
        Assert.Equal("same-browser", identities.LastExchange?.Code);
    }

    [Fact]
    public async Task ExternalLoginRequiresSetupPossessionBeforeFirstAdminCreation()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null);
        (string missingState, string missingNonce) = await StartAsync(fixture, "google");
        identities.Use("missing-setup", identities.Sign(GoogleClaims("owner@example.com", "owner-subject", missingNonce, "Owner")));
        using HttpResponseMessage missing = await CompleteAsync(fixture, "missing-setup", missingState);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        (string invalidState, string invalidNonce) = await StartAsync(fixture, "google");
        identities.Use("invalid-setup", identities.Sign(GoogleClaims("owner@example.com", "owner-subject", invalidNonce, "Owner")));
        using HttpResponseMessage invalid = await PostExternalAsync(fixture, "/api/auth/external/complete",
            new { code = "invalid-setup", state = invalidState, brokerTicket = "test-broker-ticket", setupToken = "incorrect" }, applianceOrigin, null,
            new Dictionary<string, string> { ["Cookie"] = fixture.ExternalLoginCookie });
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        using HttpResponseMessage setupStatus = await fixture.SendAnonymousAsync(HttpMethod.Get, "/api/auth/setup/status", null);
        using JsonDocument status = JsonDocument.Parse(await setupStatus.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(status.RootElement.GetProperty("needsSetup").GetBoolean());
    }

    private static ExternalLoginSession CreateSession(DateTimeOffset expiresAt, string cookie)
    {
        return new ExternalLoginSession
        {
            Provider = "google",
            CodeVerifier = "verifier",
            Nonce = "nonce",
            ReturnOrigin = applianceOrigin,
            CookieBindingHash = ExternalLoginSessionStore.HashBinding(cookie),
            Intent = "LOGIN",
            ExpiresAt = expiresAt,
        };
    }

    [Fact]
    public void RefreshCookieSecureFollowsTrustedHopOnly()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");
        Assert.True(RefreshCookieSecurity.IsSecure(context.Request));

        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = null;
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.True(RefreshCookieSecurity.IsSecure(context.Request));

        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(RefreshCookieSecurity.IsSecure(context.Request));

        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");
        Assert.False(RefreshCookieSecurity.IsSecure(context.Request));

        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        Assert.False(RefreshCookieSecurity.IsSecure(context.Request));

        context.Request.Headers.Remove("X-Forwarded-Proto");
        Assert.False(RefreshCookieSecurity.IsSecure(context.Request));
    }

    [Fact]
    public async Task ExternalLoginStaysOffWithoutHostedClient()
    {
        await using AuthApiFixture fixture = await AuthApiFixture.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Kautuka:ConnectorAuth:Host"] = string.Empty,
                ["Kautuka:Auth:Google:ClientId"] = googleAudience,
            },
            identities: null,
            logs: null);

        using HttpResponseMessage providers = await fixture.SendAnonymousAsync(HttpMethod.Get, "/api/auth/external/providers", null);
        Assert.Equal(HttpStatusCode.OK, providers.StatusCode);
        using JsonDocument payload = JsonDocument.Parse(await providers.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(payload.RootElement.GetProperty("google").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("microsoft").GetBoolean());

        using HttpResponseMessage start = await PostExternalAsync(fixture, "/api/auth/external/start", new { provider = "google" }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Fact]
    public async Task ExternalLoginStartRejectsProviderWithoutClientId()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null);
        using HttpResponseMessage start = await PostExternalAsync(fixture, "/api/auth/external/start", new { provider = "microsoft" }, applianceOrigin, null);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    [Fact]
    public async Task InviteByEmailSignsInWithMicrosoftAndHasNoLocalPassword()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null, includeMicrosoft: true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage missing = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            adminToken,
            new { role = "user" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            adminToken,
            new { email = "Guest@example.com", role = "user" });
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        using JsonDocument invitedPayload = JsonDocument.Parse(await invited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("guest", invitedPayload.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("guest@example.com", invitedPayload.RootElement.GetProperty("user").GetProperty("email").GetString());

        (string state, string nonce) = await StartAsync(fixture, "microsoft");
        identities.Use("guest-code", identities.Sign(MicrosoftClaims("guest@example.com", "ms-guest", nonce, personalTenant)));
        string guestInvite = invitedPayload.RootElement.GetProperty("inviteCode").GetString()!;
        using HttpResponseMessage signedIn = await CompleteAsync(fixture, "guest-code", state, inviteCode: guestInvite);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        using JsonDocument signedInPayload = JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("guest", signedInPayload.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.False(signedInPayload.RootElement.GetProperty("user").GetProperty("hasPassword").GetBoolean());
        Assert.Equal("microsoft", signedInPayload.RootElement.GetProperty("user").GetProperty("externalProviders")[0].GetString());
    }

    [Fact]
    public async Task MicrosoftInviteRejectsUnverifiedEmailDomainWithoutConsumingInvite()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null, includeMicrosoft: true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            adminToken,
            new { email = "alice@example.com", role = "user" });
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        using JsonDocument invite = JsonDocument.Parse(await invited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string inviteCode = invite.RootElement.GetProperty("inviteCode").GetString()!;

        (string attackerState, string attackerNonce) = await StartAsync(fixture, "microsoft");
        Dictionary<string, object?> attackerClaims = MicrosoftClaims("alice@example.com", "attacker-tenant-user", attackerNonce, personalTenant);
        attackerClaims["xms_edov"] = false;
        identities.Use("unverified-domain", identities.Sign(attackerClaims));
        using HttpResponseMessage rejected = await CompleteAsync(fixture, "unverified-domain", attackerState, inviteCode: inviteCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        (string ownerState, string ownerNonce) = await StartAsync(fixture, "microsoft");
        Dictionary<string, object?> ownerClaims = MicrosoftClaims("alice@example.com", "verified-tenant-user", ownerNonce, personalTenant);
        ownerClaims["xms_edov"] = "1";
        identities.Use("verified-domain", identities.Sign(ownerClaims));
        using HttpResponseMessage accepted = await CompleteAsync(fixture, "verified-domain", ownerState, inviteCode: inviteCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData("number")]
    [InlineData("email_verified")]
    public async Task MicrosoftInviteAcceptsSignedAlternativeEmailVerificationClaims(string verificationClaim)
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null, includeMicrosoft: true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            adminToken,
            new { email = "alice@example.com", role = "user" });
        using JsonDocument invite = JsonDocument.Parse(await invited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string inviteCode = invite.RootElement.GetProperty("inviteCode").GetString()!;

        (string state, string nonce) = await StartAsync(fixture, "microsoft");
        Dictionary<string, object?> claims = MicrosoftClaims("alice@example.com", "verified-user", nonce, personalTenant);
        claims.Remove("xms_edov");
        if (verificationClaim == "number")
        {
            claims["xms_edov"] = 1;
        }
        else
        {
            claims["email_verified"] = true;
        }

        identities.Use("verified-email", identities.Sign(claims));
        using HttpResponseMessage accepted = await CompleteAsync(fixture, "verified-email", state, inviteCode: inviteCode);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task InviteEmailMismatchDoesNotConsumeAndConcurrentInviteRedemptionSucceedsOnce()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null);
        (string adminState, string adminNonce) = await StartAsync(fixture, "google");
        identities.Use("admin", identities.Sign(GoogleClaims("admin@example.com", "admin-subject", adminNonce, "Admin")));
        using HttpResponseMessage admin = await CompleteAsync(fixture, "admin", adminState, setupToken: fixture.SetupToken);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        using JsonDocument adminPayload = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string adminToken = adminPayload.RootElement.GetProperty("accessToken").GetString()!;
        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/settings/users/invite", adminToken,
            new { username = "invitee", email = "invitee@example.com", role = "user" });
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        using JsonDocument invitePayload = JsonDocument.Parse(await invited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string inviteCode = invitePayload.RootElement.GetProperty("inviteCode").GetString()!;

        (string wrongState, string wrongNonce) = await StartAsync(fixture, "google");
        identities.Use("wrong-email", identities.Sign(GoogleClaims("attacker@example.com", "attacker-subject", wrongNonce, "Attacker")));
        using HttpResponseMessage wrongEmail = await CompleteAsync(fixture, "wrong-email", wrongState, inviteCode: inviteCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongEmail.StatusCode);

        (string conflictState, string conflictNonce) = await StartAsync(fixture, "google");
        identities.Use("provider-conflict", identities.Sign(GoogleClaims("invitee@example.com", "admin-subject", conflictNonce, "Invitee")));
        using HttpResponseMessage providerConflict = await CompleteAsync(fixture, "provider-conflict", conflictState, inviteCode: inviteCode);
        Assert.Equal(HttpStatusCode.Unauthorized, providerConflict.StatusCode);

        (string firstState, string firstNonce) = await StartAsync(fixture, "google");
        (string secondState, string secondNonce) = await StartAsync(fixture, "google");
        identities.Use("first-redemption", identities.Sign(GoogleClaims("invitee@example.com", "invitee-subject", firstNonce, "Invitee")));
        identities.Use("second-redemption", identities.Sign(GoogleClaims("invitee@example.com", "invitee-subject", secondNonce, "Invitee")));
        HttpResponseMessage[] redemptions = await Task.WhenAll(
            CompleteAsync(fixture, "first-redemption", firstState, inviteCode: inviteCode),
            CompleteAsync(fixture, "second-redemption", secondState, inviteCode: inviteCode));
        try
        {
            Assert.Single(redemptions, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(redemptions, response => response.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (HttpResponseMessage response in redemptions) response.Dispose();
        }

        using HttpResponseMessage expiringInvite = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/settings/users/invite", adminToken,
            new { username = "expired", email = "expired@example.com", role = "user" });
        using JsonDocument expiringPayload = JsonDocument.Parse(await expiringInvite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string expiringCode = expiringPayload.RootElement.GetProperty("inviteCode").GetString()!;
        fixture.ExpireInviteCode(expiringCode);
        (string expiredState, string expiredNonce) = await StartAsync(fixture, "google");
        identities.Use("expired-redemption", identities.Sign(GoogleClaims("expired@example.com", "expired-subject", expiredNonce, "Expired")));
        using HttpResponseMessage expired = await CompleteAsync(fixture, "expired-redemption", expiredState, inviteCode: expiringCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task ExternalLinkRequiresSameActorAndRejectsIdentityOwnedByAnotherUser()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null, includeMicrosoft: true);
        (string adminState, string adminNonce) = await StartAsync(fixture, "google");
        identities.Use("admin", identities.Sign(GoogleClaims("admin@example.com", "admin-subject", adminNonce, "Admin")));
        using HttpResponseMessage admin = await CompleteAsync(fixture, "admin", adminState, setupToken: fixture.SetupToken);
        using JsonDocument adminPayload = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string adminToken = adminPayload.RootElement.GetProperty("accessToken").GetString()!;
        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/settings/users/invite", adminToken,
            new { username = "member", email = "member@example.com", role = "user" });
        using JsonDocument invitePayload = JsonDocument.Parse(await invited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string inviteCode = invitePayload.RootElement.GetProperty("inviteCode").GetString()!;
        (string memberState, string memberNonce) = await StartAsync(fixture, "google");
        identities.Use("member", identities.Sign(GoogleClaims("member@example.com", "member-subject", memberNonce, "Member")));
        using HttpResponseMessage member = await CompleteAsync(fixture, "member", memberState, inviteCode: inviteCode);
        using JsonDocument memberPayload = JsonDocument.Parse(await member.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string memberToken = memberPayload.RootElement.GetProperty("accessToken").GetString()!;

        (string mismatchedActorState, string mismatchedNonce) = await StartAsync(fixture, "google", "link", adminToken);
        identities.Use("mismatch", identities.Sign(GoogleClaims("unlinked@example.com", "unlinked-subject", mismatchedNonce, "Other")));
        using HttpResponseMessage mismatchedActor = await CompleteAsync(fixture, "mismatch", mismatchedActorState, bearer: memberToken);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatchedActor.StatusCode);

        (string ownedState, string ownedNonce) = await StartAsync(fixture, "google", "link", adminToken);
        identities.Use("owned-by-member", identities.Sign(GoogleClaims("member@example.com", "member-subject", ownedNonce, "Member")));
        using HttpResponseMessage wrongOwner = await CompleteAsync(fixture, "owned-by-member", ownedState, bearer: adminToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongOwner.StatusCode);

        (string correctState, string correctNonce) = await StartAsync(fixture, "google", "link", memberToken);
        identities.Use("correct-owner", identities.Sign(GoogleClaims("member@example.com", "member-subject", correctNonce, "Member")));
        using HttpResponseMessage correctOwner = await CompleteAsync(fixture, "correct-owner", correctState, bearer: memberToken);
        Assert.Equal(HttpStatusCode.OK, correctOwner.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await correctOwner.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(memberPayload.RootElement.GetProperty("user").GetProperty("id").GetString(), result.RootElement.GetProperty("user").GetProperty("id").GetString());

        using HttpResponseMessage msInvite = await fixture.SendAuthenticatedAsync(HttpMethod.Post, "/api/settings/users/invite", adminToken,
            new { username = "msmember", email = "msmember@example.com", role = "user" });
        using JsonDocument msInvitePayload = JsonDocument.Parse(await msInvite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (string msState, string msNonce) = await StartAsync(fixture, "microsoft");
        identities.Use("ms-member", identities.Sign(MicrosoftClaims("msmember@example.com", "ms-member-subject", msNonce, personalTenant, objectId: "stable-object")));
        using HttpResponseMessage msMember = await CompleteAsync(fixture, "ms-member", msState, inviteCode: msInvitePayload.RootElement.GetProperty("inviteCode").GetString());
        Assert.Equal(HttpStatusCode.OK, msMember.StatusCode);

        (string aliasState, string aliasNonce) = await StartAsync(fixture, "microsoft", "link", adminToken);
        identities.Use("tenant-alias", identities.Sign(MicrosoftClaims("other@example.com", "different-subject", aliasNonce, personalTenant, objectId: "stable-object")));
        using HttpResponseMessage tenantAlias = await CompleteAsync(fixture, "tenant-alias", aliasState, bearer: adminToken);
        Assert.Equal(HttpStatusCode.Unauthorized, tenantAlias.StatusCode);
    }

    [Fact]
    public async Task ExternalLoginProvesAccountTokenStateAndCookieRules()
    {
        var logs = new CollectingLoggerProvider();
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs, includeMicrosoft: true);

        using HttpResponseMessage providers = await fixture.SendAnonymousAsync(HttpMethod.Get, "/api/auth/external/providers", null);
        using JsonDocument providerPayload = JsonDocument.Parse(await providers.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(providerPayload.RootElement.GetProperty("google").GetBoolean());
        Assert.True(providerPayload.RootElement.GetProperty("microsoft").GetBoolean());

        (string state, string nonce) = await StartAsync(fixture, "google");
        Assert.NotNull(fixture.ExternalLoginSetCookie);
        Assert.Contains("httponly", fixture.ExternalLoginSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", fixture.ExternalLoginSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", fixture.ExternalLoginSetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth/external", fixture.ExternalLoginSetCookie, StringComparison.OrdinalIgnoreCase);
        identities.Use("good-code", identities.Sign(GoogleClaims("ada@example.com", "subject-ada", nonce, "Ada Admin")));
        using HttpResponseMessage created = await CompleteAsync(fixture, "good-code", state, setupToken: fixture.SetupToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(("google", "good-code", "test-broker-ticket", applianceOrigin), identities.LastExchange);
        string createdCookie = Assert.Single(created.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain("Secure", createdCookie, StringComparison.OrdinalIgnoreCase);
        using JsonDocument createdPayload = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ada", createdPayload.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("admin", createdPayload.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.False(createdPayload.RootElement.GetProperty("user").GetProperty("hasPassword").GetBoolean());
        Assert.Equal("google", createdPayload.RootElement.GetProperty("user").GetProperty("externalProviders")[0].GetString());
        string adminToken = createdPayload.RootElement.GetProperty("accessToken").GetString()!;

        using HttpResponseMessage replay = await CompleteAsync(fixture, "good-code", state);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        (string linkedState, string linkedNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("moved@example.com", "subject-ada", linkedNonce, "Ada Admin")));
        using HttpResponseMessage linked = await PostExternalAsync(
            fixture,
            "/api/auth/external/complete",
            new { code = "good-code", state = linkedState, brokerTicket = "test-broker-ticket" },
            applianceOrigin,
            null,
            new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https", ["Cookie"] = fixture.ExternalLoginCookie });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Contains("Secure", Assert.Single(linked.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        (string takenState, string takenNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("ada@example.com", "subject-other", takenNonce, "Other")));
        using HttpResponseMessage taken = await CompleteAsync(fixture, "good-code", takenState);
        Assert.Equal(HttpStatusCode.Unauthorized, taken.StatusCode);

        using HttpResponseMessage clearLast = await fixture.SendAuthenticatedAsync(
            HttpMethod.Delete,
            "/api/settings/me/external-logins/google",
            adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, clearLast.StatusCode);

        using HttpResponseMessage firstPassword = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            adminToken,
            new { currentPassword = string.Empty, newPassword = "Testing1" });
        Assert.Equal(HttpStatusCode.OK, firstPassword.StatusCode);
        using HttpResponseMessage staleRefresh = await fixture.SendAnonymousWithCookieAsync(
            HttpMethod.Post,
            "/api/auth/refresh",
            createdCookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, staleRefresh.StatusCode);

        string passwordToken = await fixture.LoginAsAsync("ada", "Testing1");
        using HttpResponseMessage passwordAgain = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            passwordToken,
            new { currentPassword = string.Empty, newPassword = "Testing22" });
        Assert.Equal(HttpStatusCode.BadRequest, passwordAgain.StatusCode);
    }

    [Fact]
    public async Task ExternalLoginBindsOneActiveCanonicalEmail()
    {
        var logs = new CollectingLoggerProvider();
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs, includeMicrosoft: true);
        (string adminState, string adminNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("ada@example.com", "subject-ada", adminNonce, "Ada Admin")));
        using HttpResponseMessage admin = await CompleteAsync(fixture, "good-code", adminState, setupToken: fixture.SetupToken);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        using JsonDocument adminPayload = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string adminAccess = adminPayload.RootElement.GetProperty("accessToken").GetString()!;
        using HttpResponseMessage adminPassword = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            adminAccess,
            new { currentPassword = string.Empty, newPassword = "Testing1" });
        Assert.Equal(HttpStatusCode.OK, adminPassword.StatusCode);
        string passwordToken = await fixture.LoginAsAsync("ada", "Testing1");

        using HttpResponseMessage invited = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            passwordToken,
            new { username = "member", email = "Ada@Example.com", password = "Testing1", role = "user" });
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);

        (string bindState, string bindNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("ada@example.com", "subject-member", bindNonce, "Member")));
        using HttpResponseMessage bound = await CompleteAsync(fixture, "good-code", bindState, bearer: passwordToken);
        Assert.Equal(HttpStatusCode.Unauthorized, bound.StatusCode);

        using HttpResponseMessage second = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            passwordToken,
            new { username = "twin", email = "Ada@Example.com", password = "Testing1", role = "user" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        (string ambiguousState, string ambiguousNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("ada@example.com", "subject-ambiguous", ambiguousNonce, "Twin")));
        using HttpResponseMessage ambiguous = await CompleteAsync(fixture, "good-code", ambiguousState);
        Assert.Equal(HttpStatusCode.Unauthorized, ambiguous.StatusCode);

        using HttpResponseMessage unique = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            passwordToken,
            new { username = "unique", email = "unique@example.com", password = "Testing1", role = "user" });
        Assert.Equal(HttpStatusCode.OK, unique.StatusCode);
        using JsonDocument uniquePayload = JsonDocument.Parse(await unique.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string uniqueId = uniquePayload.RootElement.GetProperty("user").GetProperty("id").GetString()!;

        (string uniqueState, string uniqueNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("unique@example.com", "subject-unique", uniqueNonce, "Unique")));
        string uniqueInvite = uniquePayload.RootElement.GetProperty("inviteCode").GetString()!;
        using HttpResponseMessage uniqueBound = await CompleteAsync(fixture, "good-code", uniqueState, inviteCode: uniqueInvite);
        Assert.Equal(HttpStatusCode.OK, uniqueBound.StatusCode);
        string uniqueCookie = Assert.Single(uniqueBound.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using JsonDocument uniqueBoundPayload = JsonDocument.Parse(await uniqueBound.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string uniqueAccess = uniqueBoundPayload.RootElement.GetProperty("accessToken").GetString()!;

        using HttpResponseMessage renamed = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/profile",
            uniqueAccess,
            new { email = "renamed@example.com", displayName = "Unique" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        (string sameSubjectState, string sameSubjectNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("someone-else@example.com", "subject-unique", sameSubjectNonce, "Unique")));
        using HttpResponseMessage sameSubject = await CompleteAsync(fixture, "good-code", sameSubjectState);
        Assert.Equal(HttpStatusCode.OK, sameSubject.StatusCode);

        (string movedState, string movedNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("renamed@example.com", "subject-moved", movedNonce, "Moved")));
        using HttpResponseMessage moved = await CompleteAsync(fixture, "good-code", movedState);
        Assert.Equal(HttpStatusCode.Unauthorized, moved.StatusCode);

        using HttpResponseMessage selfClear = await fixture.SendAuthenticatedAsync(
            HttpMethod.Delete,
            "/api/settings/me/external-logins/google",
            uniqueAccess);
        Assert.Equal(HttpStatusCode.OK, selfClear.StatusCode);
        using HttpResponseMessage stillSignedIn = await fixture.SendAnonymousWithCookieAsync(HttpMethod.Post, "/api/auth/refresh", uniqueCookie);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

        (string reboundState, string reboundNonce) = await StartAsync(fixture, "microsoft", "link", uniqueAccess);
        identities.Use("good-code", identities.Sign(MicrosoftClaims("renamed@example.com", "ms-unique", reboundNonce, personalTenant)));
        using HttpResponseMessage rebound = await CompleteAsync(fixture, "good-code", reboundState, bearer: uniqueAccess);
        Assert.Equal(HttpStatusCode.OK, rebound.StatusCode);
        string reboundCookie = Assert.Single(rebound.Headers.GetValues("Set-Cookie")).Split(';')[0];

        using HttpResponseMessage adminClear = await fixture.SendAuthenticatedAsync(
            HttpMethod.Delete,
            "/api/settings/users/" + uniqueId + "/external-logins/microsoft",
            passwordToken);
        Assert.Equal(HttpStatusCode.OK, adminClear.StatusCode);
        string[] logged;
        lock (logs.Messages)
        {
            logged = [.. logs.Messages];
        }

        Assert.Contains(logged, message => message.Contains("Actor=" + fixture.GetUserId("ada"), StringComparison.Ordinal)
            && message.Contains("Target=" + uniqueId, StringComparison.Ordinal)
            && message.Contains("Provider=microsoft", StringComparison.Ordinal));
        using HttpResponseMessage revoked = await fixture.SendAnonymousWithCookieAsync(HttpMethod.Post, "/api/auth/refresh", reboundCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        using HttpResponseMessage sleepyInvite = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            passwordToken,
            new { username = "sleepy", email = "sleepy@example.com", password = "Testing1", role = "user" });
        Assert.Equal(HttpStatusCode.OK, sleepyInvite.StatusCode);
        using JsonDocument sleepyPayload = JsonDocument.Parse(await sleepyInvite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string sleepyId = sleepyPayload.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        (string sleepyState, string sleepyNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("sleepy@example.com", "subject-sleepy", sleepyNonce, "Sleepy")));
        string sleepyCode = sleepyPayload.RootElement.GetProperty("inviteCode").GetString()!;
        using HttpResponseMessage sleepyBound = await CompleteAsync(fixture, "good-code", sleepyState, inviteCode: sleepyCode);
        Assert.Equal(HttpStatusCode.OK, sleepyBound.StatusCode);
        using HttpResponseMessage inactive = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/users/" + sleepyId + "/active",
            passwordToken,
            new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        (string inactiveState, string inactiveNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("sleepy@example.com", "subject-sleepy", inactiveNonce, "Sleepy")));
        using HttpResponseMessage inactiveSignIn = await CompleteAsync(fixture, "good-code", inactiveState);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveSignIn.StatusCode);

        using HttpResponseMessage canonInvite = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            passwordToken,
            new { username = "canon", email = "Canon@Example.com", password = "Testing1", role = "user" });
        Assert.Equal(HttpStatusCode.OK, canonInvite.StatusCode);
        (string canonState, string canonNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("canon@example.com", "subject-canon", canonNonce, "Canon")));
        using JsonDocument canonPayload = JsonDocument.Parse(await canonInvite.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string canonCode = canonPayload.RootElement.GetProperty("inviteCode").GetString()!;
        using HttpResponseMessage canon = await CompleteAsync(fixture, "good-code", canonState, inviteCode: canonCode);
        Assert.Equal(HttpStatusCode.OK, canon.StatusCode);

        await AssertRejectedAsync(fixture, identities, GoogleClaims("ada+tag@example.com", "subject-plus", "nonce", "Plus"));
        await AssertRejectedAsync(fixture, identities, GoogleClaims("a.da@example.com", "subject-dots", "nonce", "Dots"));
        await AssertRejectedAsync(fixture, identities, GoogleClaims("not-invited@example.com", "subject-missing", "nonce", "Missing"));
        await AssertRejectedAsync(fixture, identities, GoogleClaims("ada@example.com", "subject-bad", "nonce", "Bad", emailVerified: false));
        await AssertRejectedAsync(fixture, identities, GoogleClaims("ada@example.com", "subject-bad", "nonce", "Bad", issuer: "https://evil.example"));
        await AssertRejectedAsync(fixture, identities, GoogleClaims("ada@example.com", "subject-bad", "nonce", "Bad", audience: new[] { googleAudience }));
        await AssertRejectedAsync(fixture, identities, MicrosoftClaims("renamed@example.com", "ms-common", "nonce", "common"));
        await AssertRejectedAsync(fixture, identities, MicrosoftClaims("renamed@example.com", "ms-sts", "nonce", personalTenant, issuerPrefix: "https://sts.windows.net/"));
        await AssertRejectedAsync(fixture, identities, new Dictionary<string, object?>
        {
            ["iss"] = "https://login.microsoftonline.com/" + personalTenant + "/v2.0",
            ["tid"] = personalTenant,
            ["aud"] = microsoftAudience,
            ["sub"] = "ms-upn",
            ["preferred_username"] = "renamed@example.com",
            ["nonce"] = "nonce",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        });

        (string badNonceState, string badNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("renamed@example.com", "subject-nonce", badNonce + "x", "Nonce")));
        using HttpResponseMessage badNonceResponse = await CompleteAsync(fixture, "good-code", badNonceState);
        Assert.Equal(HttpStatusCode.Unauthorized, badNonceResponse.StatusCode);

        (string badSigState, string badSigNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("renamed@example.com", "subject-sig", badSigNonce, "Sig")) + "x");
        using HttpResponseMessage badSig = await CompleteAsync(fixture, "good-code", badSigState);
        Assert.Equal(HttpStatusCode.Unauthorized, badSig.StatusCode);

        (string originState, _) = await StartAsync(fixture, "google");
        using HttpResponseMessage origin = await PostExternalAsync(
            fixture,
            "/api/auth/external/complete",
            new { code = "good-code", state = originState },
            "https://attacker.example",
            null);
        Assert.Equal(HttpStatusCode.Unauthorized, origin.StatusCode);
        using HttpResponseMessage originReplay = await CompleteAsync(fixture, "good-code", originState);
        Assert.Equal(HttpStatusCode.Unauthorized, originReplay.StatusCode);

        (string raceState, string raceNonce) = await StartAsync(fixture, "google");
        identities.Use("good-code", identities.Sign(GoogleClaims("renamed@example.com", "subject-race", raceNonce, "Race")));
        Task<HttpResponseMessage>[] race = [CompleteAsync(fixture, "good-code", raceState), CompleteAsync(fixture, "good-code", raceState)];
        HttpResponseMessage[] raceResults = await Task.WhenAll(race);
        Assert.Equal(0, raceResults.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2, raceResults.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        foreach (HttpResponseMessage response in raceResults)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task ExternalLoginFirstAdminRaceCreatesOneAccount()
    {
        ScriptedIdentityClient identities = new();
        await using AuthApiFixture fixture = await CreateSocialFixtureAsync(identities, logs: null, includeMicrosoft: false);
        (string firstState, string firstNonce) = await StartAsync(fixture, "google");
        (string secondState, string secondNonce) = await StartAsync(fixture, "google");
        identities.Use("one", identities.Sign(GoogleClaims("owner@example.com", "subject-one", firstNonce, "One")));
        identities.Use("two", identities.Sign(GoogleClaims("owner@example.com", "subject-two", secondNonce, "Two")));
        string setupToken = fixture.SetupToken;
        Task<HttpResponseMessage> first = CompleteAsync(fixture, "one", firstState, setupToken: setupToken);
        Task<HttpResponseMessage> second = CompleteAsync(fixture, "two", secondState, setupToken: setupToken);
        HttpResponseMessage[] results = await Task.WhenAll(first, second);
        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        string winner = await results.Single(response => response.StatusCode == HttpStatusCode.OK).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument winnerPayload = JsonDocument.Parse(winner);
        string token = winnerPayload.RootElement.GetProperty("accessToken").GetString()!;
        using HttpResponseMessage users = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/settings/users", token);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        using JsonDocument usersPayload = JsonDocument.Parse(await users.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(usersPayload.RootElement.GetProperty("users").EnumerateArray());
        Assert.False(winnerPayload.RootElement.GetProperty("user").GetProperty("hasPassword").GetBoolean());
        Task<HttpResponseMessage> passwordOne = fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            token,
            new { currentPassword = string.Empty, newPassword = "Testing1" });
        Task<HttpResponseMessage> passwordTwo = fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            token,
            new { currentPassword = string.Empty, newPassword = "Testing22" });
        HttpResponseMessage[] passwordResults = await Task.WhenAll(passwordOne, passwordTwo);
        Assert.Equal(1, passwordResults.Count(response => response.StatusCode == HttpStatusCode.OK));
        foreach (HttpResponseMessage response in passwordResults)
        {
            response.Dispose();
        }

        foreach (HttpResponseMessage response in results)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task ExternalLoginStateDoesNotSurviveRestart()
    {
        ScriptedIdentityClient identities = new();
        AuthApiFixture first = await CreateSocialFixtureAsync(identities, logs: null);
        (string state, _) = await StartAsync(first, "google");
        await first.DisposeAsync();

        ScriptedIdentityClient restarted = new();
        await using AuthApiFixture second = await CreateSocialFixtureAsync(restarted, logs: null);
        restarted.Use("good-code", restarted.Sign(GoogleClaims("owner@example.com", "subject-one", "nonce", "One")));
        using HttpResponseMessage complete = await CompleteAsync(second, "good-code", state);
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    private static async Task<AuthApiFixture> CreateSocialFixtureAsync(
        ScriptedIdentityClient identities,
        ILoggerProvider? logs,
        bool includeMicrosoft = false)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Kautuka:Auth:AttemptLimit"] = "1000",
            ["Kautuka:ConnectorAuth:Host"] = "https://auth.kautuka.test",
            ["Kautuka:Auth:Google:ClientId"] = googleAudience,
            ["Kautuka:Auth:Microsoft:ClientId"] = includeMicrosoft ? microsoftAudience : string.Empty,
        };
        return await AuthApiFixture.CreateAsync(configuration, identities, logs);
    }

    private static async Task<(string State, string Nonce)> StartAsync(AuthApiFixture fixture, string provider, string intent = "login", string? bearer = null)
    {
        var headers = new Dictionary<string, string>();
        if (fixture.ExternalLoginCookie.Length > 0) headers["Cookie"] = fixture.ExternalLoginCookie;
        using HttpResponseMessage response = await PostExternalAsync(
            fixture,
            "/api/auth/external/start",
            new { provider, intent },
            applianceOrigin,
            bearer,
            headers);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            fixture.ExternalLoginSetCookie = setCookies.Single();
            fixture.ExternalLoginCookie = fixture.ExternalLoginSetCookie.Split(';')[0];
        }
        Assert.NotEmpty(fixture.ExternalLoginCookie);
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string startUrl = payload.RootElement.GetProperty("startUrl").GetString() ?? string.Empty;
        Assert.StartsWith("https://auth.kautuka.test/start#", startUrl, StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(new Uri(startUrl).Fragment.TrimStart('#'));
        Assert.Equal(provider == "google" ? "kautuka-login-google" : "kautuka-login-microsoft", query["connector"].ToString());
        Assert.Equal(applianceOrigin + "/login/callback", query["return"].ToString());
        return (query["state"].ToString(), query["nonce"].ToString());
    }

    private static Task<HttpResponseMessage> CompleteAsync(
        AuthApiFixture fixture,
        string code,
        string state,
        string? inviteCode = null,
        string? setupToken = null,
        string? bearer = null)
        => PostExternalAsync(fixture, "/api/auth/external/complete", new { code, state, brokerTicket = "test-broker-ticket", inviteCode, setupToken }, applianceOrigin, bearer,
            new Dictionary<string, string> { ["Cookie"] = fixture.ExternalLoginCookie });

    private static async Task AssertRejectedAsync(
        AuthApiFixture fixture,
        ScriptedIdentityClient identities,
        Dictionary<string, object?> claims)
    {
        (string state, string nonce) = await StartAsync(fixture, claims["iss"]?.ToString()?.Contains("microsoftonline", StringComparison.Ordinal) == true ? "microsoft" : "google");
        claims["nonce"] = nonce;
        identities.Use("good-code", identities.Sign(claims));
        using HttpResponseMessage response = await CompleteAsync(fixture, "good-code", state);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Sign-in was not accepted.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", body, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> GoogleClaims(
        string email,
        string subject,
        string nonce,
        string name,
        bool emailVerified = true,
        string issuer = "https://accounts.google.com",
        object? audience = null)
        => new()
        {
            ["iss"] = issuer,
            ["aud"] = audience ?? googleAudience,
            ["sub"] = subject,
            ["email"] = email,
            ["email_verified"] = emailVerified,
            ["nonce"] = nonce,
            ["name"] = name,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        };

    private const string personalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static Dictionary<string, object?> MicrosoftClaims(
        string email,
        string subject,
        string nonce,
        string tenant,
        string issuerPrefix = "https://login.microsoftonline.com/",
        string? objectId = null)
    {
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = issuerPrefix + tenant + (issuerPrefix.Contains("sts.windows.net", StringComparison.Ordinal) ? "/" : "/v2.0"),
            ["tid"] = tenant,
            ["aud"] = microsoftAudience,
            ["sub"] = subject,
            ["email"] = email,
            ["xms_edov"] = true,
            ["nonce"] = nonce,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        };
        if (objectId is not null) claims["oid"] = objectId;
        return claims;
    }

    private static async Task<HttpResponseMessage> PostExternalAsync(
        AuthApiFixture fixture,
        string path,
        object body,
        string? origin,
        string? bearer,
        Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class ScriptedIdentityClient : IExternalIdentityClient
    {
        private readonly RSA rsa = RSA.Create(2048);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> tokens = new(StringComparer.Ordinal);

        public (string Provider, string Code, string BrokerTicket, string ReturnOrigin)? LastExchange { get; private set; }

        public void Use(string code, string token) => tokens[code] = token;

        public Task<string?> ExchangeCodeAsync(string provider, string code, string verifier, string brokerTicket, string returnOrigin, CancellationToken cancellationToken)
        {
            LastExchange = (provider, code, brokerTicket, returnOrigin);
            return Task.FromResult(tokens.TryGetValue(code, out string? token) ? token : null);
        }

        public Task<RSA?> FindKeyAsync(string provider, string keyId, CancellationToken cancellationToken)
            => Task.FromResult<RSA?>(string.Equals(keyId, "login-test", StringComparison.Ordinal) ? rsa : null);

        public string Sign(IReadOnlyDictionary<string, object?> claims)
        {
            string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["alg"] = "RS256",
                ["kid"] = "login-test",
            }));
            string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
            byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return header + "." + payload + "." + Base64Url(signature);
        }

        private static string Base64Url(byte[] value)
            => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
