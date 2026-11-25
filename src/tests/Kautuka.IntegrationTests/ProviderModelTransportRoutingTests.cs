using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using System.Runtime.CompilerServices;

using Kautuka.Gateway.Infrastructure.Egress;
using Kautuka.NetworkAccess.Client;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ProviderModelTransportRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeModelWaitsBeforeCallingProviderAndKeepsProbesImmediate(bool streaming)
    {
        var checker = new HoldingModelChecker();
        using var provider = new RecordingModelClient();
        using var client = new ApprovalAwareModelChatClient(provider, new GatewayOutboundApproval(checker, enabled: true),
            new Uri("https://model.example.test"));
        CancellationToken token = TestContext.Current.CancellationToken;
        using IDisposable scope = NetworkAccessContext.BeginScope("user-a", "agent-a", purpose: "agent-run");

        Task pending = SendAsync();
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("agent-a", checker.Target?.AgentId);

        using (NetworkAccessContext.BeginScope("user-a", "agent-a", purpose: "ai-probe"))
            await SendAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(1, provider.Calls);
        Assert.False(pending.IsCompleted);

        checker.Approved.SetResult(NetworkAccessCheckResult.CreateAllowed("rule_agent"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(2, provider.Calls);
        Assert.False(NetworkAccessContext.Current!.SkipApprovalHold);

        async Task SendAsync()
        {
            if (streaming)
            {
                await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync([], cancellationToken: token)) { }
            }
            else
            {
                await client.GetResponseAsync([], cancellationToken: token);
            }
        }
    }

    [Fact]
    public async Task RuntimeEmbeddingCancellationDoesNotSendAndApprovalResumesTheOriginalRequest()
    {
        var checker = new HoldingModelChecker();
        using var provider = new RecordingEmbeddingGenerator();
        using var generator = new ApprovalAwareEmbeddingGenerator(provider, new GatewayOutboundApproval(checker, enabled: true),
            new Uri("https://model.example.test"));
        CancellationToken token = TestContext.Current.CancellationToken;
        using IDisposable scope = NetworkAccessContext.BeginScope("user-a", "agent-a", purpose: "agent-run");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<GeneratedEmbeddings<Embedding<float>>> abandoned = generator.GenerateAsync(["cancelled"], cancellationToken: cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
        Assert.Equal(0, provider.Calls);

        Task<GeneratedEmbeddings<Embedding<float>>> waiting = generator.GenerateAsync(["original"], cancellationToken: token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, provider.Calls);
        checker.Approved.SetResult(NetworkAccessCheckResult.CreateAllowed("rule_agent"));

        Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(5), token));
        Assert.Equal(1, provider.Calls);
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

    private sealed class HoldingModelChecker : INetworkAccessChecker
    {
        public TaskCompletionSource<NetworkAccessCheckResult> Approved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutboundTarget? Target { get; private set; }

        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
        {
            Assert.Equal("model.example.test", target.Host);
            Assert.Equal("agent-run", target.Purpose);
            Target = target;
            return Approved.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingModelClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.True(NetworkAccessContext.Current!.SkipApprovalHold);
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ready")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(chatMessages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ready");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("original", Assert.Single(values));
            Assert.True(NetworkAccessContext.Current!.SkipApprovalHold);
            Calls++;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1 })]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
