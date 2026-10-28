using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Infrastructure.Credentials;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class KautukaAiClientFactory(
    IEnumerable<IAiClientProvider> providers,
    CredentialResolverService credentialResolver,
    IProviderTypeCatalog providerTypeCatalog,
    IRunLimitResolver? runLimitResolver = null)
{
    public KautukaAiClientFactory(IEnumerable<IAiClientProvider> providers, CredentialResolverService credentialResolver)
        : this(providers, credentialResolver, new SeededProviderTypeCatalog())
    {
    }

    private readonly Dictionary<string, IAiClientProvider> providers = providers
        .GroupBy(provider => provider.ProviderType, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToDictionary(provider => ProviderTypeCatalogSettings.NormalizeType(provider.ProviderType), provider => provider, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sync path — extracts api_key directly from settings JSON (validation/test flow).
    /// </summary>
    public IChatClient? TryCreate(string providerType, JsonObject settings, string modelName)
    {
        string normalizedProviderType = ProviderTypeCatalogSettings.NormalizeType(providerType);
        // In the validation flow, the api_key may be present in the request-supplied settings.
        string? apiKey = AiProviderSettings.GetString(settings, "api_key");

        if (!providers.TryGetValue(normalizedProviderType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(normalizedProviderType, allowCatalogDefaults: true);
        }

        if (strategy is null)
            return null;

        return strategy.TryCreate(MaterializeCatalogDefaults(normalizedProviderType, settings), modelName, apiKey);
    }

    /// <summary>
    /// Async path — resolves api_key from CredentialsHost using the provider ID.
    /// Use this for all runtime paths (agent execution, probing, introspection).
    /// </summary>
    public async Task<IChatClient?> TryCreateAsync(
        ProviderDefinition provider,
        string modelName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string providerType = ProviderTypeCatalogSettings.NormalizeType(provider.Type);
        if (!providers.TryGetValue(providerType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(providerType, allowCatalogDefaults: false);
        }

        if (strategy is null) return null;

        JsonObject settingsJson = ToSettingsObject(provider);
        string? apiKey = await ResolveApiKeyAsync(provider.Id, providerType, settingsJson, ct).ConfigureAwait(false);

        return strategy.TryCreate(settingsJson, modelName, apiKey);
    }

    public async Task<IEmbeddingGenerator<string, Embedding<float>>?> TryCreateEmbeddingGeneratorAsync(
        ProviderDefinition provider,
        string modelName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string providerType = ProviderTypeCatalogSettings.NormalizeType(provider.Type);
        if (!providers.TryGetValue(providerType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(providerType, allowCatalogDefaults: false);
        }

        if (strategy is null)
        {
            return null;
        }

        JsonObject settingsJson = ToSettingsObject(provider);
        string? apiKey = await ResolveApiKeyAsync(provider.Id, providerType, settingsJson, ct).ConfigureAwait(false);

        return strategy.TryCreateEmbeddingGenerator(settingsJson, modelName, apiKey);
    }

    public async Task<IEmbeddingGenerator<string, Embedding<float>>?> TryCreateEmbeddingGeneratorAsync(
        string providerId,
        string providerType,
        JsonObject settings,
        string modelName,
        CancellationToken ct = default)
    {
        string normalizedProviderType = ProviderTypeCatalogSettings.NormalizeType(providerType);
        if (!providers.TryGetValue(normalizedProviderType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(normalizedProviderType, allowCatalogDefaults: false);
        }

        if (strategy is null)
        {
            return null;
        }

        string? apiKey = await ResolveApiKeyAsync(providerId, normalizedProviderType, settings, ct).ConfigureAwait(false);

        return strategy.TryCreateEmbeddingGenerator(settings, modelName, apiKey);
    }

    /// <summary>
    /// Async path taking GatewayProvider-compatible parameters.
    /// Inline api_key from settings takes priority; falls back to CredentialsHost.
    /// Use when you have the provider ID available but not a ProviderDefinition.
    /// </summary>
    public async Task<IChatClient?> TryCreateAsync(
        string providerId,
        string providerType,
        JsonObject settings,
        string modelName,
        CancellationToken ct = default)
    {
        string normalizedProviderType = ProviderTypeCatalogSettings.NormalizeType(providerType);
        if (!providers.TryGetValue(normalizedProviderType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(normalizedProviderType, allowCatalogDefaults: false);
        }

        if (strategy is null) return null;

        // Inline api_key (e.g. from test/validation request) takes priority over CredentialsHost.
        string? apiKey = await ResolveApiKeyAsync(providerId, normalizedProviderType, settings, ct).ConfigureAwait(false);

        return strategy.TryCreate(settings, modelName, apiKey);
    }

    public async Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        string providerId,
        string providerType,
        JsonObject settings,
        bool throwOnFailure = false,
        CancellationToken ct = default)
    {
        string normalizedProviderType = ProviderTypeCatalogSettings.NormalizeType(providerType);

        if (!providers.TryGetValue(normalizedProviderType, out IAiClientProvider? strategy))
        {
            strategy = CreateFallbackProvider(normalizedProviderType, allowCatalogDefaults: false);
        }

        if (strategy is null)
        {
            if (throwOnFailure)
                throw new InvalidOperationException($"Provider type '{providerType}' is not supported.");

            return [];
        }

        // Inline api_key (e.g. from test request) takes priority; falls back to CredentialsHost.
        string? apiKey = await ResolveApiKeyAsync(providerId, normalizedProviderType, settings, ct).ConfigureAwait(false);

        return await strategy.DiscoverModelsAsync(settings, apiKey, throwOnFailure, ct).ConfigureAwait(false);
    }

    public async Task<(DeclaredCapabilities Declared, string? ModelDigest)> IntrospectModelAsync(
        ProviderDefinition provider, string modelName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string providerType = ProviderTypeCatalogSettings.NormalizeType(provider.Type);
        if (!providers.TryGetValue(providerType, out IAiClientProvider? strategy))
            return (new DeclaredCapabilities(), null);

        JsonObject settingsJson = ToSettingsObject(provider);
        string? apiKey = await ResolveApiKeyAsync(provider.Id, providerType, settingsJson, ct).ConfigureAwait(false);

        return await strategy.IntrospectModelAsync(settingsJson, modelName, apiKey, ct).ConfigureAwait(false);
    }

    public static Uri GetBaseUrl(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string providerType = ProviderTypeCatalogSettings.NormalizeType(provider.Type);
        JsonObject settingsJson = ToSettingsObject(provider);
        // base_url is the common settings key across all providers
        string value = AiProviderSettings.GetString(settingsJson, "base_url")
            ?? AiProviderSettings.GetString(settingsJson, "endpoint")
            ?? providerType;

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri
            : new Uri($"https://{value}", UriKind.Absolute);
    }

    private static JsonObject ToSettingsObject(ProviderDefinition provider)
        => JsonObject.Create(JsonSerializer.SerializeToElement(provider.Settings, KautukaJsonContext.Default.DictionaryStringObject))
            ?? new JsonObject();

    public IReadOnlyList<ProviderTypeInfo> ListProviderTypes() => providerTypeCatalog.ListProviderTypes();

    private async Task<string?> ResolveApiKeyAsync(
        string providerId,
        string providerType,
        JsonObject settings,
        CancellationToken ct)
    {
        string? inlineApiKey = AiProviderSettings.GetString(settings, "api_key");
        if (!string.IsNullOrWhiteSpace(inlineApiKey))
        {
            return inlineApiKey;
        }

        // A provider that does not authenticate with an API key must not acquire a
        // dependency on CredentialsHost. In particular, local Ollama validation and
        // execution remain available when the credential service is restarting.
        if (providerTypeCatalog.TryGet(providerType, out AiProviderCatalog.ProviderMetadata? metadata)
            && metadata is not null
            && !metadata.RequiresApiKey)
        {
            return null;
        }

        string? storedApiKey = await credentialResolver
            .GetSystemAsync($"provider.{providerId}.api_key", ct)
            .ConfigureAwait(false);

        if (metadata?.RequiresApiKey is true && string.IsNullOrWhiteSpace(storedApiKey))
        {
            throw new InvalidOperationException($"Provider '{providerId}' requires an API key, but no API key is configured.");
        }

        return storedApiKey;
    }

    private OpenAiCompatibleProvider? CreateFallbackProvider(string providerType, bool allowCatalogDefaults)
    {
        if (!providerTypeCatalog.TryGet(providerType, out AiProviderCatalog.ProviderMetadata? metadata) || metadata is null)
        {
            return null;
        }

        return new OpenAiCompatibleProvider(providerType, allowCatalogDefaults ? metadata.DefaultBaseUrl : null, runLimitResolver);
    }

    private JsonObject MaterializeCatalogDefaults(string providerType, JsonObject settings)
        => ProviderTypeCatalogSettings.ApplyCreateDefaults(settings.DeepClone() as JsonObject ?? new JsonObject(), providerType, providerTypeCatalog);
}
