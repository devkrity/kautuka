using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed record ProviderTypeInfo(
    string Type,
    string FriendlyName,
    bool RequiresApiKey,
    bool RequiresBaseUrl,
    bool SupportsHttpTimeoutSeconds);
internal sealed record DiscoveredModelDefinition(
    string Id,
    string DisplayName,
    string? Description = null,
    bool? SupportsChat = null,
    bool? SupportsEmbeddings = null,
    string[]? CapabilityTags = null);

internal static class DiscoveredModelCapabilities
{
    public static DiscoveredModelDefinition FromModelId(string id, string displayName, string? description = null)
    {
        string normalized = id.Trim();
        bool looksLikeEmbeddingModel = normalized.Contains("embedding", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("embed", StringComparison.OrdinalIgnoreCase);
        bool looksLikeNonChatModel = normalized.Contains("moderation", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("speech", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("tts", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("rerank", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("image", StringComparison.OrdinalIgnoreCase);

        bool? supportsChat = looksLikeEmbeddingModel || looksLikeNonChatModel ? false : true;
        bool? supportsEmbeddings = looksLikeEmbeddingModel ? true : null;
        string[] capabilityTags = BuildCapabilityTags(supportsChat, supportsEmbeddings);

        return new DiscoveredModelDefinition(
            id,
            displayName,
            description,
            supportsChat,
            supportsEmbeddings,
            capabilityTags.Length == 0 ? null : capabilityTags);
    }

    public static DiscoveredModelDefinition FromDeclaredMethods(
        string id,
        string displayName,
        string? description,
        IEnumerable<string> supportedMethods)
    {
        string[] methods = supportedMethods
            .Where(static method => !string.IsNullOrWhiteSpace(method))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool? supportsChat = methods.Contains("generateContent", StringComparer.Ordinal) ? true : null;
        bool? supportsEmbeddings = methods.Contains("embedContent", StringComparer.Ordinal)
            || methods.Contains("batchEmbedContents", StringComparer.Ordinal)
            ? true
            : null;

        string[] capabilityTags = BuildCapabilityTags(supportsChat, supportsEmbeddings);

        return new DiscoveredModelDefinition(
            id,
            displayName,
            description,
            supportsChat,
            supportsEmbeddings,
            capabilityTags.Length == 0 ? null : capabilityTags);
    }

    private static string[] BuildCapabilityTags(bool? supportsChat, bool? supportsEmbeddings)
    {
        List<string> tags = [];
        if (supportsChat is true)
        {
            tags.Add("chat");
        }

        if (supportsEmbeddings is true)
        {
            tags.Add("embeddings");
        }

        return [.. tags];
    }
}

internal interface IAiClientProvider
{
    string ProviderType { get; }

    ProviderTypeInfo Metadata => new(ProviderType, ProviderType, RequiresApiKey: false, RequiresBaseUrl: false, SupportsHttpTimeoutSeconds: false);

    IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey);

    IEmbeddingGenerator<string, Embedding<float>>? TryCreateEmbeddingGenerator(
        JsonObject settings,
        string modelName,
        string? apiKey)
        => null;

    Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(
        JsonObject settings,
        string? apiKey,
        bool throwOnFailure = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DiscoveredModelDefinition>>([]);

    /// <summary>
    /// Queries the provider API for declared model metadata/capabilities.
    /// Returns an empty profile when the provider does not expose this information.
    /// Also returns the model digest when available (for cache invalidation).
    /// </summary>
    Task<(DeclaredCapabilities Declared, string? ModelDigest)> IntrospectModelAsync(
        JsonObject settings, string modelName, string? apiKey, CancellationToken ct = default)
        => Task.FromResult<(DeclaredCapabilities, string?)>((new DeclaredCapabilities(), null));
}
