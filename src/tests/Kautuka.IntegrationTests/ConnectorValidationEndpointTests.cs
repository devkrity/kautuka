using System.Collections.Concurrent;
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
using Kautuka.Gateway.Domain.Stores;
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
using Microsoft.Extensions.Logging;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

[Collection(ProcessEnvironmentTestGroup.Name)]
public sealed class ConnectorValidationEndpointTests
{
    [Fact]
    public async Task RemoteConnectorOperationsEmitMetadataOnlyAuditEvents()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/audited-remote", new
        {
            name = "Audited",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            credentials = new[] { new { key = "api-key", label = "API key", required = false } },
            httpHeaders = new[] { new { name = "x-api-key", credentialKey = "api-key" } },
        }, accessToken);
        foreach (string value in new[] { "synthetic-audit-first", "synthetic-audit-second" })
        {
            using JsonDocument updated = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/audited-remote",
                new { entries = new Dictionary<string, string> { ["api-key"] = value } }, accessToken);
        }
        using JsonDocument removed = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/audited-remote",
            new { entries = new Dictionary<string, string>(), removeKeys = new[] { "api-key" } }, accessToken);
        using JsonDocument tested = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/test", new { id = "audited-remote", transport = "http" }, accessToken);
        using JsonDocument disabled = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/audited-remote",
            new { name = "Audited", transport = "http", httpUrl = "https://example.com/mcp", enabled = false }, accessToken);
        foreach (string operation in new[] { "connector.created", "connector.updated", "connector.disabled", "credential.added", "credential.replaced", "credential.removed", "connection.tested" })
            Assert.Contains(fixture.AuditMessages, entry => entry.Contains("operation=" + operation, StringComparison.Ordinal));
        Assert.All(fixture.AuditMessages, entry => Assert.DoesNotContain("synthetic-audit", entry, StringComparison.Ordinal));
    }

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
    public async Task ConnectorValidationRejectsPlaintextRemoteTestSecrets()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using var probe = new HttpRequestMessage(HttpMethod.Post, "/api/connectors/test")
        {
            Content = JsonContent.Create(new { transport = "http", httpUrl = "https://ha.example.com/api/mcp", testEnv = new { MCP_AUTH_TOKEN = "secret-token" } }, options: jsonOptions),
        };
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(probe, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-token", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
            },
            accessToken).ConfigureAwait(true);

        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Null(captured.TestEnv);
        Assert.NotNull(captured.RemoteContext);
        string key = fixture.RemoteCredentialKey("homeassistant-mcp", "https://ha.example.com/api/mcp", "MCP_AUTH_TOKEN");
        IReadOnlyDictionary<string, string> resolved = await captured.RemoteContext.Resolve([key], TestContext.Current.CancellationToken);
        Assert.Equal("saved-token", resolved[key]);
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

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RuntimeConnectorClientBlocksPrivateConnectAndDoesNotForwardCustomCredentialsOnRedirect(int statusCode)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using SocketsHttpHandler blocked = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivate: false);
        Assert.False(blocked.UseProxy);
        Assert.False(blocked.AllowAutoRedirect);
        using var blockedClient = new HttpClient(blocked) { Timeout = TimeSpan.FromSeconds(5) };
        HttpRequestException denied = await Assert.ThrowsAsync<HttpRequestException>(() =>
            blockedClient.GetAsync(new Uri("http://127.0.0.1:9/mcp"), cancellationToken));
        Assert.Contains("isn't allowed", denied.Message, StringComparison.Ordinal);

        using var target = new TcpListener(IPAddress.Loopback, 0);
        using var redirector = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            target.Start();
            redirector.Start();
            int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            int redirectPort = ((IPEndPoint)redirector.LocalEndpoint).Port;
