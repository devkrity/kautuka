using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Providers;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;
using Kautuka.NetworkAccess.Client;
using Kautuka.NetworkAccessController;
using Kautuka.Manager.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

// ─────────────────────────────────────────────────────────────────────────────
// Full-fixture HTTP tests — provider CRUD
// ─────────────────────────────────────────────────────────────────────────────

[Collection(ProcessEnvironmentTestGroup.Name)]
public sealed class ProviderSettingsEndpointTests
{
    [Fact]
    public async Task ProviderPermissionConfirmationIsBoundToTheDestinationAndPolicyRevision()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        const string path = "/api/settings/providers/save";
        var body = new JsonObject
        {
            ["id"] = "confirmation-test",
            ["type"] = "ollama",
            ["settings"] = new JsonObject { ["base_url"] = "http://192.168.1.12:11434" },
        };
        using HttpResponseMessage first = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("confirmation-test"));
        JsonNode? confirmation = JsonNode.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("192.168.1.12", confirmation?["destination"]?.GetValue<string>());
        Assert.True(confirmation?["allowLan"]?.GetValue<bool>());

        var admin = new NetworkAccessActor("admin", true);
        fixture.ProviderRules.SetRule(admin, new RuleWriteSpec("system", null, null, null, "other.example", "host", 443, false),
            fixture.ProviderRules.Revision);
        body["networkPermission"] = confirmation;
        using HttpResponseMessage stale = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("confirmation-test"));
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "192.168.1.12");

        body["networkPermission"] = JsonNode.Parse(await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body["settings"]!["base_url"] = "http://192.168.1.13:11434";
        using HttpResponseMessage changed = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("confirmation-test"));

        body["networkPermission"] = JsonNode.Parse(await changed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using HttpResponseMessage saved = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        NetworkAccessRule grant = Assert.Single(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "192.168.1.13").Rule;
        Assert.True(grant.Allowed && grant.AllowLan);
    }

    [Fact]
    public async Task SavingProviderReportsTheSavedIdWhenTheFinalRuleWriteFails()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        fixture.FailRuleWrite = true;
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "failed-rule",
                type = "ollama",
                settings = new { base_url = "http://localhost:11434" },
                networkPermission = new { destination = "localhost", port = 11434, allowLan = true, revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("ollama", fixture.GetConfiguredProviderType("failed-rule"));
        using JsonDocument details = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("failed-rule", details.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("provider_network_activation_incomplete", details.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(new NetworkAccessActor("admin", true)).Rules,
            view => view.Rule.Destination == "localhost" && view.Rule.Allowed);
    }

    [Fact]
    public async Task UpdatingProviderCanRetryTheSameSavedIdWhenTheFinalRuleWriteFails()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument initial = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/providers/save",
            new { id = "existing-provider", type = "ollama", settings = new { base_url = "http://localhost:11434" } }, token);
        fixture.FailRuleWrite = true;
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "existing-provider",
                type = "ollama",
                settings = new { base_url = "http://192.168.1.12:11434" },
                networkPermission = new { destination = "192.168.1.12", port = 11434, allowLan = true, revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using JsonDocument providers = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/providers", null, token);
        JsonElement saved = providers.RootElement.GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "existing-provider");
        Assert.Equal("http://192.168.1.12:11434", saved.GetProperty("settings").GetProperty("base_url").GetString());
        var admin = new NetworkAccessActor("admin", true);
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "192.168.1.12");

        fixture.FailRuleWrite = false;
        using HttpResponseMessage retry = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "existing-provider",
                type = "ollama",
                settings = new { base_url = "http://192.168.1.12:11434" },
                networkPermission = new { destination = "192.168.1.12", port = 11434, allowLan = true, revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }, token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Contains(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "192.168.1.12" && view.Rule.Allowed);
    }

    [Fact]
    public async Task TestConnectionUsesTemporaryAuthorizationAndEndsIt()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync(useControlledValidation: true);
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument response = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/providers/test",
            new { id = "test", type = "openai", confirmedTarget = new { host = "provider.example", port = 443 }, settings = new { base_url = "https://provider.example/v1", api_key = "test" } }, token);
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.True(fixture.TemporaryTestAllowed);
        Assert.True(fixture.TemporaryTestEnded);
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(new NetworkAccessActor("admin", true)).Rules, view => view.Rule.Destination == "provider.example");
    }

    [Fact]
    public async Task FailedCleanupPreservesTheProviderTestResultAndUnavailableManagerStopsDiscovery()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync(useControlledValidation: true);
        string token = await fixture.LoginAsBootstrapAdminAsync();
        var request = new
        {
            id = "local",
            type = "ollama",
            settings = new { base_url = "http://192.168.1.24:8199" },
            confirmedTarget = new { host = "192.168.1.24", port = 8199 }
        };
        fixture.ManagerUnavailable = true;
        using HttpResponseMessage unavailable = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/test", request, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.False(fixture.TemporaryTestAllowed);
        fixture.ManagerUnavailable = false;
        fixture.FailTestCleanup = true;
        using JsonDocument success = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/providers/test", request, token);
        Assert.True(success.RootElement.GetProperty("success").GetBoolean());
        Assert.True(fixture.TemporaryTestAllowed);
        Assert.False(fixture.TemporaryTestEnded);
    }

    [Fact]
    public async Task CancellingProviderDiscoveryEndsItsTemporaryGrant()
    {
        var validation = new PausedProviderValidation();
        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services =>
            services.AddSingleton<IProviderValidationService>(validation));
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> pending = fixture.SendCancellableAsync(HttpMethod.Post, "/api/settings/providers/test",
            new
            {
                id = "local",
                type = "ollama",
                settings = new { base_url = "http://192.168.1.24:8199" },
                confirmedTarget = new { host = "192.168.1.24", port = 8199 }
            }, token, cancellation.Token);
        await validation.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await fixture.TestGrantEnded.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(fixture.TemporaryTestEnded);
    }

    private sealed class PausedProviderValidation : IProviderValidationService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GatewayProviderTestResult> TestProviderAsync(ProviderTestDefinition definition, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected discovery to be cancelled.");
        }
    }

    [Fact]
    public async Task ProviderTestChecksEffectiveRulesAndUsesCatalogOrCustomTarget()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync(useControlledValidation: true);
        string token = await fixture.LoginAsBootstrapAdminAsync();
        const string path = "/api/settings/providers/test";
        var admin = new NetworkAccessActor("admin", true);
        using JsonDocument alias = await fixture.SendJsonAsync(HttpMethod.Post, path,
            new { id = "my-groq", type = "groq" }, token);
        Assert.Equal("api.groq.com", alias.RootElement.GetProperty("confirmationTarget").GetProperty("host").GetString());
        var local = new { id = "my-lan", type = "ollama", settings = new { base_url = "http://192.168.1.24:8199/custom" } };
        using JsonDocument localPrompt = await fixture.SendJsonAsync(HttpMethod.Post, path, local, token);
        Assert.Equal("192.168.1.24", localPrompt.RootElement.GetProperty("confirmationTarget").GetProperty("host").GetString());
        Assert.Equal(8199, localPrompt.RootElement.GetProperty("confirmationTarget").GetProperty("port").GetInt32());
        Assert.Empty(fixture.ProviderRules.ListRequests(admin).Requests);

        fixture.ProviderRules.SetRule(admin, new RuleWriteSpec("system", null, null, null, "192.168.1.24", "ip", 8199,
            true, AllowLan: true), fixture.ProviderRules.Revision);
        using HttpResponseMessage stale = await fixture.SendAsync(HttpMethod.Post, path,
            new { local.id, local.type, local.settings, confirmedTarget = new { host = "previous.example", port = 443 } }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.False(fixture.TemporaryTestAllowed);
        using JsonDocument allowed = await fixture.SendJsonAsync(HttpMethod.Post, path, local, token);
        Assert.True(allowed.RootElement.GetProperty("success").GetBoolean());
        Assert.False(fixture.TemporaryTestEnded);

        fixture.ProviderRules.SetRule(admin, new RuleWriteSpec("system", null, null, null, "192.168.1.24", "ip", 8199,
            false), fixture.ProviderRules.Revision);
        using HttpResponseMessage blocked = await fixture.SendAsync(HttpMethod.Post, path,
            new { local.id, local.type, local.settings, confirmedTarget = new { host = "192.168.1.24", port = 8199 } }, token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.False(fixture.TemporaryTestEnded);
    }

    [Fact]
    public async Task SavingProviderFailsWithoutChangingCatalogWhenManagerIsUnavailable()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        fixture.ManagerUnavailable = true;
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new { id = "offline-provider", type = "ollama", settings = new { base_url = "http://localhost:11434" } }, token);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("offline-provider"));
    }

    [Fact]
    public async Task SavingProviderPreservesMatchingSystemBlock()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        var admin = new NetworkAccessActor("admin", true);
        var (revision, _) = fixture.ProviderRules.ListRules(admin);
        fixture.ProviderRules.SetRule(admin, new RuleWriteSpec("system", null, null, null, "provider.example", "host", 443, false), revision);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new { id = "openai-test", type = "openai", settings = new { base_url = "https://provider.example/v1", api_key = "test" } }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("openai-test"));
        NetworkAccessRule rule = Assert.Single(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "provider.example").Rule;
        Assert.False(rule.Allowed);
        Assert.False(rule.AllowLan);
        Assert.Equal("system", rule.ScopeKind);
    }

    [Fact]
    public async Task FailedCredentialWritePublishesNeitherProviderSettingsNorTheConfirmedAllow()
    {
        var credentials = new FakeCredentialsServiceClient { FailSystemWrites = true };
        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<CredentialResolverService>();
            services.AddSingleton(new CredentialResolverService(
                new Lazy<CredentialsService.CredentialsServiceClient>(credentials)));
        });
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "credential-write-failed",
                type = "openai",
                settings = new { base_url = "https://provider.example/v1", api_key = "fixture-key" },
                networkPermission = new
                {
                    destination = "provider.example",
                    port = 443,
                    allowLan = false,
                    revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
            }, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("credential-write-failed"));
        var admin = new NetworkAccessActor("admin", true);
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "provider.example");
    }

    [Fact]
    public async Task FailedCatalogWriteDoesNotPublishTheConfirmedAllow()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        await fixture.SetCatalogWriteFailureAsync(fail: true);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "failed-catalog",
                type = "ollama",
                settings = new { base_url = "http://192.168.1.12:11434" },
                networkPermission = new { destination = "192.168.1.12", port = 11434, allowLan = true, revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }, token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await fixture.SetCatalogWriteFailureAsync(fail: false);
        Assert.Null(fixture.GetConfiguredProviderType("failed-catalog"));
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(new NetworkAccessActor("admin", true)).Rules,
            view => view.Rule.Destination == "192.168.1.12");
    }

    [Fact]
    public async Task FinalProviderPermissionWriteRespectsABlockAddedAfterConfirmation()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        var admin = new NetworkAccessActor("admin", true);
        fixture.BeforeRuleWrite = () => fixture.ProviderRules.SetRule(admin,
            new RuleWriteSpec("system", null, null, null, "provider.example", "host", 443, false), fixture.ProviderRules.Revision);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "concurrent-block",
                type = "openai",
                settings = new { base_url = "https://provider.example/v1", api_key = "fixture-key" },
                networkPermission = new { destination = "provider.example", port = 443, allowLan = false, revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("openai", fixture.GetConfiguredProviderType("concurrent-block"));
        Assert.False(Assert.Single(fixture.ProviderRules.ListRules(admin).Rules,
            view => view.Rule.Destination == "provider.example").Rule.Allowed);
    }

    [Fact]
    public async Task LostCredentialAcknowledgementReservesTheIdAndAllowsAFreshIdRetry()
    {
        var credentials = new FakeCredentialsServiceClient { LoseSystemWriteAcknowledgement = true };
        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<CredentialResolverService>();
            services.AddSingleton(new CredentialResolverService(
                new Lazy<CredentialsService.CredentialsServiceClient>(credentials)));
        });
        string token = await fixture.LoginAsBootstrapAdminAsync();
        const string path = "/api/settings/providers/save";
        var body = new
        {
            id = "ambiguous-write",
            type = "openai",
            settings = new { base_url = "https://provider.example/v1", api_key = "fixture-key" },
            networkPermission = new
            {
                destination = "provider.example",
                port = 443,
                allowLan = false,
                revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
        };
        using HttpResponseMessage interrupted = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType(body.id));
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(new NetworkAccessActor("admin", true)).Rules,
            view => view.Rule.Destination == "provider.example");

        credentials.LoseSystemWriteAcknowledgement = false;
        using HttpResponseMessage reserved = await fixture.SendAsync(HttpMethod.Post, path, body, token);
        Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);
        using JsonDocument conflict = JsonDocument.Parse(await reserved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("provider_id_reserved", conflict.RootElement.GetProperty("code").GetString());
        Assert.Null(fixture.GetConfiguredProviderType(body.id));

        using HttpResponseMessage retried = await fixture.SendAsync(HttpMethod.Post, path, body with { id = "fresh-provider-id" }, token);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal("openai", fixture.GetConfiguredProviderType("fresh-provider-id"));
    }

    [Fact]
    public async Task ProviderConfirmationCannotOverrideSystemPause()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        var admin = new NetworkAccessActor("admin", true);
        fixture.ProviderRules.SetBrake(admin, "system", null, on: true);
        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, "/api/settings/providers/save",
            new
            {
                id = "paused-provider",
                type = "ollama",
                settings = new { base_url = "http://192.168.1.12:11434" },
                networkPermission = new
                {
                    destination = "192.168.1.12",
                    port = 11434,
                    allowLan = true,
                    revision = fixture.ProviderRules.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
            }, token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(fixture.GetConfiguredProviderType("paused-provider"));
        Assert.DoesNotContain(fixture.ProviderRules.ListRules(admin).Rules, view => view.Rule.Destination == "192.168.1.12");
    }

    [Fact]
    public async Task TestConnectionRequestsConsentBeforeDiscoveryAndRechecksPolicyAtSendTime()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            await using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            var controller = new NetworkAccessController.NetworkAccessController(policyStore, new WorkloadRegistry());
            var providerTypeCatalog = new SeededProviderTypeCatalog();
            var checker = new ProviderTestNetworkAccessChecker(controller);
            var aiClientFactory = new KautukaAiClientFactory(
                [new OpenAiCompatibleProvider("openai", checker: checker)],
                new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(new FakeCredentialsServiceClient())),
                providerTypeCatalog);
            var validation = new ProviderValidationService(
                aiClientFactory,
                NullLogger<ProviderValidationService>.Instance);
            await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services =>
            {
                services.AddSingleton<IProviderValidationService>(validation);
                services.AddSingleton<IGatewayAuthGuard, ProviderTestAdminAuthGuard>();
            });
            string token = await fixture.LoginAsBootstrapAdminAsync();
            using JsonDocument confirmation = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/providers/test",
                new
                {
                    provider_id = "custom-openai",
                    type = "openai",
                    settings = new { base_url = "https://api.provider.test/v1" },
                    confirmedTarget = new { host = "previous.provider.test", port = 443 }
                }, token);
            Assert.Equal("api.provider.test", confirmation.RootElement.GetProperty("confirmationTarget").GetProperty("host").GetString());
            Assert.Empty(checker.Targets);
            using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post,
                "/api/settings/providers/test",
                new
                {
                    confirmedTarget = new { host = "api.provider.test", port = 443 },
                    provider_id = "custom-openai",
                    type = "openai",
                    settings = new
                    {
                        base_url = "https://api.provider.test/v1",
                        api_key = "test-key",
                    },
                },
                token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(fixture.TemporaryTestEnded);
            Assert.Equal("api.provider.test", Assert.Single(checker.Targets).Host);
            Assert.Equal("admin-1", checker.Scope?.UserId);
            Assert.Equal("provider-test", checker.Scope?.Purpose);
            Assert.False(string.IsNullOrWhiteSpace(checker.Scope?.CorrelationId));

            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            NetworkAccessRequest? pending = policyStore.ListRequests(admin).Requests
                .Select(static view => view.Request)
                .SingleOrDefault(request => request.Destination == "api.provider.test" && request.Port == 443 && request.UserId == "admin-1");
            Assert.NotNull(pending);
            Assert.Equal("gateway", pending.AgentId);
            Assert.Equal("user-agent", pending.Attribution);
            Assert.DoesNotContain(policyStore.ListRules(admin).Rules, view => view.Rule.Destination == "api.provider.test");
            await policyStore.FlushPendingWritesAsync();
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private sealed class ProviderTestNetworkAccessChecker(NetworkAccessController.NetworkAccessController controller) : INetworkAccessChecker
    {
        public NetworkAccessScope? Scope { get; private set; }
        public List<OutboundTarget> Targets { get; } = [];

        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
        {
            Scope = NetworkAccessContext.Current;
            Targets.Add(target);
            NetworkAccessDecisionResult decision = controller.EvaluateOutboundCheck(new OutboundCheckInput(
                target.UserId ?? string.Empty,
                target.AgentId ?? string.Empty,
                target.ConnectorId,
                target.Host,
                target.Port,
                target.Purpose,
                target.CorrelationId));
            return Task.FromResult(new NetworkAccessCheckResult(
                decision.Allowed,
                decision.Reason,
                decision.RequestId,
                decision.OpenInternetBadge,
                NetworkAccessDenialKind.Denied));
        }
    }

    private sealed class ProviderTestAdminAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user) { user = Admin(); return true; }
        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user) { user = Admin(); return null; }
        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user) { user = Admin(); return null; }
        private static GatewayUser Admin() => new(
            Id: "admin-1", Username: "admin", Password: "", Role: "admin", IsActive: true,
            Email: "admin@example.test", DisplayName: "Admin");
    }

    [Fact]
    public async Task SaveProviderPersistsToCatalog()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "ollama-local", type = "ollama", settings = new { base_url = "http://localhost:11434" } },
            token);

        Assert.Equal("ollama-local", saved.RootElement.GetProperty("provider").GetProperty("id").GetString());
        Assert.Equal("ollama", saved.RootElement.GetProperty("provider").GetProperty("type").GetString());
        Assert.False(saved.RootElement.GetProperty("provider").GetProperty("hasApiKey").GetBoolean());
        NetworkAccessRule rule = Assert.Single(fixture.ProviderRules.ListRules(new NetworkAccessActor("admin", true)).Rules,
            view => view.Rule.ScopeKind == "system" && view.Rule.Destination == "localhost" && view.Rule.Port == 11434).Rule;
        Assert.True(rule.Allowed);
        Assert.True(rule.AllowLan);

        using JsonDocument list = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/providers", null, token);
        JsonElement providers = list.RootElement.GetProperty("providers");
        Assert.True(providers.EnumerateArray().Any(p => p.GetProperty("id").GetString() == "ollama-local"),
            "Saved provider 'ollama-local' must appear in the list.");
    }

    [Fact]
    public async Task SaveProviderStripsApiKeyFromCatalogSettings()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        // Save with api_key — the endpoint strips api_key before writing to catalog,
        // then forwards it to CredentialsHost (captured by FakeCredentialsServiceClient).
        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "openai-main", type = "openai", settings = new { api_key = "sk-test-key" } },
            token);

        JsonElement settingsEl = saved.RootElement.GetProperty("provider").GetProperty("settings");
        Assert.False(
            settingsEl.TryGetProperty("api_key", out _),
            "api_key must not be stored in the provider catalog settings.");
        Assert.False(
            settingsEl.TryGetProperty("apiKey", out _),
            "api_key must not be stored in the provider catalog settings.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fixture-replacement-key")]
    public async Task SavedCredentialCannotMoveToADifferentProviderDestination(string? replacement)
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        const string path = "/api/settings/providers/save";
        const string original = "https://api.openai.com/v1";
        using JsonDocument saved = await fixture.SendJsonAsync(HttpMethod.Post, path,
            new { id = "bound-provider", type = "openai", settings = new { base_url = original, api_key = "fixture-key" } }, token);

        using HttpResponseMessage changed = await fixture.SendAsync(HttpMethod.Post, path,
            new { id = "bound-provider", type = "openai", settings = new { base_url = "https://provider.example.test/v1", api_key = replacement } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);

        using JsonDocument same = await fixture.SendJsonAsync(HttpMethod.Post, path,
            new { id = "bound-provider", type = "openai", settings = new { base_url = original, http_timeout_seconds = 40 } }, token);
        Assert.True(same.RootElement.GetProperty("provider").GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task SaveProviderCopiesCatalogDefaultsWhenSettingsAreOmitted()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "openai-defaulted", type = "openai", settings = new { api_key = "key" } },
            token);

        JsonElement settingsEl = saved.RootElement.GetProperty("provider").GetProperty("settings");
        Assert.Equal("https://api.openai.com/v1", settingsEl.GetProperty("base_url").GetString());
        Assert.Equal(300, settingsEl.GetProperty("http_timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task SaveProviderRejectsUnknownProviderType()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "unknown-main", type = "unknown-provider", settings = new { } },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not supported", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveProviderCanonicalizesProviderTypeIdentifier()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "openai-main", type = " OPENAI ", settings = new { api_key = "key" } },
            token);

        Assert.Equal("openai", fixture.GetConfiguredProviderType("openai-main"));
    }

    [Fact]
    public async Task SaveProviderRejectsMissingCredentialRequiredByProviderType()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "missing-key-check",
                type = "openai",
                settings = new
                {
                    base_url = "https://api.openai.com/v1",
                    api_key = (string?)null,
                    http_timeout_seconds = 30,
                },
            },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("requires an API key", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Null(fixture.GetConfiguredProviderType("missing-key-check"));
    }

    [Theory]
    [InlineData("not-a-url", 30, "absolute HTTP or HTTPS URL")]
    [InlineData("http://localhost:11434", 0, "between 1 and 3600 seconds")]
    [InlineData("http://localhost:11434", 3601, "between 1 and 3600 seconds")]
    public async Task SaveProviderRejectsInvalidSharedProviderSettings(string endpointText, int timeoutSeconds, string expectedMessage)
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "ollama-invalid",
                type = "ollama",
                settings = new { base_url = endpointText, http_timeout_seconds = timeoutSeconds },
            },
            token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvancedSettingsCatalogEditsOnlyAffectFutureProviders()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument original = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "openai-before", type = "openai", settings = new { api_key = "key" } },
            token);

        using JsonDocument updatedAdvanced = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/advanced",
            new
            {
                providerTypeCatalog = new[]
                {
                    new
                    {
                        type = "openai",
                        friendlyName = "OpenAI via Proxy",
                        defaultBaseUrl = "https://proxy.example/v1",
                        defaultHttpTimeoutSeconds = 45,
                    },
                },
            },
            token);

        JsonElement catalogEntry = updatedAdvanced.RootElement
            .GetProperty("providerTypeCatalog")
            .EnumerateArray()
            .First(static entry => entry.GetProperty("type").GetString() == "openai");
        Assert.Equal("OpenAI via Proxy", catalogEntry.GetProperty("friendlyName").GetString());
        Assert.Equal("https://proxy.example/v1", catalogEntry.GetProperty("defaultBaseUrl").GetString());
        Assert.Equal(45, catalogEntry.GetProperty("defaultHttpTimeoutSeconds").GetInt32());

        using JsonDocument future = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "openai-after", type = "openai", settings = new { api_key = "key" } },
            token);

        JsonElement originalSettings = original.RootElement.GetProperty("provider").GetProperty("settings");
        Assert.Equal("https://api.openai.com/v1", originalSettings.GetProperty("base_url").GetString());
        Assert.Equal(300, originalSettings.GetProperty("http_timeout_seconds").GetInt32());

        JsonElement futureSettings = future.RootElement.GetProperty("provider").GetProperty("settings");
        Assert.Equal("https://proxy.example/v1", futureSettings.GetProperty("base_url").GetString());
        Assert.Equal(45, futureSettings.GetProperty("http_timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task SaveProviderWithApiKeyReportsHasApiKeyTrue()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "anthropic-main", type = "anthropic", settings = new { api_key = "sk-ant-test" } },
            token);

        // FakeCredentialsServiceClient captures the write and returns it on list —
        // so the endpoint's post-save ListSystemKeysAsync should see the key.
        Assert.True(saved.RootElement.GetProperty("provider").GetProperty("hasApiKey").GetBoolean());
    }

    [Fact]
    public async Task DeleteProviderRemovesItFromList()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "to-delete", type = "ollama", settings = new { base_url = "http://localhost:11434" } },
            token);

        using JsonDocument deleted = await fixture.SendJsonAsync(
            HttpMethod.Delete, "/api/settings/providers/to-delete", null, token);
        Assert.True(deleted.RootElement.GetProperty("success").GetBoolean());

        using JsonDocument list = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/providers", null, token);
        Assert.False(list.RootElement.GetProperty("providers").EnumerateArray().Any(p => p.GetProperty("id").GetString() == "to-delete"),
            "Deleted provider 'to-delete' must not appear in the list.");
    }

    [Fact]
    public async Task ProviderTypesEndpointReturnsKnownTypes()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendJsonAsync(
            HttpMethod.Get, "/api/settings/provider-types", null, token);

        JsonElement types = response.RootElement.GetProperty("providerTypes");
        Assert.True(types.GetArrayLength() > 0, "At least one provider type must be registered.");

        // Ollama is a built-in type that requires a base URL but no API key.
        JsonElement? ollama = FindType(types, "ollama");
        Assert.NotNull(ollama);
        Assert.False(ollama.Value.GetProperty("requiresApiKey").GetBoolean());
        Assert.True(ollama.Value.GetProperty("requiresBaseUrl").GetBoolean());

        JsonElement? openRouter = FindType(types, "openrouter");
        Assert.NotNull(openRouter);
        Assert.True(openRouter.Value.GetProperty("requiresApiKey").GetBoolean());
        Assert.False(openRouter.Value.GetProperty("requiresBaseUrl").GetBoolean());

        JsonElement? openaiResponses = FindType(types, "openai-responses");
        Assert.NotNull(openaiResponses);
        Assert.True(openaiResponses.Value.GetProperty("requiresApiKey").GetBoolean());
        Assert.False(openaiResponses.Value.GetProperty("requiresBaseUrl").GetBoolean());
    }

    [Theory]
    [InlineData("ollama", "http://localhost:11434", null)]
    [InlineData("openai", null, "key")]
    [InlineData("openai-responses", null, "key")]
    [InlineData("azure-openai", "https://example.openai.azure.com/openai/v1", "key")]
    [InlineData("foundry", "https://example.services.ai.azure.com/openai/v1", "key")]
    [InlineData("anthropic", null, "key")]
    [InlineData("groq", null, "key")]
    [InlineData("mistral", null, "key")]
    [InlineData("openrouter", null, "key")]
    [InlineData("gemini", null, "key")]
    public async Task EveryCatalogProviderTypeRetainsItsCanonicalRuntimeIdentity(
        string providerType,
        string? endpointText,
        string? apiKey)
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        string providerId = $"{providerType}-main";

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = providerId,
                type = providerType,
                settings = new
                {
                    base_url = endpointText,
                    api_key = apiKey,
                    http_timeout_seconds = 30,
                },
            },
            token);

        Assert.Equal(providerType, fixture.GetConfiguredProviderType(providerId));
    }

    private static JsonElement? FindType(JsonElement types, string typeId)
    {
        foreach (JsonElement t in types.EnumerateArray())
        {
            if (string.Equals(t.GetProperty("type").GetString(), typeId, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Full-fixture HTTP tests — model CRUD
// ─────────────────────────────────────────────────────────────────────────────

[Collection(ProcessEnvironmentTestGroup.Name)]
public sealed class ModelSettingsEndpointTests
{
    [Fact]
    public async Task SaveModelPersistsToCatalog()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "ollama-local", type = "ollama", settings = new { base_url = "http://localhost:11434" } },
            token);

        using JsonDocument saved = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new { id = "gemma3-9b", providerId = "ollama-local", modelName = "gemma3:9b", settings = new { } },
            token);

        Assert.Equal("gemma3-9b", saved.RootElement.GetProperty("model").GetProperty("id").GetString());
        Assert.Equal("ollama-local", saved.RootElement.GetProperty("model").GetProperty("providerId").GetString());
        Assert.Equal("gemma3:9b", saved.RootElement.GetProperty("model").GetProperty("modelName").GetString());

        using JsonDocument list = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/models", null, token);
        Assert.True(list.RootElement.GetProperty("models").EnumerateArray().Any(m => m.GetProperty("id").GetString() == "gemma3-9b"),
            "Saved model 'gemma3-9b' must appear in the list.");
    }

    [Fact]
    public async Task DeleteModelRemovesItFromList()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new { id = "ollama-local", type = "ollama", settings = new { base_url = "http://localhost:11434" } },
            token);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new { id = "model-to-delete", providerId = "ollama-local", modelName = "llama3:8b", settings = new { } },
            token);

        using JsonDocument deleted = await fixture.SendJsonAsync(
            HttpMethod.Delete, "/api/settings/models/model-to-delete", null, token);
        Assert.True(deleted.RootElement.GetProperty("success").GetBoolean());

        using JsonDocument list = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/models", null, token);
        Assert.False(list.RootElement.GetProperty("models").EnumerateArray().Any(m => m.GetProperty("id").GetString() == "model-to-delete"),
            "Deleted model 'model-to-delete' must not appear in the list.");
    }

    [Fact]
    public async Task ModelsListIsEmptyOnFreshDatabase()
    {
        await using var fixture = await ProviderModelFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument list = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/models", null, token);
        Assert.Empty(list.RootElement.GetProperty("models").EnumerateArray());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Model discovery endpoint with production Gateway routing and a probe double.
// ─────────────────────────────────────────────────────────────────────────────

[Collection(ProcessEnvironmentTestGroup.Name)]
public sealed class ModelDiscoveryEndpointTests
{
    [Fact]
    public async Task DiscoverModelsEndpointReturnsModelsFromService()
    {
        var fakeProbe = new CapturingModelProbeService([
            new DiscoveredModelDefinition("gemma3:9b", "Gemma 3 9B"),
            new DiscoveredModelDefinition("llama3:8b", "Llama 3 8B"),
        ]);

        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services => services.AddSingleton<IModelProbeService>(fakeProbe));
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument doc = await fixture.SendJsonAsync(HttpMethod.Post,
            "/api/settings/models/discover",
            new { providerId = "ollama-local" },
            token);

        JsonElement models = doc.RootElement.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("gemma3:9b", models[0].GetProperty("id").GetString());
        Assert.Equal("Gemma 3 9B", models[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task DiscoverModelsEndpointPassesProviderIdAndCallerScopeToService()
    {
        var fakeProbe = new CapturingModelProbeService([]);

        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services => services.AddSingleton<IModelProbeService>(fakeProbe));
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument _ = await fixture.SendJsonAsync(HttpMethod.Post,
            "/api/settings/models/discover",
            new { providerId = "my-special-provider" },
            token);

        Assert.Equal("my-special-provider", fakeProbe.LastProviderIdReceived);
        NetworkAccessScope scope = Assert.IsType<NetworkAccessScope>(fakeProbe.LastCaller);
        Assert.False(string.IsNullOrWhiteSpace(scope.UserId));
        Assert.Equal("gateway", scope.AgentId);
        Assert.Equal("ai-discovery", scope.Purpose);
    }

    [Fact]
    public async Task DiscoverModelsReturnsEmptyListWhenProviderNotFound()
    {
        // IModelProbeService returns empty for unknown provider — endpoint surfaces this as empty array.
        var fakeProbe = new CapturingModelProbeService([]);

        await using var fixture = await ProviderModelFixture.CreateAsync(configureServices: services => services.AddSingleton<IModelProbeService>(fakeProbe));
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument doc = await fixture.SendJsonAsync(HttpMethod.Post,
            "/api/settings/models/discover",
            new { providerId = "nonexistent" },
            token);
        Assert.Equal(0, doc.RootElement.GetProperty("models").GetArrayLength());
    }

    private sealed class CapturingModelProbeService(IReadOnlyList<DiscoveredModelDefinition> models)
        : IModelProbeService
    {
        public string? LastProviderIdReceived { get; private set; }
        public NetworkAccessScope? LastCaller { get; private set; }

        public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(string providerId)
        {
            LastProviderIdReceived = providerId;
            LastCaller = NetworkAccessContext.Current;
            return Task.FromResult(models);
        }

        public Task<GatewayModelCapabilities?> ProbeModelAsync(string modelId)
            => Task.FromResult<GatewayModelCapabilities?>(null);

        public Task<GatewayModelCapabilities> ProbeModelCandidateAsync(string providerId, string modelName)
            => Task.FromResult(new GatewayModelCapabilities(null, null, null, null, null, null, DateTimeOffset.UtcNow.ToString("O")));
    }

}

// ─────────────────────────────────────────────────────────────────────────────
// Unit tests — KautukaAiClientFactory credential resolution
// These test the factory directly to verify the api_key credential lookup logic.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AiClientFactoryDiscoveryTests
{
    [Fact]
    public async Task DiscoverModelsUsesInlineApiKeyFromSettings()
    {
        var capturer = new CapturingAiClientProvider("test-provider");
        var credResolver = BuildUnavailableCredentialResolver();
        var factory = new KautukaAiClientFactory([capturer], credResolver);

        JsonObject settings = new() { ["api_key"] = "sk-inline-key" };
        await factory.DiscoverModelsAsync("any-id", "test-provider", settings, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("sk-inline-key", capturer.LastApiKeyReceived);
    }

    [Fact]
    public async Task DiscoverModelsFailsClosedWhenCredentialsHostUnavailable()
    {
        var capturer = new CapturingAiClientProvider("test-provider");
        var credResolver = BuildUnavailableCredentialResolver();
        var factory = new KautukaAiClientFactory([capturer], credResolver);

        JsonObject settings = new();
        RpcException exception = await Assert.ThrowsAsync<RpcException>(() =>
            factory.DiscoverModelsAsync(
                "provider-x",
                "test-provider",
                settings,
                ct: TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
        Assert.Null(capturer.LastApiKeyReceived);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fixture-explicit-key")]
    public async Task UnboundProviderTestUsesOnlyAnExplicitKey(string? suppliedKey)
    {
        var capturer = new CapturingAiClientProvider("openai");
        var client = new FakeCredentialsServiceClient();
        var credentials = new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(() => client));
        var factory = new KautukaAiClientFactory([capturer], credentials);
        var cancellationToken = TestContext.Current.CancellationToken;
        await credentials.SetSystemAsync(new Dictionary<string, string> { ["provider.fixture.api_key"] = "fixture-saved-key" },
            cancellationToken);

        await factory.DiscoverModelsAsync("fixture", "openai", new JsonObject { ["api_key"] = suppliedKey },
            useStoredCredential: false, ct: cancellationToken);

        Assert.Equal(suppliedKey, capturer.LastApiKeyReceived);
    }

    [Fact]
    public async Task DiscoverModelsForOllamaDoesNotDependOnCredentialsHost()
    {
        var capturer = new CapturingAiClientProvider("ollama");
        var factory = new KautukaAiClientFactory([capturer], BuildUnavailableCredentialResolver());

        IReadOnlyList<DiscoveredModelDefinition> models = await factory.DiscoverModelsAsync(
            "ollama-local",
            "ollama",
            new JsonObject { ["base_url"] = "http://localhost:11434" },
            throwOnFailure: true,
            ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Single(models);
        Assert.Null(capturer.LastApiKeyReceived);
    }

    [Fact]
    public async Task DiscoverModelsReturnsEmptyForUnknownProviderType()
    {
        var credResolver = BuildUnavailableCredentialResolver();
        var factory = new KautukaAiClientFactory([], credResolver);

        IReadOnlyList<DiscoveredModelDefinition> result =
            await factory.DiscoverModelsAsync("p", "does-not-exist", new JsonObject(), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverModelsAcceptsUppercaseCatalogProviderType()
    {
        var credResolver = BuildUnavailableCredentialResolver();
        var factory = new KautukaAiClientFactory([], credResolver);

        IReadOnlyList<DiscoveredModelDefinition> result =
            await factory.DiscoverModelsAsync(
                "openrouter-main",
                "OPENROUTER",
                new JsonObject { ["api_key"] = "sk-inline-key" },
                ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverModelsCanPropagateProviderFailuresForConnectivityValidation()
    {
        var provider = new ThrowingAiClientProvider("test-provider");
        var factory = new KautukaAiClientFactory([provider], BuildUnavailableCredentialResolver());

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            factory.DiscoverModelsAsync(
                "provider-x",
                "test-provider",
                new JsonObject { ["api_key"] = "sk-inline-key" },
                throwOnFailure: true,
                ct: TestContext.Current.CancellationToken));
    }

    private static CredentialResolverService BuildUnavailableCredentialResolver()
        => new(new Lazy<CredentialsService.CredentialsServiceClient>(
            () => throw new InvalidOperationException("CredentialsHost not running in unit tests")));

    private sealed class CapturingAiClientProvider(string providerType) : IAiClientProvider
    {
        public string ProviderType => providerType;

        public string? LastApiKeyReceived { get; private set; }

        public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey) => null;

        public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
            JsonObject settings, string? apiKey, bool throwOnFailure = false, CancellationToken cancellationToken = default)
        {
            LastApiKeyReceived = apiKey;
            return Task.FromResult<IReadOnlyList<DiscoveredModelDefinition>>([
                new DiscoveredModelDefinition("discovered-model", "Discovered Model"),
            ]);
        }
    }

    private sealed class ThrowingAiClientProvider(string providerType) : IAiClientProvider
    {
        public string ProviderType => providerType;

        public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey) => null;

        public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
            JsonObject settings,
            string? apiKey,
            bool throwOnFailure = false,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("provider discovery failed");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared full-fixture (GatewayAppBootstrap pattern)
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ProviderModelFixture : IAsyncDisposable
{
    private static readonly JsonSerializerOptions jsonOpts = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly string root;
    private readonly string? previousEnvironment;
    private readonly WebApplication app;

    private ProviderModelFixture(string root, string? previousEnvironment, WebApplication app)
    {
        this.root = root;
        this.previousEnvironment = previousEnvironment;
        this.app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }
    public NetworkAccessPolicyStore ProviderRules { get; private set; } = null!;
    public bool ManagerUnavailable { get => providerInvoker.Unavailable; set => providerInvoker.Unavailable = value; }
    public bool FailRuleWrite { get => providerInvoker.FailRuleWrite; set => providerInvoker.FailRuleWrite = value; }
    public Action? BeforeRuleWrite { get => providerInvoker.BeforeRuleWrite; set => providerInvoker.BeforeRuleWrite = value; }

    public async Task SetCatalogWriteFailureAsync(bool fail)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection(app.Services.GetRequiredService<KautukaDatabase>().ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        if (fail)
            command.CommandText = """
                CREATE TRIGGER fail_provider_save BEFORE INSERT ON providers
                WHEN NEW.id = 'failed-catalog'
                BEGIN SELECT RAISE(ABORT, 'fixture catalog write failed'); END;
                """;
        else
            command.CommandText = "DROP TRIGGER fail_provider_save;";
        _ = await command.ExecuteNonQueryAsync(token);
    }
    public bool FailTestCleanup { set => providerInvoker.FailTestCleanup = value; }
    public bool TemporaryTestAllowed => providerInvoker.TemporaryTestAllowed;
    public bool TemporaryTestEnded => providerInvoker.TemporaryTestEnded;
    public Task TestGrantEnded => providerInvoker.GrantEnded.Task;
    private ProviderRulesCallInvoker providerInvoker = null!;
    internal static IManagerGrpcService CreateTestManager(NetworkAccessPolicyStore rules)
        => new ManagerGrpcServiceClient(new ProviderRulesCallInvoker(rules), "http://localhost");

    public static async Task<ProviderModelFixture> CreateAsync(bool useControlledValidation = false, Action<IServiceCollection>? configureServices = null)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "kautuka-provider-model-tests", Guid.NewGuid().ToString("N"));
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
        var providerRules = new NetworkAccessPolicyStore(Path.Combine(root, "provider-network.json"));
        var providerInvoker = new ProviderRulesCallInvoker(providerRules);
        if (useControlledValidation)
        {
            builder.Services.RemoveAll<IProviderValidationService>();
            builder.Services.AddSingleton<IProviderValidationService>(new ControlledProviderValidation(providerInvoker));
        }
        builder.Services.RemoveAll<IManagerGrpcService>();
        builder.Services.AddSingleton<IManagerGrpcService>(new ManagerGrpcServiceClient(providerInvoker, "http://localhost"));

        // Remove background services that are irrelevant to these tests.
        RemoveHostedService<WorkspaceDesiredStateStartupService>(builder.Services);
        RemoveHostedService<ChannelHostOutboxWorker>(builder.Services);
        RemoveHostedService<ChannelHostIngressWorker>(builder.Services);
        RemoveHostedService<AutopilotSchedulerService>(builder.Services);

        // Replace the real CredentialResolverService with one backed by the
        // FakeCredentialsServiceClient, which captures writes and serves them back on reads.
        var fakeCredClient = new FakeCredentialsServiceClient();
        builder.Services.RemoveAll<CredentialResolverService>();
        builder.Services.AddSingleton(
            new CredentialResolverService(
                new Lazy<CredentialsService.CredentialsServiceClient>(fakeCredClient)));

        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        var database = app.Services.GetRequiredService<KautukaDatabase>();
        GatewayAppBootstrap.ConfigureApp(app, database);
        await app.StartAsync().ConfigureAwait(true);

        return new ProviderModelFixture(root, previousEnvironment, app) { ProviderRules = providerRules, providerInvoker = providerInvoker };
    }

    public async Task<string> LoginAsBootstrapAdminAsync()
    {
        using JsonDocument _ = await SendJsonAsync(
            HttpMethod.Post,
            "/api/auth/setup",
            new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() },
            accessToken: null,
            authenticated: false).ConfigureAwait(true);

        using JsonDocument json = await SendJsonAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "admin", password = "Testing1" },
            accessToken: null,
            authenticated: false).ConfigureAwait(true);

        return json.RootElement.GetProperty("accessToken").GetString()
            ?? throw new XunitException("Login response did not include an access token.");
    }

    public async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        string? accessToken = null,
        bool authenticated = true)
    {
        using HttpRequestMessage request = new(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: jsonOpts);

        if (authenticated)
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", accessToken ?? throw new ArgumentNullException(nameof(accessToken)));

        using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        if (response.StatusCode == HttpStatusCode.Conflict && path == "/api/settings/providers/save" && body is not null)
        {
            using JsonDocument challenge = JsonDocument.Parse(content);
            if (challenge.RootElement.TryGetProperty("revision", out _))
            {
                var confirmed = JsonSerializer.SerializeToNode(body, jsonOpts)!.AsObject();
                confirmed["networkPermission"] = JsonNode.Parse(content);
                using HttpResponseMessage afterConfirmation = await SendAsync(method, path, confirmed, accessToken!).ConfigureAwait(true);
                string confirmedContent = await afterConfirmation.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (!afterConfirmation.IsSuccessStatusCode)
                    throw new XunitException($"Confirmed provider save failed with {(int)afterConfirmation.StatusCode}: {confirmedContent}");
                return JsonDocument.Parse(confirmedContent);
            }
        }
        if (!response.IsSuccessStatusCode)
            throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");

        return JsonDocument.Parse(content);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string accessToken)
        => SendCancellableAsync(method, path, body, accessToken, TestContext.Current.CancellationToken);

    public async Task<HttpResponseMessage> SendCancellableAsync(
        HttpMethod method,
        string path,
        object? body,
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: jsonOpts);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Client.SendAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public string? GetConfiguredProviderType(string providerId)
        => app.Services.GetRequiredService<IPlatformConfigurationProvider>()
            .GetCurrent()
            .Providers
            .SingleOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal))
            ?.Type;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync().ConfigureAwait(true);
        await app.DisposeAsync().ConfigureAwait(true);
        ProviderRules.Dispose();
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
        SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
        if (Directory.Exists(root))
            await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
    }

    private sealed class ProviderRulesCallInvoker(NetworkAccessPolicyStore store) : CallInvoker
    {
        private readonly NetworkAccessController.NetworkAccessController controller = new(store, new WorkloadRegistry());
        public bool Unavailable { get; set; }
        public bool FailRuleWrite { get; set; }
        public Action? BeforeRuleWrite { get; set; }
        public bool FailTestCleanup { get; set; }
        public bool TemporaryTestAllowed { get; private set; }
        public bool TemporaryTestEnded { get; private set; }
        public TaskCompletionSource GrantEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? testUser;
        private string? testToken;
        private string? testHost;
        private int testPort;
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (Unavailable) throw new RpcException(new Status(StatusCode.Unavailable, "Manager unavailable."));
            object response = request switch
            {
                NetworkAccessProviderPermissionRequest permission => EnsurePermission(permission),
                NetworkAccessProviderTestRequest begin => Begin(begin),
                NetworkAccessProviderTestEndRequest end => End(end),
                _ => throw new RpcException(new Status(StatusCode.Unavailable, "No test manager implementation.")),
            };
            return new(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        private NetworkAccessProviderTestResponse Begin(NetworkAccessProviderTestRequest request)
        {
            testUser = request.Actor.UserId;
            testHost = request.Host;
            testPort = request.Port;
            var decision = controller.PrepareProviderTest(new(testUser, true), request.Host, request.Port, request.AllowLan, request.Confirmed);
            testToken = decision.Token;
            return new NetworkAccessProviderTestResponse { Token = testToken, Status = decision.Status, Reason = decision.Reason };
        }
        private WorkspaceBoolResponse End(NetworkAccessProviderTestEndRequest request)
        {
            if (FailTestCleanup) throw new RpcException(new Status(StatusCode.Unavailable, "Manager unavailable."));
            controller.EndProviderTest(new(request.Actor.UserId, true), request.Token);
            TemporaryTestEnded = !controller.EvaluateOutboundCheck(new(testUser!, "gateway", null, testHost!, testPort, "provider-test", testToken), recordPendingRequest: false).Allowed;
            GrantEnded.TrySetResult();
            return new WorkspaceBoolResponse { Ok = true };
        }
        public NetworkAccessCheckResult CheckTemporaryTest(OutboundTarget target)
        {
            NetworkAccessDecisionResult decision = controller.EvaluateOutboundCheck(new(target.UserId!, "gateway", null, target.Host, target.Port, target.Purpose, target.CorrelationId));
            TemporaryTestAllowed = decision.Allowed;
            return new NetworkAccessCheckResult(decision.Allowed, decision.Reason, decision.RequestId, decision.OpenInternetBadge, NetworkAccessDenialKind.Denied);
        }
        private NetworkAccessProviderPermissionResponse EnsurePermission(NetworkAccessProviderPermissionRequest request)
        {
            if (FailRuleWrite && !request.CheckOnly) throw new RpcException(new Status(StatusCode.Unavailable, "Rule write failed."));
            if (!request.CheckOnly) BeforeRuleWrite?.Invoke();
            (long revision, string status) = store.EnsureProviderPermission(new NetworkAccessActor(request.Actor.UserId, request.Actor.IsAdmin),
                request.Destination, request.DestKind, request.Port, request.AllowLan,
                request.HasConfirmedRevision ? request.ConfirmedRevision : null, request.CheckOnly);
            return new NetworkAccessProviderPermissionResponse { Revision = revision, Status = status };
        }
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    }
    private sealed class ControlledProviderValidation(ProviderRulesCallInvoker invoker) : IProviderValidationService
    {
        public async Task<GatewayProviderTestResult> TestProviderAsync(ProviderTestDefinition definition, CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient(new NetworkAccessCheckHandler(new ControlledChecker(invoker), new ControlledTransport()));
            using HttpResponseMessage response = await client.GetAsync(new Uri(definition.Settings["base_url"]!.GetValue<string>()), cancellationToken);
            return new GatewayProviderTestResult(response.IsSuccessStatusCode, "controlled test");
        }
    }
    private sealed class ControlledChecker(ProviderRulesCallInvoker invoker) : INetworkAccessChecker
    {
        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
            => Task.FromResult(invoker.CheckTemporaryTest(target));
    }
    private sealed class ControlledTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private static void RemoveHostedService<T>(IServiceCollection services)
        where T : class, IHostedService
    {
        ServiceDescriptor[] toRemove = [.. services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(T))];
        foreach (ServiceDescriptor d in toRemove) services.Remove(d);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fake gRPC credentials client — captures writes, serves them back on reads.
// Subclasses the generated CredentialsService.CredentialsServiceClient using the
// protected no-arg constructor available on all gRPC ClientBase<T> subclasses.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FakeCredentialsServiceClient : CredentialsService.CredentialsServiceClient
{
    private readonly Dictionary<string, string> systemStore = new(StringComparer.Ordinal);
    internal bool FailSystemWrites { get; init; }
    internal bool LoseSystemWriteAcknowledgement { get; set; }

    public override AsyncUnaryCall<CredentialKeyListResponse> ListSystemCredentialKeysAsync(
        ListSystemCredentialKeysRequest request, CallOptions options)
    {
        var response = new CredentialKeyListResponse();
        response.Keys.AddRange(systemStore.Keys);
        return Wrap(response);
    }

    public override AsyncUnaryCall<SetCredentialsResponse> SetSystemCredentialsAsync(
        SetSystemCredentialsRequest request, CallOptions options)
    {
        if (FailSystemWrites) throw new RpcException(new Status(StatusCode.Unavailable, "Fixture credential write failure."));
        foreach ((string key, string value) in request.Entries)
            systemStore[key] = value;

        if (LoseSystemWriteAcknowledgement) throw new RpcException(new Status(StatusCode.Unavailable, "Fixture lost the write acknowledgement."));
        return Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });
    }

    public override AsyncUnaryCall<CredentialMapResponse> GetSystemCredentialAsync(
        GetSystemCredentialRequest request, CallOptions options)
    {
        var response = new CredentialMapResponse();
        if (systemStore.TryGetValue(request.Key, out string? value))
            response.Entries[request.Key] = value;

        return Wrap(response);
    }

    public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
        GetResolvedCredentialsRequest request, CallOptions options)
    {
        var response = new CredentialMapResponse();
        foreach (string key in request.RequiredKeys)
        {
            if (systemStore.TryGetValue(key, out string? value))
                response.Entries[key] = value;
        }
        return Wrap(response);
    }

    public override AsyncUnaryCall<CredentialKeyListResponse> ListUserCredentialKeysAsync(
        ListUserCredentialKeysRequest request, CallOptions options)
        => Wrap(new CredentialKeyListResponse());

    public override AsyncUnaryCall<DeleteCredentialResponse> DeleteSystemCredentialAsync(
        DeleteSystemCredentialRequest request, CallOptions options)
    {
        bool deleted = systemStore.Remove(request.Key);
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
