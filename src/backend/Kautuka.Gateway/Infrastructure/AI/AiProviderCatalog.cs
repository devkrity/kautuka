namespace Kautuka.Gateway.Infrastructure.AI;

internal static class AiProviderCatalog
{
    public static readonly Dictionary<string, ProviderMetadata> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ollama",       new("Ollama",           "http://localhost:11434",                          300, RequiresApiKey: false, RequiresBaseUrl: true,  SupportsHttpTimeoutSeconds: true) },
        { "openai",       new("OpenAI",            "https://api.openai.com/v1",                      300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "openai-responses", new("OpenAI Compatible (Responses)", "https://api.openai.com/v1",      300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "azure-openai", new("Azure OpenAI",      null,                                             300, RequiresApiKey: true,  RequiresBaseUrl: true,  SupportsHttpTimeoutSeconds: true) },
        { "foundry",      new("Azure AI Foundry",  null,                                             300, RequiresApiKey: true,  RequiresBaseUrl: true,  SupportsHttpTimeoutSeconds: true) },
        { "anthropic",    new("Anthropic",         "https://api.anthropic.com/v1",                   300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "groq",         new("Groq",              "https://api.groq.com/openai/v1",                 300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "mistral",      new("Mistral",           "https://api.mistral.ai/v1",                      300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "openrouter",   new("OpenRouter",        "https://openrouter.ai/api/v1",                  300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
        { "gemini",       new("Google Gemini",     "https://generativelanguage.googleapis.com/", 300, RequiresApiKey: true,  RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: true) },
    };

    internal sealed record ProviderMetadata(
        string FriendlyName,
        string? DefaultBaseUrl,
        int? DefaultHttpTimeoutSeconds,
        bool RequiresApiKey,
        bool RequiresBaseUrl,
        bool SupportsHttpTimeoutSeconds);
}

internal interface IProviderTypeCatalog
{
    IReadOnlyList<ProviderTypeInfo> ListProviderTypes();

    IReadOnlyList<ProviderCatalogEntry> ListCatalogEntries();

    bool TryGet(string providerType, out AiProviderCatalog.ProviderMetadata? metadata);

    void SaveCatalogEntries(IReadOnlyList<ProviderCatalogEntry> entries);
}

internal sealed record ProviderCatalogEntry(
    string Type,
    string FriendlyName,
    string? DefaultBaseUrl,
    int? DefaultHttpTimeoutSeconds,
    bool RequiresApiKey,
    bool RequiresBaseUrl,
    bool SupportsHttpTimeoutSeconds,
    bool IsBuiltIn,
    int SeedVersion);

internal sealed class SeededProviderTypeCatalog : IProviderTypeCatalog
{
    public IReadOnlyList<ProviderTypeInfo> ListProviderTypes()
        => [.. AiProviderCatalog.Defaults
            .OrderBy(static entry => entry.Value.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new ProviderTypeInfo(
                entry.Key,
                entry.Value.FriendlyName,
                entry.Value.RequiresApiKey,
                entry.Value.RequiresBaseUrl,
                entry.Value.SupportsHttpTimeoutSeconds))];

    public IReadOnlyList<ProviderCatalogEntry> ListCatalogEntries()
        => [.. AiProviderCatalog.Defaults
            .OrderBy(static entry => entry.Value.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => new ProviderCatalogEntry(
                entry.Key,
                entry.Value.FriendlyName,
                entry.Value.DefaultBaseUrl,
                entry.Value.DefaultHttpTimeoutSeconds,
                entry.Value.RequiresApiKey,
                entry.Value.RequiresBaseUrl,
                entry.Value.SupportsHttpTimeoutSeconds,
                IsBuiltIn: true,
                SeedVersion: 1))];

    public bool TryGet(string providerType, out AiProviderCatalog.ProviderMetadata? metadata)
    {
        bool found = AiProviderCatalog.Defaults.TryGetValue(providerType, out AiProviderCatalog.ProviderMetadata? value);
        metadata = found ? value : null;
        return found;
    }

    public void SaveCatalogEntries(IReadOnlyList<ProviderCatalogEntry> entries)
    {
    }
}
