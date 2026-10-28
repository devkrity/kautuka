using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class GoogleAiClientProviderTests
{
    [Fact]
    public void TryCreateReturnsNativeGenAiChatClientWithoutBaseUrl()
    {
        var provider = new GoogleAiClientProvider();
        IChatClient? client = provider.TryCreate(new JsonObject(), "models/gemini-3.5-flash-lite", "test-key");

        Assert.NotNull(client);
        Assert.Equal("GeminiDeveloperApiChatClient", client.GetType().Name);
    }

    [Fact]
    public void TryCreateIgnoresOpenAiCompatibleBaseUrl()
    {
        var provider = new GoogleAiClientProvider();
        IChatClient? client = provider.TryCreate(
            new JsonObject
            {
                ["base_url"] = "https://generativelanguage.googleapis.com/v1beta/openai/",
            },
            "gemini-3.5-flash-lite",
            "test-key");

        Assert.NotNull(client);
        Assert.Equal("GeminiDeveloperApiChatClient", client.GetType().Name);
    }

    [Fact]
    public void TryCreateEmbeddingGeneratorReturnsNativeGenAiEmbeddingClient()
    {
        var provider = new GoogleAiClientProvider();
        IEmbeddingGenerator<string, Embedding<float>>? client =
            provider.TryCreateEmbeddingGenerator(new JsonObject(), "text-embedding-004", "test-key");

        Assert.NotNull(client);
        Assert.Equal("GoogleGenAIEmbeddingGenerator", client.GetType().Name);
    }

    [Fact]
    public void TryCreateRequiresApiKey()
    {
        var provider = new GoogleAiClientProvider();
        Assert.Null(provider.TryCreate(new JsonObject(), "gemini-3.5-flash-lite", apiKey: null));
    }

    [Theory]
    [InlineData("gemini-3.5-flash-lite", "gemini-3.5-flash-lite")]
    [InlineData("models/gemini-3.5-flash-lite", "gemini-3.5-flash-lite")]
    [InlineData("  models/gemini-2.5-flash  ", "gemini-2.5-flash")]
    public void NormalizeModelIdStripsModelsPrefix(string input, string expected)
        => Assert.Equal(expected, GoogleAiClientProvider.NormalizeModelId(input));

    [Fact]
    public void ResolveNativeBaseUrlDropsOpenAiCompatShim()
    {
        Assert.Null(GoogleAiClientProvider.ResolveNativeBaseUrl(null));
        Assert.Null(GoogleAiClientProvider.ResolveNativeBaseUrl(
            "https://generativelanguage.googleapis.com/v1beta/openai/"));
        Assert.Equal(
            "https://generativelanguage.googleapis.com",
            GoogleAiClientProvider.ResolveNativeBaseUrl("https://generativelanguage.googleapis.com/"));
    }
}
