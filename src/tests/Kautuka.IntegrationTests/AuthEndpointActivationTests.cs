using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed partial class AuthEndpointActivationTests
{
    [Fact]
    public async Task NullLoginFieldsReturnBadRequest()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        await fixture.SendAnonymousAsync(HttpMethod.Post, "/api/auth/setup", new { username = "admin", password = "Testing1", setupToken = fixture.SetupToken });

        using HttpResponseMessage response = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = (string?)null, password = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using HttpRequestMessage missing = new(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage missingResponse = await fixture.Client.SendAsync(missing, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

        using HttpRequestMessage empty = new(HttpMethod.Post, "/api/auth/login");
        using HttpResponseMessage emptyResponse = await fixture.Client.SendAsync(empty, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);

        using HttpRequestMessage malformed = new(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage malformedResponse = await fixture.Client.SendAsync(malformed, TestContext.Current.CancellationToken);
        string malformedBody = await malformedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        Assert.DoesNotContain("Correlation ID", malformedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedHopRateLimitPartitionsForwardedClients()
    {
        await using var fixture = await AuthApiFixture.CreateAsync(
            new Dictionary<string, string?> { ["Kautuka:Auth:AttemptLimit"] = "1" },
            null,
            null);

        using HttpResponseMessage first = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "missing", password = "Testing1" },
            "203.0.113.8");
        using HttpResponseMessage second = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "missing", password = "Testing1" },
            "203.0.113.9");
        using HttpResponseMessage repeat = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "missing", password = "Testing1" },
            "203.0.113.8");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeat.StatusCode);
    }

    [Fact]
    public void DirectConnectionIgnoresForwardedClientAddress()
    {
        var trusted = new DefaultHttpContext();
        trusted.Connection.RemoteIpAddress = IPAddress.Loopback;
        trusted.Request.Headers["X-Forwarded-For"] = "203.0.113.8";
        Assert.Equal("203.0.113.8", AuthClientAddress.ResolvePartitionKey(trusted));

        var direct = new DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        direct.Request.Headers["X-Forwarded-For"] = "203.0.113.8";
        Assert.Equal("198.51.100.4", AuthClientAddress.ResolvePartitionKey(direct));
    }

    [Fact]
    public async Task LoginCookieIsSecureAndRefreshRotationRejectsReplay()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        await fixture.SendAnonymousAsync(HttpMethod.Post, "/api/auth/setup", new { username = "admin", password = "Testing1", setupToken = fixture.SetupToken });

        using HttpResponseMessage login = await fixture.SendAnonymousAsync(
            HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "Testing1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        if (fixture.Client.BaseAddress?.Scheme == Uri.UriSchemeHttps)
        {
            Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("SameSite=Lax", setCookie, StringComparison.OrdinalIgnoreCase);
        using JsonDocument loginPayload = JsonDocument.Parse(
            await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(
            checked((int)GatewayTokenStore.AccessTokenLifetime.TotalSeconds),
            loginPayload.RootElement.GetProperty("expiresInSeconds").GetInt32());
        string cookie = setCookie.Split(';')[0];

        using HttpResponseMessage refresh = await fixture.SendAnonymousWithCookieAsync(HttpMethod.Post, "/api/auth/refresh", cookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.NotEqual(setCookie, Assert.Single(refresh.Headers.GetValues("Set-Cookie")));
        using JsonDocument refreshPayload = JsonDocument.Parse(
            await refresh.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(
            checked((int)GatewayTokenStore.AccessTokenLifetime.TotalSeconds),
            refreshPayload.RootElement.GetProperty("expiresInSeconds").GetInt32());

        using HttpResponseMessage replay = await fixture.SendAnonymousWithCookieAsync(HttpMethod.Post, "/api/auth/refresh", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        AssertRefreshCookieCleared(replay);
    }

    [Fact]
    public async Task InvalidRefreshSessionClearsStaleBrowserCookie()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();

        using HttpResponseMessage response = await fixture.SendAnonymousWithCookieAsync(
            HttpMethod.Post,
            "/api/auth/refresh",
            "kautuka_refresh_token=stale-session-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertRefreshCookieCleared(response);
    }

    private static void AssertRefreshCookieCleared(HttpResponseMessage response)
    {
        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("kautuka_refresh_token=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/runtime/diagnostics/manager/containers")]
    [InlineData("/api/runtime/diagnostics/pkgs-proxy/stats")]
    public async Task ManagerDiagnosticsRequiresAdminPolicy(string path)
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string memberToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage anonymous = await fixture.SendAnonymousAsync(
            HttpMethod.Get, path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using HttpResponseMessage member = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get, path, memberToken);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
    }

    [Theory]
    [InlineData("/api/internal/channels/ingress")]
    [InlineData("/api/internal/channels/outbox/lease")]
    [InlineData("/api/internal/channels/outbox/ack")]
    [InlineData("/api/internal/channels/outbox/enqueue")]
    [InlineData("/api/internal/channels/interactions/interaction-1/transport")]
    public async Task ChannelInternalHttpRoutesAreNotMapped(string path)
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string memberToken = await fixture.LoginAsAsync("member-user", "Testing1");
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage member = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post, path, memberToken, new { });
        Assert.Equal(HttpStatusCode.NotFound, member.StatusCode);

        using HttpResponseMessage admin = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post, path, adminToken, new { });
        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
    }

    [Fact]
    public async Task UnhandledErrorsDoNotLeakInternalMessage()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get, "/api/security-tests/throw", adminToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("super-secret-path", body, StringComparison.Ordinal);
        Assert.Contains("Correlation ID", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InactiveUserLoginIsRejected()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("inactive-user", "Testing1", isActive: false);

        using HttpResponseMessage response = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "inactive-user", password = "Testing1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InactiveUserAccessTokenCannotAccessAuthenticatedEndpoints()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string userId = fixture.CreateUser("inactive-user", "Testing1", isActive: false);
        string accessToken = fixture.IssueAccessToken(userId);

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/auth/me", accessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanDeactivateUserViaSettingsEndpoint()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);

        string userId = fixture.GetUserId("member-user");

        using HttpResponseMessage deactivateResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/active",
            adminToken,
            new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, deactivateResponse.StatusCode);

        using HttpResponseMessage loginResponse = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "member-user", password = "Testing1" });

        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
    }

    [Fact]
    public async Task InvitingDuplicateUsernameIsRejectedAndDoesNotOverwriteExistingAdmin()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage inviteResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            adminToken,
            new
            {
                username = "admin",
                email = "duplicate@example.com",
                password = "Testing2",
                role = "user",
            });

        Assert.Equal(HttpStatusCode.BadRequest, inviteResponse.StatusCode);

        string inviteContent = await inviteResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument inviteJson = JsonDocument.Parse(inviteContent);
        Assert.Equal("Username already exists.", inviteJson.RootElement.GetProperty("detail").GetString());

        using HttpResponseMessage meResponse = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/auth/me", adminToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        string meContent = await meResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument meJson = JsonDocument.Parse(meContent);
        Assert.Equal("admin", meJson.RootElement.GetProperty("username").GetString());
        Assert.Equal("admin", meJson.RootElement.GetProperty("role").GetString());

        using HttpResponseMessage usersResponse = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/settings/users", adminToken);
        Assert.Equal(HttpStatusCode.OK, usersResponse.StatusCode);

        string usersContent = await usersResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument usersJson = JsonDocument.Parse(usersContent);
        Assert.Single(usersJson.RootElement.GetProperty("users").EnumerateArray());
    }

    [Fact]
    public async Task LastActiveAdminCannotBeDemoted()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        string adminUserId = fixture.GetUserId("admin");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            $"/api/settings/users/{adminUserId}/role",
            adminToken,
            new { isAdmin = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.Equal("At least one active admin must remain in the system.", json.RootElement.GetProperty("detail").GetString());

        using HttpResponseMessage meResponse = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/auth/me", adminToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task LastActiveAdminCannotBeDeactivated()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        string adminUserId = fixture.GetUserId("admin");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            $"/api/settings/users/{adminUserId}/active",
            adminToken,
            new { isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.Equal("At least one active admin must remain in the system.", json.RootElement.GetProperty("detail").GetString());

        using HttpResponseMessage meResponse = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/auth/me", adminToken);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserCanUpdateOwnProfile()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage updateResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/profile",
            accessToken,
            new { email = "member@example.com", displayName = "Member User" });

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        using HttpResponseMessage meResponse = await fixture.SendAuthenticatedAsync(HttpMethod.Get, "/api/auth/me", accessToken);
        string meContent = await meResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument meJson = JsonDocument.Parse(meContent);

        Assert.Equal("member@example.com", meJson.RootElement.GetProperty("email").GetString());
        Assert.Equal("Member User", meJson.RootElement.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData(null, "Display name is required.")]
    [InlineData("Member User", "Enter a valid email address.")]
    public async Task ProfileUpdateReturnsSpecificValidationErrors(string? displayName, string expectedError)
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage updateResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/profile",
            accessToken,
            new { email = displayName is null ? "member@example.com" : "bad-email", displayName });

        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);
        using JsonDocument responseJson = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedError, responseJson.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AuthenticatedUserCanChangeOwnPassword()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage passwordResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/password",
            accessToken,
            new { currentPassword = "Testing1", newPassword = "Testing2" });

        Assert.Equal(HttpStatusCode.OK, passwordResponse.StatusCode);

        using HttpResponseMessage oldLoginResponse = await fixture.SendAnonymousAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "member-user", password = "Testing1" });

        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResponse.StatusCode);

        string newAccessToken = await fixture.LoginAsAsync("member-user", "Testing2");
        Assert.False(string.IsNullOrWhiteSpace(newAccessToken));
    }

    [Fact]
    public async Task AuthenticatedUserCanReadOwnChannelSettingsEndpoint()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/me/channels",
            accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using JsonDocument json = JsonDocument.Parse(content);

        Assert.Equal("member-user", json.RootElement.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public async Task OwnChannelCatalogRefreshUsesTheAuthenticatedOwnerForAttachmentRead()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string ownerUserId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.ChannelHost.AddAttachment("telegram:" + ownerUserId + ":default", ownerUserId, "telegram");
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/me/channels",
            accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ownerUserId, fixture.ChannelHost.GetAttachmentActors);
        Assert.DoesNotContain("gateway-system", fixture.ChannelHost.GetAttachmentActors, StringComparer.Ordinal);
    }

    [Fact]
    public async Task OwnChannelCatalogRefreshHydratesOnlyActiveAttachmentCredentials()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string ownerUserId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        const string activeAttachmentId = "telegram-active";
        const string inactiveAttachmentId = "telegram-inactive";
        fixture.ChannelHost.AddAttachment(activeAttachmentId, ownerUserId, "telegram", AttachmentStatus.Active, includeCredentialReference: true);
        fixture.ChannelHost.AddAttachment(inactiveAttachmentId, ownerUserId, "telegram", AttachmentStatus.Inactive, includeCredentialReference: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/me/channels",
            accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(activeAttachmentId, fixture.ChannelHost.RuntimeCredentialAttachmentIds);
        Assert.DoesNotContain(inactiveAttachmentId, fixture.ChannelHost.RuntimeCredentialAttachmentIds);
    }

    [Fact]
    public async Task AuthenticatedUserCanRequestPermanentChannelRemoval()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string ownerUserId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.ChannelHost.AddAttachment("telegram:" + ownerUserId + ":default", ownerUserId, "telegram", AttachmentStatus.Active);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/me/channels/telegram/lifecycle",
            accessToken,
            new { transition = "remove", reason = "untrusted browser reason" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("untrusted browser reason", content, StringComparison.Ordinal);
        Assert.DoesNotContain("suspend and recover only", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedUserCannotRequestUnknownChannelLifecycleTransition()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/settings/me/channels/telegram/lifecycle",
            accessToken,
            new { transition = "explode", reason = "untrusted browser reason" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("suspend, recover, and remove only", content, StringComparison.Ordinal);
        Assert.DoesNotContain("untrusted browser reason", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnChannelUpdateRequiresVisibleRecoveryInsteadOfReturningInternalError()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string ownerUserId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.ChannelHost.AddAttachment("telegram:" + ownerUserId + ":default", ownerUserId, "telegram", AttachmentStatus.RecoveryRequired);
        fixture.ChannelHost.RequireExplicitRecoveryOnUpsert = true;
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/me/channels/telegram",
            accessToken,
            new { channel_device_id = "100001", bot_token = "not-a-real-secret" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("recovery_required", json.RootElement.GetProperty("channelSetup").GetProperty("status").GetString());
        Assert.Equal("RECOVERY_REQUIRED", json.RootElement.GetProperty("channels")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task OwnChannelUpdateReturnsTypedPayloadWhenAnyProviderRejectsSettings()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.ChannelHost.RejectedSetupCode = "telegram_setup_rejected";
        fixture.ChannelHost.RejectedSetupMessage = "Telegram settings were rejected.";
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put, "/api/settings/me/channels/telegram", accessToken,
            new { channel_device_id = "100001", bot_token = "not-a-real-secret" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement setup = json.RootElement.GetProperty("channelSetup");
        Assert.False(setup.GetProperty("success").GetBoolean());
        Assert.Equal("telegram_setup_rejected", setup.GetProperty("status").GetString());
        Assert.Equal("Telegram settings were rejected.", setup.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AdminChannelUpdateReturnsTypedPayloadForNonSlackRejection()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string targetUserId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.SetRole("member-user", "admin");
        fixture.ChannelHost.RejectedSetupCode = "telegram_setup_rejected";
        fixture.ChannelHost.RejectedSetupMessage = "Telegram settings were rejected.";
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage response = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put, $"/api/settings/users/{targetUserId}/channels/telegram", accessToken,
            new { channel_device_id = "100001", bot_token = "not-a-real-secret" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("telegram_setup_rejected", json.RootElement.GetProperty("channelSetup").GetProperty("status").GetString());
    }

    [Fact]
    public async Task AuthenticatedUserCanReadAgentAuthoringMetadataAndManageOwnAgent()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        fixture.CreateModel("provider-1", "model-1", "llama-3.1-8b");
        fixture.CreateUser("member-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage systemResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/system",
            accessToken);
        Assert.Equal(HttpStatusCode.Forbidden, systemResponse.StatusCode);

        using HttpResponseMessage limitsResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/runtime-limits",
            accessToken);
        Assert.Equal(HttpStatusCode.OK, limitsResponse.StatusCode);
        using JsonDocument limitsJson = JsonDocument.Parse(
            await limitsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Number, limitsJson.RootElement.GetProperty("maxRunDurationSeconds").ValueKind);
        Assert.False(limitsJson.RootElement.TryGetProperty("logLevel", out _));
        Assert.False(limitsJson.RootElement.TryGetProperty("workspace", out _));

        using HttpResponseMessage modelsResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/models",
            accessToken);
        Assert.Equal(HttpStatusCode.OK, modelsResponse.StatusCode);

        using JsonDocument modelsJson = JsonDocument.Parse(
            await modelsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(modelsJson.RootElement.GetProperty("models").EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), "model-1", StringComparison.Ordinal));

        using HttpResponseMessage personaResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/persona/definitions",
            accessToken);
        Assert.Equal(HttpStatusCode.OK, personaResponse.StatusCode);

        using JsonDocument personaJson = JsonDocument.Parse(
            await personaResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(personaJson.RootElement.TryGetProperty("definitions", out _));

        using HttpResponseMessage organizationResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/organization",
            accessToken);
        Assert.Equal(HttpStatusCode.OK, organizationResponse.StatusCode);

        using JsonDocument organizationJson = JsonDocument.Parse(
            await organizationResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(organizationJson.RootElement.TryGetProperty("organization", out _));

        using HttpResponseMessage saveAgentResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-member",
            accessToken,
            new
            {
                name = "Member Agent",
                modelId = "model-1",
                systemPrompt = "",
                superExecMode = "disabled",
                skillAttachments = Array.Empty<object>(),
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = true,
                    summaryModelId = (string?)null,
                },
                sandbox = new
                {
                    enabled = false,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "512m",
                    maxTimeout = 30,
                },
            });
        Assert.Equal(HttpStatusCode.OK, saveAgentResponse.StatusCode);

        using HttpResponseMessage agentsResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/settings/agents",
            accessToken);
        Assert.Equal(HttpStatusCode.OK, agentsResponse.StatusCode);

        using JsonDocument agentsJson = JsonDocument.Parse(
            await agentsResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(agentsJson.RootElement.GetProperty("agents").EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), "agent-member", StringComparison.Ordinal));

        using HttpResponseMessage deleteResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Delete,
            "/api/settings/agents/agent-member",
            accessToken);
        string deleteContent = await deleteResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            deleteResponse.StatusCode == HttpStatusCode.OK,
            $"Expected agent deletion to return OK, received {deleteResponse.StatusCode}: {deleteContent}");
    }

    [Fact]
    public async Task AuthenticatedUserCanCreateAndListSessionsForOwnedAgentWithoutChatCapability()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string userId = fixture.CreateUser("member-user", "Testing1", isActive: true);
        fixture.CreateOwnedAgent("member-user-agent", userId, "Member Agent");
        string accessToken = await fixture.LoginAsAsync("member-user", "Testing1");

        using HttpResponseMessage createResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/sessions",
            accessToken,
            new { targetType = "agent", targetId = "member-user-agent" });

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        using JsonDocument createJson = JsonDocument.Parse(
            await createResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(userId, createJson.RootElement.GetProperty("ownerUserId").GetString());

        using HttpResponseMessage listResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Get,
            "/api/sessions/agents/member-user-agent",
            accessToken);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using JsonDocument listJson = JsonDocument.Parse(
            await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains(listJson.RootElement.GetProperty("sessions").EnumerateArray(), item =>
            string.Equals(item.GetProperty("targetId").GetString(), "member-user-agent", StringComparison.Ordinal)
            && string.Equals(item.GetProperty("ownerUserId").GetString(), userId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CaseVariantAgentTargetCannotWriteAnotherUsersWorkspace()
    {
        await using var fixture = await AuthApiFixture.CreateAsync();
        string ownerId = fixture.CreateUser("owner-user", "Testing1", isActive: true);
        fixture.CreateOwnedAgent("private-agent", ownerId, "Private Agent");
        string otherId = fixture.CreateUser("other-user", "Testing1", isActive: true);
        string accessToken = await fixture.LoginAsAsync("other-user", "Testing1");

        using HttpResponseMessage createResponse = await fixture.SendAuthenticatedAsync(
            HttpMethod.Post,
            "/api/sessions",
            accessToken,
            new { targetType = "Agent", targetId = "private-agent" });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);

        string sessionId = fixture.CreateSession("Agent", "private-agent", otherId);
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent("planted"u8.ToArray()), "file", "planted.txt");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/assets")
        {
            Content = upload,
        };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage uploadResponse = await fixture.Client.SendAsync(uploadRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, uploadResponse.StatusCode);
    }

    private sealed class AuthApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;
        private readonly FakeChannelHostGrpcService channelHost;

        private AuthApiFixture(
            string root,
            string? previousEnvironment,
            string? previousBootstrapPassword,
            WebApplication app,
            FakeChannelHostGrpcService channelHost)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.previousBootstrapPassword = previousBootstrapPassword;
            this.app = app;
            this.channelHost = channelHost;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public string ExternalLoginCookie { get; set; } = string.Empty;
        public string? ExternalLoginSetCookie { get; set; }

        public void ExpireInviteCode(string code)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
            using var connection = new SqliteConnection(app.Services.GetRequiredService<KautukaDatabase>().ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE external_login_invites SET expires_at=$expiresAt WHERE code_hash=$hash;";
            command.Parameters.AddWithValue("$expiresAt", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$hash", hash);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public string SetupToken => app.Services.GetRequiredService<SetupPossession>().Peek();
        public FakeChannelHostGrpcService ChannelHost => channelHost;
        public bool IsStopping => app.Lifetime.ApplicationStopping.IsCancellationRequested;

        public static Task<AuthApiFixture> CreateAsync() => CreateAsync(null, null, null);

        public static async Task<AuthApiFixture> CreateAsync(
            Dictionary<string, string?>? configuration,
            IExternalIdentityClient? identities,
            ILoggerProvider? logs,
            Action<IServiceCollection>? configureServices = null,
            IPAddress? remoteAddress = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-auth-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string sqlitePath = Path.Combine(root, "gateway.db");
            string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            string? previousBootstrapPassword = Environment.GetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
            Environment.SetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD", "admin");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });

            builder.Logging.ClearProviders();
            if (logs is not null)
            {
                builder.Logging.AddProvider(logs);
            }

            var settings = new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            };
            if (configuration is not null)
            {
                foreach (KeyValuePair<string, string?> entry in configuration)
                {
                    settings[entry.Key] = entry.Value;
                }
            }

            builder.Configuration.AddInMemoryCollection(settings);

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            builder.Services.AddSingleton(new CredentialResolverService(
                new Lazy<CredentialsService.CredentialsServiceClient>(new FakeCredentialsServiceClient())));
            var channelHost = new FakeChannelHostGrpcService();
            builder.Services.AddSingleton<IChannelHostGrpcService>(channelHost);
            if (identities is not null)
            {
                builder.Services.AddSingleton(identities);
            }
            configureServices?.Invoke(builder.Services);

            WebApplication app = builder.Build();
            if (remoteAddress is not null)
            {
                app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = remoteAddress;
                    return next(context);
                });
            }
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            app.MapGet("/api/security-tests/throw", static () => ThrowSecurityTestException());
            await app.StartAsync();

            return new AuthApiFixture(root, previousEnvironment, previousBootstrapPassword, app, channelHost);
        }

        public string CreateUser(string username, string password, bool isActive)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            string userId = $"user-{Guid.NewGuid():N}";
            state.Users[username] = new GatewayUser(
                userId,
                username,
                GatewayPasswordHasher.Hash(password),
                "user",
                isActive,
                null,
                null);
            return userId;
        }

        public void SetRole(string username, string role)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            state.Users[username] = state.Users[username] with { Role = role };
        }

        public string GetUserId(string username)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            if (!state.Users.TryGetValue(username, out GatewayUser? user) || user is null)
            {
                throw new XunitException($"User '{username}' was not found.");
            }

            return user.Id;
        }

        public void CreateModel(string providerId, string modelId, string modelName)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            string now = DateTimeOffset.UtcNow.ToString("O");
            state.Providers[providerId] = new GatewayProvider(
                providerId,
                providerId,
                "groq",
                new JsonObject(),
                now);
            state.Models[modelId] = new GatewayModel(
                modelId,
                providerId,
                modelName,
                new JsonObject(),
                null,
                now);
        }

        public string CreateSession(string targetType, string targetId, string ownerUserId)
        {
            IGatewaySessionStore sessions = app.Services.GetRequiredService<IGatewaySessionStore>();
            return sessions.CreateSession(targetType, targetId, ownerUserId).Id;
        }

        public void CreateOwnedAgent(string agentId, string ownerUserId, string name)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            IAgentDefinitionStore agentDefinitionStore = app.Services.GetRequiredService<IAgentDefinitionStore>();
            string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
            state.Agents[agentId] = new GatewayAgent(
                agentId,
                name,
                "model-local",
                string.Empty,
                "disabled",
                new JsonObject(),
                new JsonObject(),
                new JsonObject(),
                [],
                ownerUserId,
                DateTimeOffset.UtcNow.ToString("O"));
            state.AgentStorageLocators[agentId] = storageLocatorKey;
            agentDefinitionStore.SaveDefinition(storageLocatorKey, new AgentDefinition
            {
                Id = agentId,
                Scope = GatewayOwnershipScopes.User,
                Enabled = true,
                Name = name,
                ModelId = "model-local",
                SystemPrompt = string.Empty,
                SuperExecMode = "disabled",
            });
        }

        public async Task<string> LoginAsBootstrapAdminAsync()
        {
            await SendAnonymousAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() });

            return await LoginAsAsync("admin", "Testing1");
        }

        public async Task<string> LoginAsAsync(string username, string password)
        {
            using HttpResponseMessage loginResponse = await SendAnonymousAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new { username, password });

            string content = await loginResponse.Content.ReadAsStringAsync();
            if (!loginResponse.IsSuccessStatusCode)
            {
                throw new XunitException($"Login failed with {(int)loginResponse.StatusCode}: {content}");
            }

            using JsonDocument json = JsonDocument.Parse(content);
            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public string IssueAccessToken(string userId)
            => app.Services.GetRequiredService<IGatewayTokenStore>().IssueAccessToken(userId);

        public Task<HttpResponseMessage> SendAnonymousAsync(HttpMethod method, string path, object? body)
            => SendAnonymousAsync(method, path, body, null);

        public async Task<HttpResponseMessage> SendAnonymousAsync(HttpMethod method, string path, object? body, string? forwardedFor)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            }

            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendAnonymousWithCookieAsync(HttpMethod method, string path, string cookie)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", cookie);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path, string accessToken, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD", previousBootstrapPassword);
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);

            if (Directory.Exists(root))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
            }
        }
    }

    private static IResult ThrowSecurityTestException() =>
        throw new InvalidOperationException("super-secret-path");

    private sealed class FakeChannelHostGrpcService : IChannelHostGrpcService
    {
        private readonly Lock sync = new();
        private readonly Dictionary<string, GetAttachmentResponse> attachments = new(StringComparer.Ordinal);
        private readonly List<string> getAttachmentActors = [];
        private readonly List<string> runtimeCredentialAttachmentIds = [];

        public bool RequireExplicitRecoveryOnUpsert { get; set; }
        public string? RejectedSetupCode { get; set; }
        public string? RejectedSetupMessage { get; set; }

        public IReadOnlyList<string> GetAttachmentActors
        {
            get
            {
                lock (sync)
                {
                    return [.. getAttachmentActors];
                }
            }
        }

        public IReadOnlyList<string> RuntimeCredentialAttachmentIds
        {
            get
            {
                lock (sync)
                {
                    return [.. runtimeCredentialAttachmentIds];
                }
            }
        }

        public void AddAttachment(
            string attachmentId,
            string ownerUserId,
            string providerKey,
            AttachmentStatus status = AttachmentStatus.Active,
            bool includeCredentialReference = false)
        {
            GetAttachmentResponse response = new()
            {
                Attachment = new AttachmentSummary
                {
                    AttachmentId = attachmentId,
                    ProviderKey = providerKey,
                    DisplayName = providerKey,
                    Status = status,
                    SchemaVersion = providerKey + "/v1",
                    CapabilityVersion = providerKey + "/v1",
                },
            };
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = ownerUserId });
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "display_name", StringValue = providerKey });
            if (includeCredentialReference)
            {
                response.CredentialReferences.Add(new AttachmentCredentialReference
                {
                    FieldKey = "bot_token",
                    CredentialKey = "channel." + attachmentId + ".bot_token",
                });
            }
            lock (sync)
            {
                attachments[attachmentId] = response;
            }
        }

        public ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChannelHostPingResponse
            {
                Providers =
                {
                    new ChannelProviderHealth { ProviderKey = "telegram", DisplayName = "Telegram", Status = "not_configured", Detail = "test" },
                    new ChannelProviderHealth { ProviderKey = "slack", DisplayName = "Slack", Status = "not_configured", Detail = "test" },
                }
            });

        public ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
        {
            ListChannelProvidersResponse response = new();
            response.Providers.AddRange(
            [
                BuildProvider("telegram", "Telegram",
                [
                    BuildField("display_name", "Display name", AttachmentSchemaFieldType.Text),
                    BuildField("owner_user_id", "Owner user id", AttachmentSchemaFieldType.ReadonlyText),
                    BuildField("channel_device_id", "Conversation id", AttachmentSchemaFieldType.Text, required: true),
                    BuildField("bot_token", "Bot token", AttachmentSchemaFieldType.SecretText, required: true, secret: true),
                    BuildField("hitl_tether_mode", "Channel sync mode", AttachmentSchemaFieldType.EnumSingle, enumValues: ["hitl", "mirror"]),
                ]),
                BuildProvider("slack", "Slack",
                [
                    BuildField("display_name", "Display name", AttachmentSchemaFieldType.Text),
                    BuildField("owner_user_id", "Owner user id", AttachmentSchemaFieldType.ReadonlyText),
                    BuildField("bot_token", "Bot token", AttachmentSchemaFieldType.SecretText, required: true, secret: true),
                    BuildField("app_token", "App token", AttachmentSchemaFieldType.SecretText, required: true, secret: true),
                    BuildField("hitl_tether_mode", "Channel sync mode", AttachmentSchemaFieldType.EnumSingle, enumValues: ["hitl", "mirror"]),
                ]),
            ]);

            return ValueTask.FromResult(response);
        }

        public ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentsResponse response = new();
            lock (sync)
            {
                foreach (GetAttachmentResponse attachment in attachments.Values.Where(attachment =>
                             attachment.Settings.Any(setting => setting.FieldKey == "owner_user_id" && setting.StringValue == request.OwnerUserId)))
                {
                    response.Attachments.Add(attachment.Attachment);
                }
            }

            return ValueTask.FromResult(response);
        }

        public ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                getAttachmentActors.Add(request.Context?.ActorUserId ?? string.Empty);
                if (attachments.TryGetValue(request.AttachmentId, out GetAttachmentResponse? response))
                {
                    return ValueTask.FromResult(response);
                }
            }

            throw new RpcException(new Status(StatusCode.NotFound, "Attachment not found."));
        }

        public ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new GetAttachmentSchemaResponse
            {
                AttachmentId = request.AttachmentId,
                ProviderKey = "telegram",
                SchemaVersion = "telegram/v1",
                CapabilityVersion = "telegram/v1",
            });

        public ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            if (RequireExplicitRecoveryOnUpsert)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "attachment_requires_explicit_recovery"));
            }

            if (RejectedSetupCode is not null)
            {
                UpsertAttachmentResponse rejected = new() { ValidationStatus = ValidationStatus.Rejected };
                rejected.ValidationIssues.Add(new ValidationIssue { Code = RejectedSetupCode, Message = RejectedSetupMessage ?? "Setup rejected." });
                return ValueTask.FromResult(rejected);
            }

            return ValueTask.FromResult(new UpsertAttachmentResponse
            {
                Attachment = new AttachmentSummary
                {
                    AttachmentId = request.AttachmentId,
                    ProviderKey = request.ProviderKey,
                    Status = AttachmentStatus.Active,
                },
                ValidationStatus = ValidationStatus.Accepted,
            });
        }

        public ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SyncOwnerTargetsResponse { Accepted = true, Detail = "noop" });

        public ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ProvisionAttachmentResponse
            {
                AttachmentId = request.AttachmentId,
                Status = ProvisioningStatus.Accepted,
                Detail = "noop",
                OperationId = request.Context?.RequestId ?? string.Empty,
            });

        public ValueTask<TransitionAttachmentLifecycleResponse> TransitionAttachmentLifecycleAsync(
            TransitionAttachmentLifecycleRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                if (!attachments.TryGetValue(request.AttachmentId, out GetAttachmentResponse? existing))
                {
                    return ValueTask.FromResult(new TransitionAttachmentLifecycleResponse
                    {
                        Accepted = false,
                        Status = AttachmentStatus.Unspecified,
                        Detail = "not_found",
                    });
                }

                string transition = request.Transition.Trim().ToUpperInvariant();
                AttachmentStatus current = existing.Attachment.Status;
                AttachmentStatus next = transition switch
                {
                    "SUSPEND" when current is AttachmentStatus.Active or AttachmentStatus.Provisioning or AttachmentStatus.Error
                        => AttachmentStatus.Suspended,
                    "RECOVER" when current is AttachmentStatus.Suspended or AttachmentStatus.Error
                        or AttachmentStatus.RecoveryRequired or AttachmentStatus.Deleted
                        => AttachmentStatus.Inactive,
                    "DELETE" when current is AttachmentStatus.Inactive or AttachmentStatus.Suspended
                        or AttachmentStatus.RecoveryRequired
                        => AttachmentStatus.Deleted,
                    _ => current,
                };
                bool accepted = next != current || transition is "DELETE" && current == AttachmentStatus.Deleted;
                if (accepted && next != current)
                {
                    existing.Attachment.Status = next;
                    attachments[request.AttachmentId] = existing;
                }

                return ValueTask.FromResult(new TransitionAttachmentLifecycleResponse
                {
                    Accepted = accepted || (transition is "DELETE" && current == AttachmentStatus.Deleted),
                    Status = existing.Attachment.Status,
                    Detail = accepted ? request.Reason : "lifecycle_transition_not_supported",
                });
            }
        }

        public ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new DeactivateAttachmentResponse
            {
                AttachmentId = request.AttachmentId,
                Status = AttachmentStatus.Inactive,
                Detail = request.Reason,
            });

        public ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SendOutboundMessageResponse
            {
                Accepted = true,
                DeliveryStatus = DeliveryStatus.Delivered,
                Detail = "noop",
                TransportMessageId = request.MessageId,
            });

        public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AcknowledgeIngressResponse { Acknowledged = true });

        public ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                runtimeCredentialAttachmentIds.Add(request.AttachmentId);
            }

            return ValueTask.FromResult(new SetRuntimeCredentialsResponse { Accepted = true });
        }

        public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        private static ChannelProviderDefinition BuildProvider(string providerKey, string displayName, IEnumerable<AttachmentSchemaField> fields)
        {
            ChannelProviderDefinition provider = new()
            {
                ProviderKey = providerKey,
                DisplayName = displayName,
                SchemaVersion = $"{providerKey}/v1",
                CapabilityVersion = $"{providerKey}/v1",
            };
            provider.Fields.AddRange(fields);
            return provider;
        }

        private static AttachmentSchemaField BuildField(
            string fieldKey,
            string label,
            AttachmentSchemaFieldType fieldType,
            bool required = false,
            bool secret = false,
            IEnumerable<string>? enumValues = null)
        {
            AttachmentSchemaField field = new()
            {
                FieldKey = fieldKey,
                Label = label,
                FieldType = fieldType,
                Required = required,
                Secret = secret,
            };
            if (enumValues is not null)
            {
                field.EnumValues.AddRange(enumValues);
            }

            return field;
        }
    }
}
