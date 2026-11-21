using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.NetworkAccess.Client;

namespace Kautuka.IntegrationTests;

public sealed class ProviderModelTransportRoutingTests
{
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[],"models":[]}""", Encoding.UTF8, "application/json"),
            });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