#pragma warning disable CA2025 // The response task is awaited before either listener is stopped.
            Task redirectAccepted = AcceptOnceAsync(
                redirector,
                $"HTTP/1.1 {statusCode} Redirect\r\nLocation: http://127.0.0.1:{targetPort}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                cancellationToken);
#pragma warning restore CA2025
            using SocketsHttpHandler allowed = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivate: true);
            using var allowedClient = new HttpClient(allowed) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{redirectPort}/"));
            request.Headers.Add("x-api-key", "synthetic-redirect-regression-key");
            using HttpResponseMessage response = await allowedClient.SendAsync(request, cancellationToken);
            Assert.Equal(statusCode, (int)response.StatusCode);
            await redirectAccepted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.False(target.Pending());
        }
        finally
        {
            target.Stop();
            redirector.Stop();
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
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
            HttpMethod.Post,
            "/api/connectors/shared-http",
            new
            {
                name = "Shared",
                description = "System connector",
                transport = "http",
                httpUrl = "https://ha.example.com/api/mcp",
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
                enabled = true,
            },
            adminToken).ConfigureAwait(true);
        string sharedKey = fixture.RemoteCredentialKey("shared-http", "https://ha.example.com/api/mcp", "MCP_AUTH_TOKEN");
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/shared-http/system", new { entries = new { MCP_AUTH_TOKEN = "system-token" } }, adminToken).ConfigureAwait(true);
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
        GatewayConnectorTestRequest systemCaptured = fixture.GetCapturedTestRequestForUrl("https://ha.example.com/api/mcp");
        Assert.Null(systemCaptured.TestEnv);
        Assert.NotNull(systemCaptured.RemoteContext);
        Assert.True(systemCaptured.RemoteContext.IncludeSystemScope);
        IReadOnlyDictionary<string, string> systemResolved = await systemCaptured.RemoteContext.Resolve([sharedKey], TestContext.Current.CancellationToken);
        Assert.Equal("system-token", systemResolved[sharedKey]);
        Assert.True(systemCaptured.AllowPrivateDestination);
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
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
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequestForUrl("https://member.example/mcp");
        Assert.Null(captured.TestEnv);
        Assert.NotNull(captured.RemoteContext);
        Assert.False(captured.RemoteContext.IncludeSystemScope);
        string memberKey = fixture.RemoteCredentialKey("member-http", "https://member.example/mcp", "MCP_AUTH_TOKEN");
        IReadOnlyDictionary<string, string> memberResolved = await captured.RemoteContext.Resolve([memberKey, sharedKey], TestContext.Current.CancellationToken);
        Assert.Equal("user-token", memberResolved[memberKey]);
        Assert.False(memberResolved.ContainsKey(sharedKey));
        Assert.False(captured.AllowPrivateDestination);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("system")]
    [InlineData("headers")]
    public async Task ConnectorTestRejectsChangesWhileEndpointIsResolving(string change)
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        object Candidate(string header) => new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = header, credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/snapshot", Candidate("X-Api-Key"), admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/snapshot", new { entries = new { key = "before" } }, admin);
        TaskCompletionSource<bool> release = fixture.Credentials.BlockNextAssociationRead();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/connectors/test") { Content = JsonContent.Create(new { id = "snapshot", transport = "http", httpUrl = "https://example.com/mcp" }, options: jsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
#pragma warning disable CA2025 // The finally block releases the barrier and awaits this request before disposal.
        Task<HttpResponseMessage> pending = fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
#pragma warning restore CA2025
        HttpResponseMessage? response = null;
        try
        {
            await fixture.Credentials.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (change == "headers") _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/snapshot", Candidate("Authorization"), admin);
            else _ = await fixture.SendJsonAsync(HttpMethod.Put, change == "system" ? "/api/credentials/connector/snapshot/system" : "/api/credentials/connector/snapshot", new { entries = new { key = "after" } }, admin);
        }
        finally { release.TrySetResult(true); response = await pending; }
        using (response)
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("system")]
    [InlineData("headers")]
    public async Task DraftTestRejectsChangesWhileEndpointIsResolving(string change)
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        object Candidate(string header) => new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = header, credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/draft-snapshot", Candidate("X-Api-Key"), admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/draft-snapshot", new { entries = new { key = "before" } }, admin);
        using JsonDocument draft = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "draft-snapshot", candidate = Candidate("X-Api-Key"), credentialScope = "user", entries = new { }, removeKeys = Array.Empty<string>() }, admin);
        string id = draft.RootElement.GetProperty("draftId").GetString()!;
        TaskCompletionSource<bool> release = fixture.Credentials.BlockNextAssociationRead();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/connectors/drafts/{id}/test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
#pragma warning disable CA2025 // The finally block releases the barrier and awaits this request before disposal.
        Task<HttpResponseMessage> pending = fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
