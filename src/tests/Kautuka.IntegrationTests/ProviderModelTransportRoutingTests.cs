using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.NetworkAccess.Client;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ProviderModelTransportRoutingTests
{
    [Theory]
    [InlineData("chat")]
    [InlineData("stream")]
    [InlineData("embedding")]
    public async Task RuntimeModelSendsOnlyAfterAnExplicitRetry(string operation)
    {
        var checker = new RetryNetworkChecker();
        using var handler = new RecordingHandler();
        var provider = new OllamaAiClientProvider(defaultBaseUrl: "http://model.example.test",
            transportFactory: _ => new NetworkAccessCheckHandler(checker, handler));
        var settings = new JsonObject();
        using IDisposable scope = NetworkAccessContext.BeginScope("user-a", "agent-a", purpose: "agent-run");
        CancellationToken token = TestContext.Current.CancellationToken;
        using var chat = provider.TryCreate(settings, "model", null)!;
        using var embeddings = provider.TryCreateEmbeddingGenerator(settings, "model", null)!;

        await Assert.ThrowsAsync<NetworkAccessDeniedException>(SendAsync);
        Assert.Equal("agent-a", Assert.Single(checker.Targets).AgentId);
        checker.Allowed = true;
        Assert.Equal(0, handler.RequestCount);
        await SendAsync();
        Assert.Equal(1, handler.RequestCount);

        async Task SendAsync()
        {
            if (operation == "embedding") await embeddings.GenerateAsync(["hello"], cancellationToken: token);
            else if (operation == "stream")
            {
                await foreach (var _ in chat.GetStreamingResponseAsync([new(ChatRole.User, "hello")], cancellationToken: token)) { }
            }
            else await chat.GetResponseAsync([new(ChatRole.User, "hello")], cancellationToken: token);
        }
    }

    [Fact]
    public async Task DiscoveryUsesOptionalTransportForEveryProvider()
    {
        var handlers = new List<RecordingHandler>();
        HttpMessageHandler Create(INetworkAccessChecker? _)
        {
            var handler = new RecordingHandler();
            handlers.Add(handler);
            return handler;
        }
        var settings = new JsonObject { ["base_url"] = "http://example.test" };
        var openai = new OpenAiCompatibleProvider("openai", transportFactory: Create);
        var responses = new OpenAiResponsesCompatibleProvider(transportFactory: Create);
        var ollama = new OllamaAiClientProvider(transportFactory: Create);
        var google = new GoogleAiClientProvider(transportFactory: Create);
        CancellationToken token = TestContext.Current.CancellationToken;
        await openai.DiscoverModelsAsync(settings, "key", true, token);
        await responses.DiscoverModelsAsync(settings, "key", true, token);
        await ollama.DiscoverModelsAsync(settings, null, true, token);
        await google.DiscoverModelsAsync(settings, "key", true, token);
        Assert.Equal(4, handlers.Count);
        Assert.All(handlers, handler => Assert.True(handler.RequestCount > 0));
        Assert.True(handlers[0].Disposed);
        Assert.True(handlers[1].Disposed);
        Assert.True(handlers[2].Disposed);
    }

    [Fact]
    public async Task ExplicitResponsesHandlerRemainsCallerOwned()
    {
        using var explicitHandler = new RecordingHandler();
        var provider = new OpenAiResponsesCompatibleProvider(transportHandler: explicitHandler,
            transportFactory: _ => throw new InvalidOperationException("Unexpected transport factory call."));
        await provider.DiscoverModelsAsync(new JsonObject { ["base_url"] = "http://example.test" }, "key", true, TestContext.Current.CancellationToken);
        Assert.Equal(1, explicitHandler.RequestCount);
        Assert.False(explicitHandler.Disposed);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            string body = request.RequestUri?.AbsolutePath switch
            {
                "/api/chat" => """{"model":"model","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ready"},"done":true}""",
                "/api/embed" => """{"model":"model","embeddings":[[1.0]]}""",
                _ => """{"data":[],"models":[]}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

}

internal sealed class RetryNetworkChecker : INetworkAccessChecker
{
    public bool Allowed { get; set; }
    public List<OutboundTarget> Targets { get; } = [];

    public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
    {
        Targets.Add(target);
        return Task.FromResult(Allowed ? NetworkAccessCheckResult.CreateAllowed("rule_user")
            : NetworkAccessCheckResult.CreateDenied("no_rule", "pending-request"));
    }
}
