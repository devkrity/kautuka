using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Endpoints.Settings;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

// ─────────────────────────────────────────────────────────────────────────────
// Full-fixture HTTP tests — provider CRUD
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ProviderSettingsEndpointTests
{
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
// Minimal-pattern tests — model discovery endpoint
// These use a hand-rolled WebApplication with only the required services,
// avoiding the full GatewayAppBootstrap startup time.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ModelDiscoveryEndpointTests
{
    private static readonly JsonSerializerOptions jsonOpts = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public async Task DiscoverModelsEndpointReturnsModelsFromService()
    {
        var fakeProbe = new CapturingModelProbeService([
            new DiscoveredModelDefinition("gemma3:9b", "Gemma 3 9B"),
            new DiscoveredModelDefinition("llama3:8b", "Llama 3 8B"),
        ]);

        using WebApplication app = await BuildMinimalAppAsync(fakeProbe);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/settings/models/discover",
            new { providerId = "ollama-local" },
            jsonOpts,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement models = doc.RootElement.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("gemma3:9b", models[0].GetProperty("id").GetString());
        Assert.Equal("Gemma 3 9B", models[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task DiscoverModelsEndpointPassesProviderIdToService()
    {
        var fakeProbe = new CapturingModelProbeService([]);

        using WebApplication app = await BuildMinimalAppAsync(fakeProbe);
        using HttpClient client = app.GetTestClient();

        await client.PostAsJsonAsync(
            "/api/settings/models/discover",
            new { providerId = "my-special-provider" },
            jsonOpts,
            TestContext.Current.CancellationToken);

        Assert.Equal("my-special-provider", fakeProbe.LastProviderIdReceived);
    }

    [Fact]
    public async Task DiscoverModelsReturnsEmptyListWhenProviderNotFound()
    {
        // IModelProbeService returns empty for unknown provider — endpoint surfaces this as empty array.
        var fakeProbe = new CapturingModelProbeService([]);

        using WebApplication app = await BuildMinimalAppAsync(fakeProbe);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/settings/models/discover",
            new { providerId = "nonexistent" },
            jsonOpts,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, doc.RootElement.GetProperty("models").GetArrayLength());
    }

    private static async Task<WebApplication> BuildMinimalAppAsync(IModelProbeService modelProbeService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IModelProbeService>(modelProbeService);
        builder.Services.AddSingleton<IGatewayAuthGuard, AllowAllAuthGuard>();

        WebApplication app = builder.Build();
        app.MapGatewaySettingsEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class CapturingModelProbeService(IReadOnlyList<DiscoveredModelDefinition> models)
        : IModelProbeService
    {
        public string? LastProviderIdReceived { get; private set; }

        public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(string providerId)
        {
            LastProviderIdReceived = providerId;
            return Task.FromResult(models);
        }

        public Task<GatewayModelCapabilities?> ProbeModelAsync(string modelId)
            => Task.FromResult<GatewayModelCapabilities?>(null);

        public Task<GatewayModelCapabilities> ProbeModelCandidateAsync(string providerId, string modelName)
            => Task.FromResult(new GatewayModelCapabilities(null, null, null, null, null, null, DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class AllowAllAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        private static GatewayUser BuildUser() => new(
            Id: "user-1",
            Username: "tester",
            Password: "pwd",
            Role: "admin",
            IsActive: true,
            Email: null,
                DisplayName: null);
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

    public static async Task<ProviderModelFixture> CreateAsync()
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

        WebApplication app = builder.Build();
        var database = app.Services.GetRequiredService<KautukaDatabase>();
        GatewayAppBootstrap.ConfigureApp(app, database);
        await app.StartAsync().ConfigureAwait(true);

        return new ProviderModelFixture(root, previousEnvironment, app);
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
        if (!response.IsSuccessStatusCode)
            throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");

        return JsonDocument.Parse(content);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string accessToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: jsonOpts);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Client.SendAsync(request).ConfigureAwait(true);
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
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
        SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
        if (Directory.Exists(root))
            await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
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
        foreach ((string key, string value) in request.Entries)
            systemStore[key] = value;

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