#pragma warning restore CA2025
        HttpResponseMessage? response = null;
        try
        {
            await fixture.Credentials.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (change == "headers") _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/draft-snapshot", Candidate("Authorization"), admin);
            else _ = await fixture.SendJsonAsync(HttpMethod.Put, change == "system" ? "/api/credentials/connector/draft-snapshot/system" : "/api/credentials/connector/draft-snapshot", new { entries = new { key = "after" } }, admin);
        }
        finally { release.TrySetResult(true); response = await pending; }
        using (response) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestRejectsAdminDemotionBeforeLeaseAndPrivateDestinationSend()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var candidate = new
        {
            scope = "user",
            name = "Private",
            transport = "http",
            httpUrl = "http://127.0.0.1:4567/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/private-snapshot", candidate, admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/private-snapshot", new { entries = new { key = "before" } }, admin);
        TaskCompletionSource<bool> release = fixture.Credentials.BlockNextAssociationRead();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/connectors/test") { Content = JsonContent.Create(new { id = "private-snapshot", transport = "http", httpUrl = "http://127.0.0.1:4567/mcp" }, options: jsonOptions) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
#pragma warning disable CA2025 // The finally block releases the barrier and awaits this request before disposal.
        Task<HttpResponseMessage> pending = fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
#pragma warning restore CA2025
        HttpResponseMessage? response = null;
        try
        {
            await fixture.Credentials.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            fixture.DemoteBootstrapAdmin();
        }
        finally { release.TrySetResult(true); response = await pending; }
        using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequestForUrl("http://127.0.0.1:4567/mcp");
        Assert.True(captured.AllowPrivateDestination);
        var inner = new FailIfSentHandler();
        using var invoker = new HttpMessageInvoker(new RemoteMcpRequestHandler(
            captured.RemoteContext!,
            new AllowAllConnectorTestChecker(),
            inner));
        using var outgoing = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:4567/mcp");
        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(outgoing, TestContext.Current.CancellationToken));
        Assert.False(inner.WasSent);
    }

    private sealed class AllowAllConnectorTestChecker : Kautuka.NetworkAccess.Client.INetworkAccessChecker
    {
        public Task<Kautuka.NetworkAccess.Client.NetworkAccessCheckResult> CheckAsync(
            Kautuka.NetworkAccess.Client.OutboundTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Kautuka.NetworkAccess.Client.NetworkAccessCheckResult.CreateAllowed("test_allow"));
    }

    private sealed class FailIfSentHandler : HttpMessageHandler
    {
        public bool WasSent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
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
    public async Task DraftApiTestsAndSavesOnlyTheBoundCandidate()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        var candidate = new
        {
            name = "Draft",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = true } },
            httpHeaders = new[] { new { name = "Authorization", credentialKey = "key", prefix = "Bearer " } }
        };
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "new-draft", candidate, credentialScope = "user", entries = new { key = "synthetic-secret" }, removeKeys = Array.Empty<string>() }, token);
        string id = created.RootElement.GetProperty("draftId").GetString()!;
        Assert.DoesNotContain("synthetic-secret", created.RootElement.GetRawText(), StringComparison.Ordinal);
        using JsonDocument pending = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector-drafts", null, token);
        Assert.Equal(id, pending.RootElement.GetProperty("drafts")[0].GetProperty("draftId").GetString());
        Assert.DoesNotContain("synthetic-secret", pending.RootElement.GetRawText(), StringComparison.Ordinal);
        using JsonDocument tested = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/test", null, token);
        Assert.True(tested.RootElement.GetProperty("success").GetBoolean());
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Null(captured.TestEnv);
        Assert.NotNull(captured.RemoteContext);
        string storageKey = ConnectorCredentialKeys.BuildRemoteCredentialKey(captured.RemoteContext.CredentialNamespace!, captured.RemoteContext.Endpoint, "key");
        IReadOnlyDictionary<string, string> resolved = await captured.RemoteContext.Resolve([storageKey], TestContext.Current.CancellationToken);
        Assert.Equal("synthetic-secret", resolved[storageKey]);
        using JsonDocument saved = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, token);
        Assert.Equal("new-draft", saved.RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain("synthetic-secret", saved.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(fixture.AuditMessages, message => message.Contains("operation=connector.created", StringComparison.Ordinal) && message.Contains("connector=new-draft", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.AuditMessages, message => message.Contains("synthetic-secret", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("toolCount").ValueKind);
        using JsonDocument repeated = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, token);
        Assert.Equal("new-draft", repeated.RootElement.GetProperty("id").GetString());
        using JsonDocument status = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/new-draft", null, token);
        Assert.Equal("user", status.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
    }

    [Fact]
    public async Task OwnerCredentialOnlyDraftPreservesSharedTimestampAndOtherCallerLease()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var candidate = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/owner-draft", candidate, admin);
        string updatedAt = fixture.ConnectorUpdatedAt("owner-draft");
        using RemoteMcpOperationLease otherLease = fixture.AcquireConnectorLease("owner-draft", "other-user");
        using JsonDocument draft = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "owner-draft", candidate, credentialScope = "user", entries = new { key = "owner-secret" }, removeKeys = Array.Empty<string>() }, admin);
        string id = draft.RootElement.GetProperty("draftId").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, admin);
        Assert.Equal(updatedAt, fixture.ConnectorUpdatedAt("owner-draft"));
        Assert.False(otherLease.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task LegacyMarkerRejectsPreviouslyCreatedUnconfirmedDraftUntilReviewedReplacement()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var candidate = new { name = "Legacy", transport = "http", httpUrl = "https://example.com/mcp", enabled = true, credentials = Array.Empty<object>(), httpHeaders = Array.Empty<object>() };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/legacy-review", candidate, admin);
        using JsonDocument draft = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "legacy-review", candidate, credentialScope = "user", entries = new { } }, admin);
        string id = draft.RootElement.GetProperty("draftId").GetString()!;
        fixture.MarkConnectorLegacy("legacy-review");
        foreach (string action in new[] { "test", "save" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/connectors/drafts/{id}/{action}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        var confirmed = new { name = "Legacy", transport = "http", httpUrl = "https://example.com/mcp", enabled = true, credentials = Array.Empty<object>(), httpHeaders = Array.Empty<object>(), confirmLegacyAuthReplacement = true };
        using JsonDocument replacement = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "legacy-review", candidate = confirmed, credentialScope = "user", entries = new { } }, admin);
        string replacementId = replacement.RootElement.GetProperty("draftId").GetString()!;
        using JsonDocument saved = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{replacementId}/save", null, admin);
        Assert.False(saved.RootElement.GetProperty("requiresCredentialReentry").GetBoolean());
    }

    [Fact]
    public async Task DraftApiReauthorizesOwnerAndPreventsSharedConnectorTakeover()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var candidate = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://example.com/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/shared-draft", candidate, admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite", new { username = "draft-user", email = "draft@example.com", password = "Testing1", role = "user" }, admin);
        string member = await fixture.LoginAsAsync("draft-user", "Testing1");
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "shared-draft", candidate, credentialScope = "user", entries = new { key = "member-secret" }, removeKeys = Array.Empty<string>() }, member);
        string id = created.RootElement.GetProperty("draftId").GetString()!;
        using var foreign = new HttpRequestMessage(HttpMethod.Post, $"/api/connectors/drafts/{id}/save");
        foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using HttpResponseMessage denied = await fixture.Client.SendAsync(foreign, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var changed = new
        {
            name = "Taken",
            transport = "http",
            httpUrl = "https://other.example/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        using var takeover = new HttpRequestMessage(HttpMethod.Post, "/api/credentials/connector-drafts") { Content = JsonContent.Create(new { connectorId = "shared-draft", candidate = changed, credentialScope = "user", entries = new { key = "member-secret" } }, options: jsonOptions) };
        takeover.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member);
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(takeover, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using JsonDocument saved = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, member);
        Assert.Equal("Shared", saved.RootElement.GetProperty("name").GetString());
        Assert.Equal("https://example.com/mcp", saved.RootElement.GetProperty("httpUrl").GetString());
    }

    [Fact]
    public async Task PersonalDraftCanChangeDestinationWithoutForwardingOldCredential()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var shared = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://shared.example/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        var personal = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://1.1.1.1/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/personal-draft", shared, admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite", new { username = "personal-user", email = "personal@example.com", password = "Testing1", role = "user" }, admin);
        string member = await fixture.LoginAsAsync("personal-user", "Testing1");
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/personal-draft", new { entries = new { key = "old-secret" } }, member);
        using JsonDocument draft = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "personal-draft", candidate = personal, credentialScope = "user", entries = new { key = "new-secret" }, removeKeys = Array.Empty<string>() }, member);
        string id = draft.RootElement.GetProperty("draftId").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/test", null, member);
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("https://1.1.1.1/mcp", captured.HttpUrl);
        Assert.False(captured.RemoteContext!.IncludeSystemScope);
        string key = ConnectorCredentialKeys.BuildRemoteCredentialKey(captured.RemoteContext.CredentialNamespace!, captured.RemoteContext.Endpoint, "key");
        Assert.Equal("new-secret", (await captured.RemoteContext.Resolve([key], TestContext.Current.CancellationToken))[key]);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, member);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, member);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{id}/save", null, member);
        using JsonDocument sharedAfter = await fixture.SendJsonAsync(HttpMethod.Get, "/api/connectors/personal-draft", null, member);
        Assert.Equal("https://shared.example/mcp", sharedAfter.RootElement.GetProperty("httpUrl").GetString());
        using JsonDocument status = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/personal-draft", null, member);
        Assert.Equal("https://1.1.1.1/mcp", status.RootElement.GetProperty("httpUrl").GetString());
        Assert.Equal("user", status.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        using var cancel = new HttpRequestMessage(HttpMethod.Delete, $"/api/credentials/connector-drafts/{id}");
        cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member);
        using HttpResponseMessage canceled = await fixture.Client.SendAsync(cancel, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, canceled.StatusCode);
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/connectors/drafts/{id}/save");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member);
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(retry, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        using JsonDocument retained = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/personal-draft", null, member);
        Assert.Equal("user", retained.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
    }

    [Fact]
    public async Task SharedDestinationEditAndRecreatedIdDoNotReuseOldCredentials()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string admin = await fixture.LoginAsBootstrapAdminAsync();
        var first = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://old.example/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        var moved = new
        {
            name = "Shared",
            transport = "http",
            httpUrl = "https://new.example/mcp",
            enabled = true,
            credentials = new[] { new { key = "key", label = "Key", required = false } },
            httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } }
        };
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/shared-binding", first, admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite", new { username = "binding-user", email = "binding@example.com", password = "Testing1", role = "user" }, admin);
        string member = await fixture.LoginAsAsync("binding-user", "Testing1");
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/shared-binding", new { entries = new { key = "old-secret" } }, member);
        using JsonDocument before = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/shared-binding", null, member);
        Assert.Equal("user", before.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        using JsonDocument draft = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "shared-binding", candidate = moved, credentialScope = "user", entries = new { }, removeKeys = Array.Empty<string>() }, admin);
        string draftId = draft.RootElement.GetProperty("draftId").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/connectors/drafts/{draftId}/save", null, admin);
        using JsonDocument afterMove = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/shared-binding", null, member);
        Assert.Equal("none", afterMove.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/shared-binding", new { entries = new { key = "new-secret" } }, member);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/shared-binding", new { entries = new { }, httpUrl = "https://1.1.1.1/mcp" }, member);
        using JsonDocument stale = await fixture.SendJsonAsync(HttpMethod.Post, "/api/credentials/connector-drafts", new { connectorId = "shared-binding", candidate = moved, credentialScope = "user", entries = new { }, removeKeys = Array.Empty<string>() }, admin);
        string staleId = stale.RootElement.GetProperty("draftId").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Delete, "/api/connectors/shared-binding", null, admin);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/shared-binding", moved, admin);
        using JsonDocument afterRecreate = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/shared-binding", null, member);
        Assert.Equal("none", afterRecreate.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        Assert.Equal(JsonValueKind.Null, afterRecreate.RootElement.GetProperty("httpUrl").ValueKind);
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/connectors/drafts/{staleId}/save");
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(retry, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task ConnectorAndCallerChangesCancelOnlyMatchingOperationLeases()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/lease-test", new { name = "Lease", transport = "http", httpUrl = "https://example.com/mcp", enabled = true }, token);
        using JsonDocument invited = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite", new { username = "lease-user", email = "lease@example.com", password = "Testing1", role = "user" }, token);
        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/settings/agents/lease-agent", new { name = "Lease agent", modelId = "model-1", systemPrompt = "", mcpBindings = new[] { new { connectorId = "lease-test", context = "OwnContext" } } }, token);
        using RemoteMcpOperationLease agentLease = fixture.AcquireRemoteLease("other", userId, "lease-agent", false);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/settings/agents/lease-agent", new { name = "Lease agent", modelId = "model-1", systemPrompt = "", mcpBindings = Array.Empty<object>() }, token);
        Assert.True(agentLease.Token.IsCancellationRequested);
        using RemoteMcpOperationLease connectorLease = fixture.AcquireRemoteLease("lease-test", userId, "agent-a", false);
        using RemoteMcpOperationLease otherLease = fixture.AcquireRemoteLease("other", "other-user", "agent-b", false);
        _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/lease-test", new { name = "Lease", transport = "http", httpUrl = "https://example.com/mcp", enabled = false }, token);
        Assert.True(connectorLease.Token.IsCancellationRequested);
        Assert.False(otherLease.Token.IsCancellationRequested);
        using RemoteMcpOperationLease callerLease = fixture.AcquireRemoteLease("other", userId, "agent-a", false);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, $"/api/settings/users/{userId}/active", new { isActive = false }, token);
        Assert.True(callerLease.Token.IsCancellationRequested);
        Assert.False(otherLease.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RemoteCredentialMutationsPreserveBlankAndReportEffectiveFallback()
    {
        await using var fixture = await ConnectorApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        foreach (string id in new[] { "first", "second" })
            _ = await fixture.SendJsonAsync(HttpMethod.Post, "/api/connectors/" + id,
                new { name = id, transport = "http", httpUrl = "https://example.com/mcp", credentials = new[] { new { key = "key", label = "Key", required = false } }, httpHeaders = new[] { new { name = "X-Api-Key", credentialKey = "key" } } }, token);
        string userId = fixture.AdminUserId();
        string otherBefore = fixture.GetCredentialRevision("second", userId, true);
        string before = fixture.GetCredentialRevision("first", userId, true);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first/system", new { entries = new { key = "system-value" } }, token);
        string after = fixture.GetCredentialRevision("first", userId, true);
        Assert.NotEqual(before, after);
        Assert.Equal(otherBefore, fixture.GetCredentialRevision("second", userId, true));
        using JsonDocument system = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/first", null, token);
        Assert.Equal("system", system.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { key = "user-value" } }, token);
        Assert.NotEqual(after, fixture.GetCredentialRevision("first", userId, true));
        using JsonDocument personal = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/first", null, token);
        Assert.Equal("user", personal.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        string beforeReplace = fixture.GetCredentialRevision("first", userId, true);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { key = "replacement-value" } }, token);
        Assert.NotEqual(beforeReplace, fixture.GetCredentialRevision("first", userId, true));
        string unchanged = fixture.GetCredentialRevision("first", userId, true);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { key = " " } }, token);
        Assert.Equal(unchanged, fixture.GetCredentialRevision("first", userId, true));
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { }, removeKeys = new[] { "key" } }, token);
        using JsonDocument fallback = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/first", null, token);
        Assert.Equal("system", fallback.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { }, httpUrl = "https://personal.example/mcp" }, token);
        using JsonDocument overrideStatus = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/first", null, token);
        Assert.Equal("none", overrideStatus.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        Assert.False(overrideStatus.RootElement.GetProperty("credentials")[0].GetProperty("systemConfigured").GetBoolean());
        using var bypass = new HttpRequestMessage(HttpMethod.Put, "/api/credentials/user")
        {
            Content = JsonContent.Create(new { entries = new Dictionary<string, string> { [fixture.RemoteCredentialKey("first", "https://example.com/mcp", "key")] = "invalid" } }, options: jsonOptions),
        };
        bypass.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage rejected = await fixture.Client.SendAsync(bypass, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using JsonDocument isolated = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/second", null, token);
        Assert.Equal("none", isolated.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
        using var rejectedSystem = new HttpRequestMessage(HttpMethod.Put, "/api/credentials/connector/first/system")
        {
            Content = JsonContent.Create(new { entries = new { }, removeKeys = new[] { "key" } }, options: jsonOptions),
        };
        rejectedSystem.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage rejectedSystemResponse = await fixture.Client.SendAsync(rejectedSystem, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedSystemResponse.StatusCode);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first", new { entries = new { }, httpUrl = "https://example.com/mcp" }, token);
        _ = await fixture.SendJsonAsync(HttpMethod.Put, "/api/credentials/connector/first/system", new { entries = new { }, removeKeys = new[] { "key" } }, token);
        using JsonDocument missing = await fixture.SendJsonAsync(HttpMethod.Get, "/api/credentials/connector/first", null, token);
        Assert.Equal("none", missing.RootElement.GetProperty("credentials")[0].GetProperty("effectiveScope").GetString());
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
                credentials = new[] { new { key = "MCP_AUTH_TOKEN", label = "Token", required = false } },
                httpHeaders = new[] { new { name = "Authorization", credentialKey = "MCP_AUTH_TOKEN", prefix = "Bearer " } },
                enabled = true,
            },
            accessToken).ConfigureAwait(true);

        string key = fixture.RemoteCredentialKey("homeassistant-mcp", "https://attacker.example/collect", "MCP_AUTH_TOKEN");
        await fixture.SetSystemCredentialAsync(key, "system-token");

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
        Assert.Equal("https://attacker.example/collect", response.RootElement.GetProperty("message").GetString());
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequest();
        Assert.Equal("https://attacker.example/collect", captured.HttpUrl);
        Assert.Null(captured.TestEnv);
        Assert.NotNull(captured.RemoteContext);
        Assert.False(captured.RemoteContext.IncludeSystemScope);
        IReadOnlyDictionary<string, string> resolved = await captured.RemoteContext.Resolve([key], TestContext.Current.CancellationToken);
        Assert.Equal("user-token", resolved[key]);
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

        string? bundlePath = response.RootElement.GetProperty("connector").GetProperty("bundlePath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(bundlePath));
        GatewayConnectorTestRequest captured = fixture.GetCapturedTestRequestForBundle(bundlePath);
        Assert.Equal("container", captured.Transport);
        Assert.Equal("node", captured.ServerType);
        Assert.Equal(bundlePath, captured.BundlePath);
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

        WorkspaceMcpContainerProbeRequest captured = Assert.Single(manager.ContainerProbeRequests, request => request.ConnectorId == "splose-mcp");

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

        WorkspaceMcpContainerProbeRequest captured = Assert.Single(manager.ContainerProbeRequests, request => request.ConnectorId == "probe-local-normalized");

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

    private sealed class AuditCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new AuditLogger(Messages);
        public void Dispose() { }
        private sealed class AuditLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 7340) messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class ConnectorApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly WebApplication app;

        private ConnectorApiFixture(string root, string? previousEnvironment, WebApplication app, AuditCapture audit, FakeCredentialsServiceClient credentials)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.app = app;
            Client = app.GetTestClient();
            AuditMessages = audit.Messages;
            Credentials = credentials;
        }

        public HttpClient Client { get; }
        public ConcurrentQueue<string> AuditMessages { get; }
        public FakeCredentialsServiceClient Credentials { get; }

        public string ConnectorUpdatedAt(string id) => app.Services.GetRequiredService<GatewayState>().McpServers[id].UpdatedAt;
        public void MarkConnectorLegacy(string id)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            state.McpServers[id] = state.McpServers[id] with { RequiresCredentialReentry = true };
        }
        public RemoteMcpOperationLease AcquireConnectorLease(string id, string userId)
            => app.Services.GetRequiredService<RemoteMcpCredentialRevisionTracker>().Acquire(id, userId, null, includeSystem: false);

        public void DemoteBootstrapAdmin()
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            KeyValuePair<string, GatewayUser> entry = state.Users.Single(pair => pair.Value.Role == "admin");
            state.Users[entry.Key] = entry.Value with { Role = "user" };
        }

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
            var audit = new AuditCapture();
            builder.Logging.AddProvider(audit);
            if (captureConnectorTests)
            {
                builder.Services.RemoveAll<IMcpConnectorTestService>();
                builder.Services.AddSingleton<IMcpConnectorTestService, CapturingConnectorTestService>();
            }

            builder.Services.RemoveAll<IManagerGrpcService>();
            builder.Services.AddSingleton(managerGrpcService ?? new FakeManagerGrpcService());

            builder.Services.RemoveAll<CredentialResolverService>();
            var credentials = new FakeCredentialsServiceClient();
            builder.Services.AddSingleton(new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(credentials)));
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync().ConfigureAwait(true);

            return new ConnectorApiFixture(root, previousEnvironment, app, audit, credentials);
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

        public GatewayConnectorTestRequest GetCapturedTestRequestForBundle(string? bundlePath)
            => app.Services.GetRequiredService<IMcpConnectorTestService>() is CapturingConnectorTestService service
                ? service.Requests.FirstOrDefault(request => request.BundlePath == bundlePath)
                    ?? throw new XunitException("No connector test request was captured for the imported bundle.")
                : throw new XunitException("Capturing connector test service is not registered.");

        public string RemoteCredentialKey(string id, string endpoint, string key)
        {
            GatewayMcpServer server = app.Services.GetRequiredService<IGatewayCatalogStore>().ListMcpServers().Single(item => item.Id == id);
            return ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace!, new Uri(endpoint), key);
        }

        public string AdminUserId() => app.Services.GetRequiredService<GatewayState>().Users.Values.Single(user => user.Role == "admin").Id;

        public RemoteMcpOperationLease AcquireRemoteLease(string connectorId, string userId, string? agentId, bool includeSystem)
            => app.Services.GetRequiredService<RemoteMcpCredentialRevisionTracker>().Acquire(connectorId, userId, agentId, includeSystem);

        public string GetCredentialRevision(string connectorId, string userId, bool includeSystem)
            => app.Services.GetRequiredService<RemoteMcpCredentialRevisionTracker>().Get(connectorId, userId, includeSystem);

        public async Task SetSystemCredentialAsync(string key, string value)
        {
            var resolver = app.Services.GetRequiredService<CredentialResolverService>();
            await resolver.SetSystemAsync(new Dictionary<string, string> { [key] = value }, TestContext.Current.CancellationToken);
        }

        public GatewayConnectorTestRequest GetCapturedTestRequestForUrl(string url)
            => app.Services.GetRequiredService<IMcpConnectorTestService>() is CapturingConnectorTestService service
                ? service.Requests.Single(request => request.HttpUrl == url)
                : throw new XunitException("Capturing connector test service is not registered.");
    }

    private sealed class CapturingConnectorTestService : IMcpConnectorTestService
    {
        public ConcurrentQueue<GatewayConnectorTestRequest> Requests { get; } = new();

        public GatewayConnectorTestRequest? LastRequest { get; private set; }

        public McpConnectorTestResult? NextResult { get; set; }

        public Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
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
        public ConcurrentQueue<WorkspaceMcpContainerProbeRequest> ContainerProbeRequests { get; } = new();

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBoolResponse { Ok = true });

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerStatusResponse { Ok = true });

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true, Message = "connector-validation-test" });

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new RpcException(new Status(StatusCode.Unavailable, "Test manager unavailable."));

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
            ContainerProbeRequests.Enqueue(request);
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
        private TaskCompletionSource<bool>? blocked;
        public TaskCompletionSource<bool> BlockStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockNextAssociationRead()
        {
            BlockStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            blocked = release; return release;
        }
        private sealed record Association(Dictionary<string, Dictionary<string, string>> Users, Dictionary<string, string> System);
        private readonly Dictionary<string, Association> associations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> operations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Actor, ConnectorDraftMetadata Metadata)> stagedDrafts = new(StringComparer.Ordinal);
        private Association CloneAssociation(string source)
        {
            if (source.Length == 0) return new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            Association existing = associations[source];
            return new(existing.Users.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal), new(existing.System, StringComparer.Ordinal));
        }
        private string Stage(string operation, string source, string actor, string scope, IEnumerable<KeyValuePair<string, string>> changes, IEnumerable<string> removals)
        {
            if (operations.TryGetValue(operation, out string? known)) return known;
            Association association = CloneAssociation(source);
            if (!association.Users.TryGetValue(actor, out Dictionary<string, string>? user)) association.Users[actor] = user = new(StringComparer.Ordinal);
            Dictionary<string, string> target = scope == "system" ? association.System : user;
            foreach ((string key, string value) in changes) target[key] = value;
            foreach (string key in removals) target.Remove(key);
            string id = Guid.NewGuid().ToString("N"); associations[id] = association; operations[operation] = id; return id;
        }
        public override AsyncUnaryCall<StageConnectorAssociationResponse> StageConnectorMutationAsync(StageConnectorMutationRequest request, CallOptions options)
            => Wrap(new StageConnectorAssociationResponse { AssociationId = Stage(request.OperationId, request.SourceAssociationId, request.Access.ActorId, request.CredentialScope, request.Entries, request.RemoveKeys) });
        public override AsyncUnaryCall<StageConnectorAssociationResponse> StageConnectorAssociationAsync(StageConnectorAssociationRequest request, CallOptions options)
        {
            var draft = drafts[request.DraftId].Request;
            stagedDrafts[request.DraftId] = (request.Access.ActorId, drafts[request.DraftId].Metadata.Clone());
            return Wrap(new StageConnectorAssociationResponse { AssociationId = Stage(request.DraftId, request.SourceAssociationId, request.Access.ActorId, draft.CredentialScope, draft.Entries, draft.RemoveKeys) });
        }
        public override AsyncUnaryCall<ConnectorDraftMetadata> GetStagedConnectorDraftAsync(ConnectorDraftAccessRequest request, CallOptions options)
        {
            if (!stagedDrafts.TryGetValue(request.DraftId, out var staged) || staged.Actor != request.Access.ActorId) throw new RpcException(new Status(StatusCode.NotFound, "Draft not found."));
            return Wrap(staged.Metadata.Clone());
        }
        public override AsyncUnaryCall<CredentialMapResponse> ResolveConnectorAssociationAsync(ResolveConnectorAssociationRequest request, CallOptions options)
        {
            Association association = associations[request.AssociationId]; var response = new CredentialMapResponse();
            association.Users.TryGetValue(request.UserId, out Dictionary<string, string>? user);
            foreach (string key in request.RequiredKeys)
            {
                if (user is not null && user.TryGetValue(key, out string? value)) response.Entries[key] = value;
                else if (request.IncludeSystemScope && association.System.TryGetValue(key, out value)) response.Entries[key] = value;
            }
            TaskCompletionSource<bool>? barrier = Interlocked.Exchange(ref blocked, null);
            if (barrier is null) return Wrap(response);
            BlockStarted.TrySetResult(true);
            async Task<CredentialMapResponse> CompleteAsync() { await barrier.Task.ConfigureAwait(false); return response; }
            return new AsyncUnaryCall<CredentialMapResponse>(CompleteAsync(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), static () => { });
        }
        public override AsyncUnaryCall<ConnectorAssociationPresenceResponse> GetConnectorAssociationPresenceAsync(ResolveConnectorAssociationRequest request, CallOptions options)
        {
            Association association = associations[request.AssociationId]; var response = new ConnectorAssociationPresenceResponse();
            association.Users.TryGetValue(request.UserId, out Dictionary<string, string>? user);
            foreach (string key in request.RequiredKeys)
            {
                if (user?.ContainsKey(key) == true) response.UserKeys.Add(key);
                if (request.IncludeSystemScope && association.System.ContainsKey(key)) response.SystemKeys.Add(key);
            }
            return Wrap(response);
        }
        private readonly Dictionary<string, (CreateConnectorDraftRequest Request, ConnectorDraftMetadata Metadata)> drafts = new(StringComparer.Ordinal);
        public override AsyncUnaryCall<ConnectorDraftListResponse> ListConnectorDraftsAsync(ListConnectorDraftsRequest request, CallOptions options)
        {
            var response = new ConnectorDraftListResponse();
            response.Drafts.AddRange(drafts.Values.Where(entry => entry.Request.Access.ActorId == request.Access.ActorId).Select(entry => entry.Metadata.Clone()));
            return Wrap(response);
        }
        public override AsyncUnaryCall<ConnectorDraftMetadata> CreateConnectorDraftAsync(CreateConnectorDraftRequest request, CallOptions options)
        {
            var metadata = new ConnectorDraftMetadata { DraftId = request.DraftId, ConnectorId = request.ConnectorId, CredentialScope = request.CredentialScope, CandidateJson = request.CandidateJson, BaseUpdatedAt = request.BaseUpdatedAt, EffectiveEndpoint = request.EffectiveEndpoint, BaseEndpoint = request.BaseEndpoint, UseDurableCredentials = request.UseDurableCredentials, CredentialNamespace = request.CredentialNamespace, SourceAssociationId = request.SourceAssociationId, ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds() };
            drafts[request.DraftId] = (request.Clone(), metadata);
            return Wrap(metadata.Clone());
        }
        public override AsyncUnaryCall<ConnectorDraftMetadata> GetConnectorDraftAsync(ConnectorDraftAccessRequest request, CallOptions options)
        {
            if (!drafts.TryGetValue(request.DraftId, out var entry) || entry.Request.Access.ActorId != request.Access.ActorId)
                throw new RpcException(new Status(StatusCode.NotFound, "Draft not found."));
            return Wrap(entry.Metadata.Clone());
        }
        public override AsyncUnaryCall<CredentialMapResponse> ResolveConnectorDraftAsync(ResolveConnectorDraftRequest request, CallOptions options)
        {
            var entry = drafts[request.DraftId];
            var result = new CredentialMapResponse();
            foreach (string key in request.RequiredKeys)
                if (entry.Request.Entries.TryGetValue(key, out string? value)) result.Entries[key] = value;
            return Wrap(result);
        }
        public override AsyncUnaryCall<DeleteCredentialResponse> CancelConnectorDraftAsync(ConnectorDraftAccessRequest request, CallOptions options)
        {
            bool deleted = drafts.Remove(request.DraftId) | stagedDrafts.Remove(request.DraftId);
            return Wrap(new DeleteCredentialResponse { Deleted = deleted });
        }
        public override AsyncUnaryCall<DeleteCredentialResponse> DiscardConnectorDraftInputAsync(ConnectorDraftAccessRequest request, CallOptions options)
            => Wrap(new DeleteCredentialResponse { Deleted = drafts.Remove(request.DraftId) });
        private Dictionary<string, string> GetUserStore(string id)
        {
            if (!userStore.TryGetValue(id, out Dictionary<string, string>? values)) userStore[id] = values = new(StringComparer.Ordinal);
            return values;
        }
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

                if (request.IncludeSystemScope && systemStore.TryGetValue(key, out string? systemValue))
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
