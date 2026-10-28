using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorValidationEndpointTests
{
    [Fact]
    public void ConnectorOAuthSessionsPruneExpiredEntriesBeforeAcceptingNewOnes()
    {
        var sessions = new ConnectorOAuthSessionStore();
        Assert.True(sessions.Save("expired", new ConnectorOAuthSession
        {
            UserId = "owner",
            ConnectorId = "kautuka-google-mail",
            CodeVerifier = "verifier",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }));

        Assert.True(sessions.Save("live", new ConnectorOAuthSession
        {
            UserId = "owner",
            ConnectorId = "kautuka-google-mail",
            CodeVerifier = "verifier",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        }));

        Assert.False(sessions.TryGet("expired", out _));
        Assert.True(sessions.TryGet("live", out _));
    }

    [Fact]
    public void ConnectorOAuthSessionStoreRejectsNewSessionsAtItsHardLimit()
    {
        var sessions = new ConnectorOAuthSessionStore();
        for (int i = 0; i < 4096; i++)
        {
            Assert.True(sessions.Save("state-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), new ConnectorOAuthSession
            {
                UserId = "owner",
                ConnectorId = "kautuka-google-mail",
                CodeVerifier = "verifier",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            }));
        }

        Assert.False(sessions.Save("overflow", new ConnectorOAuthSession
        {
            UserId = "owner",
            ConnectorId = "kautuka-google-mail",
            CodeVerifier = "verifier",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }));
    }

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public async Task ConnectorValidationEndpointPassesTestEnvPayloadToService()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                testEnv = new
                {
                    MCP_AUTH_TOKEN = "secret-token",
                },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("secret-token", response.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ConnectorValidationEndpointMergesSavedConnectorCredentialsWhenIdProvided()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/homeassistant-mcp",
            new
            {
                name = "Home Assistant",
                description = "Test connector",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
                enabled = true,
            },
            accessToken).ConfigureAwait(true);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/homeassistant-mcp",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "saved-token",
                },
            },
            accessToken).ConfigureAwait(true);

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "homeassistant-mcp",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("saved-token", response.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void UnsavedProbeRefusesPrivateDestinationsUnlessAllowed()
    {
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://127.0.0.1/mcp", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://localhost/mcp", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://10.1.2.3/mcp", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://[::ffff:169.254.169.254]/latest", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://[::ffff:127.0.0.1]/", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://100.64.0.1/mcp", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://100.127.255.254/mcp", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://[::ffff:100.64.0.1]/", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("http://[::]/", allowPrivate: false));
        Assert.False(ProbeNetworkPolicy.IsAllowed("https://probe.invalid/mcp", allowPrivate: false));
        Assert.True(ProbeNetworkPolicy.IsAllowed("http://127.0.0.1/mcp", allowPrivate: true));
        Assert.True(ProbeNetworkPolicy.IsAllowed("http://100.64.0.1/mcp", allowPrivate: true));
        Assert.True(ProbeNetworkPolicy.IsAllowed("http://100.128.0.1/mcp", allowPrivate: false));
        Assert.True(ProbeNetworkPolicy.IsAllowed("https://1.1.1.1/mcp", allowPrivate: false));
    }

    [Fact]
    public async Task RuntimeConnectorClientBlocksPrivateConnectAndStillFollowsRedirect()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using SocketsHttpHandler blocked = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivate: false);
        Assert.True(blocked.AllowAutoRedirect);
        using var blockedClient = new HttpClient(blocked) { Timeout = TimeSpan.FromSeconds(5) };
        HttpRequestException denied = await Assert.ThrowsAsync<HttpRequestException>(() =>
            blockedClient.GetAsync(new Uri("http://127.0.0.1:9/mcp"), cancellationToken));
        Assert.Contains("not allowed", denied.Message, StringComparison.Ordinal);

        var target = new TcpListener(IPAddress.Loopback, 0);
        var redirector = new TcpListener(IPAddress.Loopback, 0);
        Task? targetAccepted = null;
        Task? redirectAccepted = null;
        try
        {
            target.Start();
            redirector.Start();
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            int redirectPort = ((IPEndPoint)redirector.LocalEndpoint).Port;
#pragma warning disable CA2025 // Both accept tasks are awaited before the listeners are stopped.
            targetAccepted = AcceptOnceAsync(
                target,
                "HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nlanded",
                cancellationToken);
            redirectAccepted = AcceptOnceAsync(
                redirector,
                $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                cancellationToken);
#pragma warning restore CA2025

            using SocketsHttpHandler allowed = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivate: true);
            using var allowedClient = new HttpClient(allowed) { Timeout = TimeSpan.FromSeconds(5) };
            string body = await allowedClient.GetStringAsync(new Uri($"http://127.0.0.1:{redirectPort}/"), cancellationToken).ConfigureAwait(true);
            Assert.Equal("landed", body);
        }
        finally
        {
            if (targetAccepted is not null && redirectAccepted is not null)
            {
                await Task.WhenAll(targetAccepted, redirectAccepted).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            }

            target.Stop();
            redirector.Stop();
            target.Dispose();
            redirector.Dispose();
        }
    }

    private static async Task AcceptOnceAsync(TcpListener listener, string response, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(true);
        NetworkStream stream = client.GetStream();
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(true);
        byte[] payload = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public void ProbeTimeoutIsCappedAtThirtySeconds()
    {
        Assert.Equal(30, McpConnectorTestService.ClampProbeTimeoutSeconds(null));
        Assert.Equal(30, McpConnectorTestService.ClampProbeTimeoutSeconds(int.MaxValue));
        Assert.Equal(5, McpConnectorTestService.ClampProbeTimeoutSeconds(1));
        Assert.Equal(15, McpConnectorTestService.ClampProbeTimeoutSeconds(15));
    }

    [Fact]
    public async Task ConnectorValidationDoesNotSendStoredSecretsToACallerChosenUrl()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/homeassistant-mcp",
            new
            {
                name = "Home Assistant",
                description = "Test connector",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
                enabled = true,
            },
            accessToken).ConfigureAwait(true);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/homeassistant-mcp",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "saved-token",
                },
            },
            accessToken).ConfigureAwait(true);

        using HttpRequestMessage probe = new(HttpMethod.Post, "/api/connectors/test")
        {
            Content = JsonContent.Create(new
            {
                id = "homeassistant-mcp",
                transport = "http",
                httpUrl = "https://attacker.example/collect",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
            }, options: jsonOptions),
        };
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(probe, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("saved-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserScopedConnectorTestDoesNotReceiveSystemCredentials()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/system",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "system-token",
                },
            },
            adminToken).ConfigureAwait(true);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/shared-http",
            new
            {
                name = "Shared",
                description = "System connector",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
                enabled = true,
            },
            adminToken).ConfigureAwait(true);
        using JsonDocument systemProbe = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "shared-http",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
            },
            adminToken).ConfigureAwait(true);
        Assert.True(systemProbe.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("system-token", fixture.GetCapturedTestRequest().TestEnv!["MCP_AUTH_TOKEN"]);
        Assert.True(fixture.GetCapturedTestRequest().AllowPrivateDestination);
        (string systemScope, string? systemOwner) = fixture.ReadStoredConnectorOwnership("shared-http");
        Assert.Equal("system", systemScope);
        Assert.Null(systemOwner);

        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "connector-owner",
                email = "connector-owner@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new XunitException("Invite response did not include a user id.");
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/import-permissions",
            new
            {
                canImportConnectors = true,
                canImportSkills = false,
            },
            adminToken).ConfigureAwait(true);
        string memberToken = await fixture.LoginAsAsync("connector-owner", "Testing1").ConfigureAwait(true);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/member-http",
            new
            {
                name = "Member",
                description = "User connector",
                transport = "http",
                httpUrl = "https://member.example/mcp",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
                enabled = true,
            },
            memberToken).ConfigureAwait(true);
        (string memberScope, string? memberOwner) = fixture.ReadStoredConnectorOwnership("member-http");
        Assert.Equal("user", memberScope);
        Assert.Equal(userId, memberOwner);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/member-http",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "user-token",
                },
            },
            memberToken).ConfigureAwait(true);

        using JsonDocument memberProbe = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "member-http",
                transport = "http",
                httpUrl = "https://member.example/mcp",
            },
            memberToken).ConfigureAwait(true);
        Assert.True(memberProbe.RootElement.GetProperty("success").GetBoolean());
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("user-token", captured.TestEnv!["MCP_AUTH_TOKEN"]);
        Assert.False(captured.TestEnv.ContainsValue("system-token"));
        Assert.False(captured.AllowPrivateDestination);
    }

    [Fact]
    public async Task NonAdminCannotSaveAPrivateConnectorDestination()
    {
        await using ConnectorApiFixture fixture = await ConnectorApiFixture.CreateAsync().ConfigureAwait(true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync().ConfigureAwait(true);
        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "private-importer",
                email = "private-importer@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new XunitException("Invite response did not include a user id.");
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/import-permissions",
            new { canImportConnectors = true, canImportSkills = false },
            adminToken).ConfigureAwait(true);
        string memberToken = await fixture.LoginAsAsync("private-importer", "Testing1").ConfigureAwait(true);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/connectors/private-http")
        {
            Content = JsonContent.Create(new
            {
                name = "Private",
                description = "Private destination",
                transport = "http",
                httpUrl = "http://10.1.2.3/mcp",
                enabled = true,
            }, options: jsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            fixture.ReadStoredConnectorIds(),
            id => string.Equals(id, "private-http", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonAdminCannotPointAConnectorOverrideAtAPrivateDestination()
    {
        await using ConnectorApiFixture fixture = await ConnectorApiFixture.CreateAsync().ConfigureAwait(true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync().ConfigureAwait(true);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/shared-http",
            new
            {
                name = "Shared",
                description = "Shared HTTP connector",
                transport = "http",
                httpUrl = "https://shared.example/mcp",
                enabled = true,
            },
            adminToken).ConfigureAwait(true);
        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "override-member",
                email = "override-member@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        _ = invited;
        string memberToken = await fixture.LoginAsAsync("override-member", "Testing1").ConfigureAwait(true);

        using HttpRequestMessage request = new(HttpMethod.Put, "/api/credentials/connector/shared-http")
        {
            Content = JsonContent.Create(new { entries = new { }, httpUrl = "http://127.0.0.1/mcp" }, options: jsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);

        using JsonDocument status = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/credentials/connector/shared-http",
            body: null,
            memberToken).ConfigureAwait(true);
        Assert.False(status.RootElement.TryGetProperty("httpUrl", out JsonElement saved) && saved.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task NonAdminCannotProbeABundleOutsideTheManagedDirectory()
    {
        await using ConnectorApiFixture fixture = await ConnectorApiFixture.CreateAsync().ConfigureAwait(true);
        string adminToken = await fixture.LoginAsBootstrapAdminAsync().ConfigureAwait(true);
        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "bundle-importer",
                email = "bundle-importer@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new XunitException("Invite response did not include a user id.");
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/import-permissions",
            new { canImportConnectors = true, canImportSkills = false },
            adminToken).ConfigureAwait(true);
        string memberToken = await fixture.LoginAsAsync("bundle-importer", "Testing1").ConfigureAwait(true);
        string outsideBundle = Path.Combine(Path.GetTempPath(), "kautuka-outside-" + Guid.NewGuid().ToString("N") + ".mcpb");
        await File.WriteAllTextAsync(outsideBundle, "synthetic-bundle-escape", TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            using HttpRequestMessage probe = new(HttpMethod.Post, "/api/connectors/test")
            {
                Content = JsonContent.Create(new
                {
                    id = "outside-bundle",
                    transport = "container",
                    containerImage = "quay.io/fedora/fedora-minimal:44",
                    bundlePath = outsideBundle,
                    serverType = "uv",
                }, options: jsonOptions),
            };
            probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
            using HttpResponseMessage probeResponse = await fixture.Client.SendAsync(probe, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string probeBody = await probeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, probeResponse.StatusCode);
            Assert.Contains("bundle path", probeBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("synthetic-bundle-escape", probeBody, StringComparison.Ordinal);
            GatewayConnectorTestRequest? captured = null;
            try
            {
                captured = fixture.GetCapturedTestRequest();
            }
            catch (XunitException)
            {
                captured = null;
            }

            Assert.NotEqual(outsideBundle, captured?.BundlePath, StringComparer.OrdinalIgnoreCase);

            using HttpRequestMessage save = new(HttpMethod.Post, "/api/connectors/outside-bundle")
            {
                Content = JsonContent.Create(new
                {
                    name = "Outside",
                    description = "Outside bundle",
                    transport = "container",
                    containerImage = "quay.io/fedora/fedora-minimal:44",
                    bundlePath = outsideBundle,
                    serverType = "uv",
                    enabled = true,
                }, options: jsonOptions),
            };
            save.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
            using HttpResponseMessage saveResponse = await fixture.Client.SendAsync(save, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.BadRequest, saveResponse.StatusCode);
            Assert.DoesNotContain(
                fixture.ReadStoredConnectorIds(),
                id => string.Equals(id, "outside-bundle", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(outsideBundle);
        }
    }

    [Fact]
    public async Task ConnectorValidationWithoutImportPermissionReturnsForbidden()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "connector-member",
                email = "connector-member@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        string memberToken = await fixture.LoginAsAsync("connector-member", "Testing1").ConfigureAwait(true);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/connectors/test")
        {
            Content = JsonContent.Create(new
            {
                transport = "http",
                httpUrl = "https://attacker.example/collect",
                envKeys = new[] { "MCP_AUTH_TOKEN" },
            }, options: jsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConnectorDiscoveryWithoutManagePermissionReturnsForbidden()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/shared-system",
            new
            {
                name = "Shared",
                description = "System connector",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                envKeys = Array.Empty<string>(),
                enabled = true,
            },
            adminToken).ConfigureAwait(true);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "connector-reader",
                email = "connector-reader@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);
        string memberToken = await fixture.LoginAsAsync("connector-reader", "Testing1").ConfigureAwait(true);

        using HttpRequestMessage request = new(HttpMethod.Put, "/api/connectors/shared-system/discovery")
        {
            Content = JsonContent.Create(new { toolCount = 1, toolNames = new[] { "invented" } }, options: jsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConnectorValidationEndpointMergesSavedPersonalHttpUrlOverrideWhenIdProvided()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/homeassistant-mcp",
            new
            {
                name = "Home Assistant",
                description = "Test connector",
                transport = "http",
                httpUrl = (string?)null,
                envKeys = Array.Empty<string>(),
                enabled = true,
            },
            accessToken).ConfigureAwait(true);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/homeassistant-mcp",
            new
            {
                entries = new { },
                httpUrl = "https://ha.example.com/api/mcp",
            },
            accessToken).ConfigureAwait(true);

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "homeassistant-mcp",
                transport = "http",
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("https://ha.example.com/api/mcp", response.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PersonalHttpUrlOverrideDoesNotReceiveSystemCredentials()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/homeassistant-mcp",
            new
            {
                name = "Home Assistant",
                description = "Test connector",
                transport = "http",
                httpUrl = (string?)null,
                envKeys = new[] { "MCP_AUTH_TOKEN" },
                enabled = true,
            },
            accessToken).ConfigureAwait(true);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/system",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "system-token",
                },
            },
            accessToken).ConfigureAwait(true);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/homeassistant-mcp",
            new
            {
                entries = new
                {
                    MCP_AUTH_TOKEN = "user-token",
                },
                httpUrl = "https://attacker.example/collect",
            },
            accessToken).ConfigureAwait(true);

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "homeassistant-mcp",
                transport = "http",
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("user-token", response.RootElement.GetProperty("message").GetString());
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("https://attacker.example/collect", captured.HttpUrl);
        Assert.NotNull(captured.TestEnv);
        Assert.False(captured.TestEnv!.ContainsValue("system-token"));
    }

    [Fact]
    public async Task ConnectorCrudPersistsContainerTransportFields()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/markitdown-container",
            new
            {
                name = "MarkItDown",
                description = "Containerized parser",
                transport = "container",
                containerImage = "docker.io/mcp/markitdown",
                containerCommand = (string?)null,
                containerArgs = new[] { "--mode", "server" },
                containerSidecarName = "mcp-markitdown",
                readinessTimeoutSeconds = 45,
                jitCooldownSeconds = 120,
                healthcheckUrl = "http://127.0.0.1:8080/health",
                healthcheckIntervalMilliseconds = 500,
                enabled = true,
                envKeys = new[] { "OPENAI_API_KEY" },
            },
            accessToken).ConfigureAwait(true);

        Assert.Equal("container", saved.RootElement.GetProperty("transport").GetString());
        Assert.Equal("docker.io/mcp/markitdown", saved.RootElement.GetProperty("containerImage").GetString());
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("containerCommand").ValueKind);
        Assert.Equal(120, saved.RootElement.GetProperty("jitCooldownSeconds").GetInt32());

        using JsonDocument reloaded = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/connectors/markitdown-container",
            body: null,
            accessToken).ConfigureAwait(true);

        Assert.Equal("container", reloaded.RootElement.GetProperty("transport").GetString());
        Assert.Equal("docker.io/mcp/markitdown", reloaded.RootElement.GetProperty("containerImage").GetString());
        Assert.Equal("mcp-markitdown", reloaded.RootElement.GetProperty("containerSidecarName").GetString());
        Assert.Equal(45, reloaded.RootElement.GetProperty("readinessTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task ConnectorValidationEndpointPreservesBundleProbeFields()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "splose-mcp",
                transport = "container",
                containerImage = "quay.io/fedora/fedora-minimal:44",
                containerCommand = "uv",
                containerArgs = new[] { "run", "-m", "splose_mcp.server" },
                bundlePath = "/tmp/connectors/splose.mcpb",
                serverType = "uv",
                testEnv = new
                {
                    SPLOSE_ROOT = "/opt/kautuka/mcpb/config",
                },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());

        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("/tmp/connectors/splose.mcpb", captured.BundlePath);
        Assert.Equal("uv", captured.ServerType);
        Assert.Equal("container", captured.Transport);
        Assert.NotNull(captured.TestEnv);
        Assert.True(captured.TestEnv!.TryGetValue("SPLOSE_ROOT", out string? probeRoot));
        Assert.Equal("/opt/kautuka/mcpb/config", probeRoot);
    }

    [Fact]
    public async Task McpbImportEndpointPersistsReadyArtifactStatusAfterValidation()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using MultipartFormDataContent upload = CreateMcpbUpload();
        using JsonDocument response = await fixture.SendMultipartAsync("/api/connectors/import/mcpb", upload, accessToken).ConfigureAwait(true);

        Assert.Equal("created", response.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("ready", response.RootElement.GetProperty("connector").GetProperty("runtimeArtifactBuildStatus").GetString());

        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("container", captured.Transport);
        Assert.Equal("node", captured.ServerType);
        Assert.False(string.IsNullOrWhiteSpace(captured.BundlePath));
    }

    [Fact]
    public async Task McpbImportEndpointRejectsConnectorWhenValidationFails()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        fixture.SetTestResult(new McpConnectorTestResult(false, "validation failed"));

        using MultipartFormDataContent upload = CreateMcpbUpload();
        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync(
            "/api/connectors/import/mcpb",
            upload,
            accessToken,
            HttpStatusCode.BadRequest).ConfigureAwait(true);

        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("failed", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("validation failed", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ConnectorValidationEndpointIncludesProxyDefaultsInContainerProbeEnvironment()
    {
        var manager = new FakeManagerGrpcService(
            containerProbeResponse: new WorkspaceMcpProbeResponse
            {
                Ok = true,
                ToolCount = 0,
            });

        await using var fixture = await ConnectorApiFixture.CreateAsync(
            captureConnectorTests: false,
            managerGrpcService: manager);

        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                id = "splose-mcp",
                transport = "container",
                containerImage = "quay.io/fedora/fedora-minimal:44",
                containerCommand = "uv",
                containerArgs = new[] { "run", "-m", "splose_mcp.server" },
                bundlePath = "/tmp/connectors/splose.mcpb",
                serverType = "uv",
                testEnv = new
                {
                    SPLOSE_ROOT = "/opt/kautuka/mcpb/config",
                },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());

        WorkspaceMcpContainerProbeRequest captured = manager.LastContainerProbeRequest
            ?? throw new XunitException("Container probe request was not captured.");

        Dictionary<string, string> environment = captured.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("/opt/kautuka/mcpb/config", environment["SPLOSE_ROOT"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/npm/", environment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["UV_INDEX_URL"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["UV_DEFAULT_INDEX"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["PIP_INDEX_URL"]);
        Assert.Equal("kautuka-pkgs-proxy", environment["PIP_TRUSTED_HOST"]);
    }

    [Fact]
    public async Task ConnectorSaveNormalizesLocalTransportToContainer()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/filesystem",
            new
            {
                name = "Filesystem",
                description = "Imported from mcpServers JSON",
                transport = "local",
                localCommand = "npx",
                localArgs = new[]
                {
                    "-y",
                    "@modelcontextprotocol/server-filesystem",
                    "/workspace/documents",
                },
                enabled = true,
                envKeys = Array.Empty<string>(),
            },
            accessToken).ConfigureAwait(true);

        Assert.Equal("container", saved.RootElement.GetProperty("transport").GetString());
        Assert.Equal(McpCommandTransportMapper.UvImage, saved.RootElement.GetProperty("containerImage").GetString());
        Assert.Equal("npx", saved.RootElement.GetProperty("containerCommand").GetString());
        Assert.Equal("@modelcontextprotocol/server-filesystem", saved.RootElement.GetProperty("containerArgs")[1].GetString());
    }

    [Fact]
    public async Task ConnectorValidationEndpointNormalizesLocalProbeToContainer()
    {
        var manager = new FakeManagerGrpcService(
            containerProbeResponse: new WorkspaceMcpProbeResponse
            {
                Ok = true,
                ToolCount = 1,
                Tools = { new WorkspaceMcpToolMessage { Name = "list_allowed_directories", Description = "Lists directories." } },
            });

        await using var fixture = await ConnectorApiFixture.CreateAsync(
            captureConnectorTests: false,
            managerGrpcService: manager);

        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                transport = "local",
                localCommand = "npx",
                localArgs = new[]
                {
                    "-y",
                    "@modelcontextprotocol/server-filesystem",
                    "/Users/username/Desktop",
                    "/path/to/other/allowed/dir",
                },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, response.RootElement.GetProperty("toolCount").GetInt32());

        WorkspaceMcpContainerProbeRequest captured = manager.LastContainerProbeRequest
            ?? throw new XunitException("Container probe request was not captured.");

        Assert.Equal(McpCommandTransportMapper.UvImage, captured.Image);
        Assert.Equal("npx", captured.Command);
        Assert.Equal("@modelcontextprotocol/server-filesystem", captured.Args[1]);
        Dictionary<string, string> environment = captured.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/npm/", environment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("kautuka-pkgs-proxy", environment["PIP_TRUSTED_HOST"]);
    }

    [Fact]
    public async Task ConnectorValidationEndpointRejectsUnsupportedLocalCommands()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync(captureConnectorTests: false);
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/test",
            new
            {
                transport = "local",
                localCommand = "ruby",
                localArgs = new[] { "server.rb" },
            },
            accessToken).ConfigureAwait(true);

        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Unsupported command 'ruby'", response.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectorCreateWithoutImportPermissionReturnsForbidden()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        _ = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "connector-member",
                email = "connector-member@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);

        string memberToken = await fixture.LoginAsAsync("connector-member", "Testing1").ConfigureAwait(true);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/connectors/unauthorized-create")
        {
            Content = JsonContent.Create(new
            {
                name = "Unauthorized Create",
                description = "Should be blocked",
                transport = "http",
                httpUrl = "https://example.invalid/mcp",
                envKeys = Array.Empty<string>(),
                enabled = true,
            }, options: jsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument payload = JsonDocument.Parse(content);
        Assert.Equal("Forbidden.", payload.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ConnectorCreateWithImportPermissionSucceeds()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "connector-importer",
                email = "connector-importer@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken).ConfigureAwait(true);

        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new XunitException("Invite response did not include a user id.");

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/import-permissions",
            new
            {
                canImportConnectors = true,
                canImportSkills = false,
            },
            adminToken).ConfigureAwait(true);

        string memberToken = await fixture.LoginAsAsync("connector-importer", "Testing1").ConfigureAwait(true);

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/connectors/authorized-create",
            new
            {
                name = "Authorized Create",
                description = "Should be allowed",
                transport = "http",
                httpUrl = "https://example.invalid/mcp",
                envKeys = Array.Empty<string>(),
                enabled = true,
            },
            memberToken).ConfigureAwait(true);

        Assert.Equal("authorized-create", saved.RootElement.GetProperty("id").GetString());
        Assert.Equal("http", saved.RootElement.GetProperty("transport").GetString());
    }

    [Fact]
    public async Task LoopbackConnectorCallbackRequiresTheBrowserThatStartedSignIn()
    {
        await using ConnectorApiFixture fixture = await ConnectorApiFixture.CreateAsync().ConfigureAwait(true);
        const string state = "synthetic-oauth-state";
        fixture.SaveLoopbackOAuthSession(state, "user-owner");

        using HttpRequestMessage missingCookie = new(HttpMethod.Get, "/api/connectors/oauth/callback?code=synthetic-code&state=" + state);
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(missingCookie, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Contains("expired", rejected.Headers.Location?.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.OAuthSessionExists(state));

        using HttpRequestMessage owned = new(HttpMethod.Get, "/api/connectors/oauth/callback?code=synthetic-code&state=" + state);
        owned.Headers.Add("Cookie", "kautuka_connector_oauth=" + state);
        using HttpResponseMessage accepted = await fixture.Client.SendAsync(owned, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.False(fixture.OAuthSessionExists(state));
    }

    [Fact]
    public async Task HostedConnectorCompletionRequiresTheInitiatingBrowserAndReturnOrigin()
    {
        await using ConnectorApiFixture fixture = await ConnectorApiFixture.CreateAsync().ConfigureAwait(true);
        string accessToken = await fixture.LoginAsBootstrapAdminAsync().ConfigureAwait(true);
        using JsonDocument currentUser = await fixture.SendJsonAsync(HttpMethod.Get, "/api/auth/me", body: null, accessToken: accessToken).ConfigureAwait(true);
        string userId = currentUser.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Profile response did not include a user id.");
        const string state = "hosted-oauth-state";
        fixture.SaveHostedOAuthSession(state, userId, "https://appliance.example");

        using HttpRequestMessage missingCookie = new(HttpMethod.Post, "/api/connectors/oauth/complete")
        {
            Content = JsonContent.Create(new { code = "synthetic-code", state }),
        };
        missingCookie.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        missingCookie.Headers.TryAddWithoutValidation("Origin", "https://appliance.example");
        using HttpResponseMessage browserRejected = await fixture.Client.SendAsync(missingCookie, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, browserRejected.StatusCode);
        using JsonDocument browserError = JsonDocument.Parse(await browserRejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("rejected", browserError.RootElement.GetProperty("status").GetString());
        Assert.True(fixture.OAuthSessionExists(state));

        using HttpRequestMessage wrongOrigin = new(HttpMethod.Post, "/api/connectors/oauth/complete")
        {
            Content = JsonContent.Create(new { code = "synthetic-code", state }),
        };
        wrongOrigin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        wrongOrigin.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
        wrongOrigin.Headers.Add("Cookie", "kautuka_connector_oauth=" + state);
        using HttpResponseMessage originRejected = await fixture.Client.SendAsync(wrongOrigin, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, originRejected.StatusCode);
        using JsonDocument originError = JsonDocument.Parse(await originRejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("rejected", originError.RootElement.GetProperty("status").GetString());
        Assert.True(fixture.OAuthSessionExists(state));

        using HttpRequestMessage owned = new(HttpMethod.Post, "/api/connectors/oauth/complete")
        {
            Content = JsonContent.Create(new { code = "synthetic-code", state }),
        };
        owned.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        owned.Headers.TryAddWithoutValidation("Origin", "https://appliance.example");
        owned.Headers.Add("Cookie", "kautuka_connector_oauth=" + state);
        using HttpResponseMessage accepted = await fixture.Client.SendAsync(owned, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using JsonDocument completed = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("not_configured", completed.RootElement.GetProperty("status").GetString());
        Assert.False(fixture.OAuthSessionExists(state));
    }

    private sealed class ConnectorApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly WebApplication app;

        private ConnectorApiFixture(string root, string? previousEnvironment, WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public void SaveLoopbackOAuthSession(string state, string userId)
        {
            app.Services.GetRequiredService<ConnectorOAuthSessionStore>().Save(state, new ConnectorOAuthSession
            {
                UserId = userId,
                ConnectorId = "not-a-real-connector",
                CodeVerifier = "synthetic-verifier",
                RedirectUri = "http://127.0.0.1:20229/api/connectors/oauth/callback",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
        }

        public void SaveHostedOAuthSession(string state, string userId, string returnOrigin)
        {
            app.Services.GetRequiredService<ConnectorOAuthSessionStore>().Save(state, new ConnectorOAuthSession
            {
                UserId = userId,
                ConnectorId = "not-a-real-connector",
                CodeVerifier = "synthetic-verifier",
                ReturnOrigin = returnOrigin,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
        }

        public bool OAuthSessionExists(string state)
            => app.Services.GetRequiredService<ConnectorOAuthSessionStore>().TryGet(state, out _);

        public IReadOnlyList<string> ReadStoredConnectorIds()
            => [.. app.Services.GetRequiredService<IPlatformConfigurationProvider>()
                .GetCurrent()
                .McpServers
                .Select(static server => server.Id)];

        public (string Scope, string? OwnerUserId) ReadStoredConnectorOwnership(string connectorId)
        {
            McpServerDefinition server = app.Services.GetRequiredService<IPlatformConfigurationProvider>()
                .GetCurrent()
                .McpServers
                .Single(item => string.Equals(item.Id, connectorId, StringComparison.Ordinal));
            return (server.Scope, server.OwnerUserId);
        }

        public static async Task<ConnectorApiFixture> CreateAsync(
            bool captureConnectorTests = true,
            IManagerGrpcService? managerGrpcService = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-connector-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string sqlitePath = Path.Combine(root, "gateway.db");
            string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            });

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            if (captureConnectorTests)
            {
                builder.Services.RemoveAll<IMcpConnectorTestService>();
                builder.Services.AddSingleton<IMcpConnectorTestService, CapturingConnectorTestService>();
            }

            builder.Services.RemoveAll<IManagerGrpcService>();
            builder.Services.AddSingleton(managerGrpcService ?? new FakeManagerGrpcService());

            builder.Services.RemoveAll<CredentialResolverService>();
            builder.Services.AddSingleton(
                new CredentialResolverService(
                    new Lazy<CredentialsService.CredentialsServiceClient>(new FakeCredentialsServiceClient())));
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync().ConfigureAwait(true);

            return new ConnectorApiFixture(root, previousEnvironment, app);
        }

        public async Task<string> LoginAsBootstrapAdminAsync()
        {
            using JsonDocument setup = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() },
                accessToken: null,
                authenticated: false).ConfigureAwait(true);

            using JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new
                {
                    username = "admin",
                    password = "Testing1",
                },
                accessToken: null,
                authenticated: false).ConfigureAwait(true);

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task<string> LoginAsAsync(string username, string password)
        {
            using JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new
                {
                    username,
                    password,
                },
                accessToken: null,
                authenticated: false).ConfigureAwait(true);

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: jsonOptions);
            }

            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken ?? throw new ArgumentNullException(nameof(accessToken)));
            }

            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");
            }

            return JsonDocument.Parse(content);
        }

        public async Task<JsonDocument> SendMultipartAsync(string path, MultipartFormDataContent content, string accessToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {payload}");
            }

            return JsonDocument.Parse(payload);
        }

        public async Task<HttpResponseMessage> SendMultipartExpectingStatusAsync(string path, MultipartFormDataContent content, string accessToken, HttpStatusCode expectedStatusCode)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            Assert.Equal(expectedStatusCode, response.StatusCode);
            return response;
        }

        public void SetTestResult(McpConnectorTestResult result)
        {
            if (app.Services.GetRequiredService<IMcpConnectorTestService>() is not CapturingConnectorTestService service)
            {
                throw new XunitException("Capturing connector test service is not registered.");
            }

            service.NextResult = result;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync().ConfigureAwait(true);

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);

            if (Directory.Exists(root))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
            }
        }

        public GatewayConnectorTestRequest GetCapturedTestRequest()
            => app.Services.GetRequiredService<IMcpConnectorTestService>() is CapturingConnectorTestService service
                ? service.LastRequest ?? throw new XunitException("No connector test request was captured.")
                : throw new XunitException("Capturing connector test service is not registered.");
    }

    private sealed class CapturingConnectorTestService : IMcpConnectorTestService
    {
        public GatewayConnectorTestRequest? LastRequest { get; private set; }

        public McpConnectorTestResult? NextResult { get; set; }

        public Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (NextResult is not null)
            {
                return Task.FromResult(NextResult);
            }

            string? token = null;
            request.TestEnv?.TryGetValue("MCP_AUTH_TOKEN", out token);

            if (!string.IsNullOrWhiteSpace(token))
            {
                return Task.FromResult(new McpConnectorTestResult(true, token));
            }

            return Task.FromResult(new McpConnectorTestResult(true, request.HttpUrl ?? string.Empty));
        }
    }

    private static MultipartFormDataContent CreateMcpbUpload()
    {
        const string manifest =
            """
            {
              "manifest_version": "0.4",
              "name": "Acme MCP",
              "display_name": "Acme MCP Bundle",
              "version": "1.2.3",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": {
                  "command": "node",
                  "args": ["${__dirname}/server/index.js"]
                }
              }
            }
            """;

        byte[] archiveBytes = CreateMcpbArchive(manifest);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(archiveBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", "acme.mcpb");
        return content;
    }

    private static byte[] CreateMcpbArchive(string manifestJson)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json");
            using (Stream entryStream = manifest.Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(manifestJson);
                entryStream.Write(bytes, 0, bytes.Length);
            }

            archive.CreateEntry("server/index.js");
            archive.CreateEntry("package.json");
        }

        return stream.ToArray();
    }

    private sealed class FakeManagerGrpcService(
        WorkspaceMcpProbeResponse? containerProbeResponse = null) : IManagerGrpcService
    {
        public WorkspaceMcpContainerProbeRequest? LastContainerProbeRequest { get; private set; }

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBoolResponse { Ok = true });

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerStatusResponse { Ok = true });

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true, Message = "connector-validation-test" });

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
        {
            LastContainerProbeRequest = request;
            return Task.FromResult(containerProbeResponse ?? new WorkspaceMcpProbeResponse
            {
                Ok = true,
                ToolCount = 0,
            });
        }

        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new DesiredStateUpdateResponse { Outcome = DesiredStateUpdateOutcome.Applied });
    }

    private sealed class FakeCredentialsServiceClient : CredentialsService.CredentialsServiceClient
    {
        private readonly Dictionary<string, string> systemStore = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> userStore = new(StringComparer.Ordinal);

        public override AsyncUnaryCall<CredentialKeyListResponse> ListSystemCredentialKeysAsync(
            ListSystemCredentialKeysRequest request,
            CallOptions options)
        {
            var response = new CredentialKeyListResponse();
            response.Keys.AddRange(systemStore.Keys);
            return Wrap(response);
        }

        public override AsyncUnaryCall<SetCredentialsResponse> SetSystemCredentialsAsync(
            SetSystemCredentialsRequest request,
            CallOptions options)
        {
            foreach ((string key, string value) in request.Entries)
            {
                systemStore[key] = value;
            }

            return Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });
        }

        public override AsyncUnaryCall<CredentialMapResponse> GetSystemCredentialAsync(
            GetSystemCredentialRequest request,
            CallOptions options)
        {
            var response = new CredentialMapResponse();
            if (systemStore.TryGetValue(request.Key, out string? value))
            {
                response.Entries[request.Key] = value;
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
            GetResolvedCredentialsRequest request,
            CallOptions options)
        {
            userStore.TryGetValue(request.UserId, out Dictionary<string, string>? userValues);

            var response = new CredentialMapResponse();
            foreach (string key in request.RequiredKeys)
            {
                if (userValues is not null && userValues.TryGetValue(key, out string? userValue))
                {
                    response.Entries[key] = userValue;
                    continue;
                }

                if (systemStore.TryGetValue(key, out string? systemValue))
                {
                    response.Entries[key] = systemValue;
                }
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<CredentialKeyListResponse> ListUserCredentialKeysAsync(
            ListUserCredentialKeysRequest request,
            CallOptions options)
        {
            var response = new CredentialKeyListResponse();
            if (userStore.TryGetValue(request.UserId, out Dictionary<string, string>? values))
            {
                response.Keys.AddRange(values.Keys);
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<SetCredentialsResponse> SetUserCredentialsAsync(
            SetUserCredentialsRequest request,
            CallOptions options)
        {
            if (!userStore.TryGetValue(request.UserId, out Dictionary<string, string>? values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                userStore[request.UserId] = values;
            }

            foreach ((string key, string value) in request.Entries)
            {
                values[key] = value;
            }

            return Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });
        }

        public override AsyncUnaryCall<DeleteCredentialResponse> DeleteSystemCredentialAsync(
            DeleteSystemCredentialRequest request,
            CallOptions options)
        {
            bool deleted = systemStore.Remove(request.Key);
            return Wrap(new DeleteCredentialResponse { Deleted = deleted });
        }

        public override AsyncUnaryCall<DeleteCredentialResponse> DeleteUserCredentialAsync(
            DeleteUserCredentialRequest request,
            CallOptions options)
        {
            bool deleted = userStore.TryGetValue(request.UserId, out Dictionary<string, string>? values)
                && values.Remove(request.Key);
            return Wrap(new DeleteCredentialResponse { Deleted = deleted });
        }

        private static AsyncUnaryCall<T> Wrap<T>(T response) where T : class
            => new(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => new Metadata(),
                disposeAction: static () => { });
    }
}
