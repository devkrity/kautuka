using Kautuka.Gateway.Infrastructure.AI;

namespace Kautuka.ContractTests;

public sealed class AiProviderCatalogTests
{
    public static IEnumerable<object[]> HostedProviderBaseUrls()
    {
        yield return ["openai", new Uri("https://api.openai.com/v1", UriKind.Absolute)];
        yield return ["openai-responses", new Uri("https://api.openai.com/v1", UriKind.Absolute)];
        yield return ["groq", new Uri("https://api.groq.com/openai/v1", UriKind.Absolute)];
        yield return ["mistral", new Uri("https://api.mistral.ai/v1", UriKind.Absolute)];
        yield return ["anthropic", new Uri("https://api.anthropic.com/v1", UriKind.Absolute)];
        yield return ["openrouter", new Uri("https://openrouter.ai/api/v1", UriKind.Absolute)];
        yield return ["gemini", new Uri("https://generativelanguage.googleapis.com/", UriKind.Absolute)];
    }

    [Fact]
    public void CatalogContainsAllExpectedProviderTypes()
    {
        string[] expected = ["ollama", "openai", "openai-responses", "azure-openai", "foundry", "anthropic", "groq", "mistral", "openrouter", "gemini"];
        foreach (string type in expected)
        {
            Assert.True(AiProviderCatalog.Defaults.ContainsKey(type), $"AiProviderCatalog is missing provider type '{type}'");
        }
    }

    [Theory]
    [InlineData("openai", true, false)]
    [InlineData("openai-responses", true, false)]
    [InlineData("azure-openai", true, true)]
    [InlineData("foundry", true, true)]
    [InlineData("anthropic", true, false)]
    [InlineData("groq", true, false)]
    [InlineData("mistral", true, false)]
    [InlineData("openrouter", true, false)]
    [InlineData("gemini", true, false)]
    [InlineData("ollama", false, true)]
    public void CatalogMetadataMatchesExpectedShape(string type, bool requiresApiKey, bool requiresBaseUrl)
    {
        Assert.True(AiProviderCatalog.Defaults.TryGetValue(type, out AiProviderCatalog.ProviderMetadata? meta));
        Assert.NotNull(meta);
        Assert.Equal(requiresApiKey, meta.RequiresApiKey);
        Assert.Equal(requiresBaseUrl, meta.RequiresBaseUrl);
        Assert.False(string.IsNullOrWhiteSpace(meta.FriendlyName));
    }

    [Theory]
    [MemberData(nameof(HostedProviderBaseUrls))]
    public void CatalogHasDefaultBaseUrlForHostedProviders(string type, Uri expectedBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(expectedBaseUrl);
        Assert.True(AiProviderCatalog.Defaults.TryGetValue(type, out AiProviderCatalog.ProviderMetadata? meta));
        Assert.Equal(expectedBaseUrl.AbsoluteUri, meta!.DefaultBaseUrl);
    }

    [Theory]
    [InlineData("azure-openai")]
    [InlineData("foundry")]
    public void CatalogHasNullDefaultBaseUrlForDeploymentSpecificProviders(string type)
    {
        Assert.True(AiProviderCatalog.Defaults.TryGetValue(type, out AiProviderCatalog.ProviderMetadata? meta));
        Assert.Null(meta!.DefaultBaseUrl);
    }
}
