using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.AI;

using OllamaSharp;

namespace Kautuka.IntegrationTests;

public sealed class OllamaAiClientProviderTests
{
    [Fact]
    public void TryCreateReturnsNativeOllamaApiClient()
    {
        var provider = new OllamaAiClientProvider(defaultBaseUrl: "http://localhost:11434");
        IChatClient? client = provider.TryCreate(new JsonObject(), "gemma4:12b", apiKey: null);

        Assert.NotNull(client);
        Assert.IsType<OllamaApiClient>(client);
    }

    [Fact]
    public void TryCreateEmbeddingGeneratorReturnsNativeOllamaApiClient()
    {
        var provider = new OllamaAiClientProvider(defaultBaseUrl: "http://localhost:11434");
        IEmbeddingGenerator<string, Embedding<float>>? client =
            provider.TryCreateEmbeddingGenerator(new JsonObject(), "gemma4:12b", apiKey: null);

        Assert.NotNull(client);
        Assert.IsType<OllamaApiClient>(client);
    }

    [Fact]
    public void TryCreateRequiresBaseUrl()
    {
        var provider = new OllamaAiClientProvider(defaultBaseUrl: null);
        Assert.Null(provider.TryCreate(new JsonObject(), "gemma4:12b", apiKey: null));
    }

    /// <summary>
    /// Regression for nested AAAT / tool continuation: OllamaSharp maps
    /// <see cref="FunctionResultContent"/> through internal
    /// <c>OllamaFunctionResultContent</c>. Our incomplete public AOT mirror omitted that
    /// type and threw at Path <c>$.Messages</c> after invoke_agent_roller approval.
    /// </summary>
    [Fact]
    public async Task ChatRequestWithFunctionResultSerializesAgainstProviderClientOptions()
    {
        var handler = new CapturingHandler(
            """
            {"model":"gemma4:12b","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"rolled 7"},"done":true}
            """);
#pragma warning disable CA2000
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
#pragma warning restore CA2000

        // Same construction path as OllamaAiClientProvider (built-in OllamaSharp context).
        var client = new OllamaApiClient(
            http,
            "gemma4:12b",
            ResolveOllamaSharpJsonContextForTest());

        ChatMessage[] messages =
        [
            new(ChatRole.User, "roll a d222"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "invoke_agent_roller",
                    new Dictionary<string, object?> { ["message"] = "roll a d222" }),
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", """{"roll":7}"""),
            ]),
        ];

        // This is the wire path that previously threw JsonTypeInfo for OllamaFunctionResultContent.
        ChatResponse response = await ((IChatClient)client).GetResponseAsync(
            messages,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("rolled", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"role\":\"tool\"", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("CallId", handler.LastRequestBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves the incomplete public mirror still fails the tool-result path — so the
    /// provider must not use it as the primary TypeInfoResolver.
    /// </summary>
    [Fact]
    public async Task IncompletePublicAotMirrorCannotSerializeFunctionResults()
    {
        var handler = new CapturingHandler(
            """
            {"model":"x","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true}
            """);
#pragma warning disable CA2000
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
#pragma warning restore CA2000

        var client = new OllamaApiClient(http, "x", OllamaSharpAotJsonContext.Default);

        ChatMessage[] messages =
        [
            new(ChatRole.Tool, [new FunctionResultContent("call-1", """{"roll":7}""")]),
        ];

        NotSupportedException ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await ((IChatClient)client).GetResponseAsync(
                messages,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("OllamaFunctionResultContent", ex.Message, StringComparison.Ordinal);
    }

    private static JsonSerializerContext ResolveOllamaSharpJsonContextForTest()
    {
        Type contextType = Type.GetType("OllamaSharp.Models.JsonSourceGenerationContext, OllamaSharp", throwOnError: true)!;
        return (JsonSerializerContext)contextType
            .GetProperty("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return response;
        }
    }
}
