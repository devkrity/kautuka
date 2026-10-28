using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ModelProbeServiceTests
{
    [Fact]
    public void ClearProbeErrorWhenResolvedRemovesStaleProbeError()
    {
        Kautuka.Gateway.Domain.Models.GatewayModelCapabilities capabilities = new(
            ToolCalling: true,
            Streaming: true,
            ReasoningOnlyOutput: null,
            StructuredJson: true,
            AgentConversations: true,
            ProbeError: "429 Too Many Requests",
            ProbedAt: "2026-05-12T00:02:00Z");

        Kautuka.Gateway.Domain.Models.GatewayModelCapabilities cleaned = ModelProbeService.ClearProbeErrorWhenResolved(capabilities);

        Assert.Null(cleaned.ProbeError);
        Assert.True(cleaned.ToolCalling);
        Assert.True(cleaned.Streaming);
        Assert.True(cleaned.StructuredJson);
        Assert.True(cleaned.AgentConversations);
    }

    [Fact]
    public void ClearProbeErrorWhenResolvedPreservesProbeErrorWhenResultIsStillUnknown()
    {
        Kautuka.Gateway.Domain.Models.GatewayModelCapabilities capabilities = new(
            ToolCalling: true,
            Streaming: null,
            ReasoningOnlyOutput: null,
            StructuredJson: true,
            AgentConversations: null,
            ProbeError: "429 Too Many Requests",
            ProbedAt: "2026-05-12T00:02:00Z");

        Kautuka.Gateway.Domain.Models.GatewayModelCapabilities cleaned = ModelProbeService.ClearProbeErrorWhenResolved(capabilities);

        Assert.Equal("429 Too Many Requests", cleaned.ProbeError);
        Assert.Null(cleaned.Streaming);
        Assert.Null(cleaned.AgentConversations);
    }

    [Fact]
    public async Task ProbeCapabilitiesTreatsUnknownOllamaFinishReasonAsUnknownInsteadOfThrowing()
    {
        var service = new ModelProbeService(
            null!,
            null!,
            NullLogger<ModelProbeService>.Instance);

        Kautuka.Gateway.Domain.Models.GatewayModelCapabilities capabilities = await service
            .ProbeCapabilitiesAsync(new UnknownFinishReasonChatClient())
            .ConfigureAwait(true);

        Assert.Null(capabilities.Streaming);
        Assert.Null(capabilities.ToolCalling);
        Assert.Null(capabilities.StructuredJson);
        Assert.Null(capabilities.AgentConversations);
        Assert.Contains("Unknown ChatFinishReason value", capabilities.ProbeError, StringComparison.Ordinal);
    }

    private sealed class UnknownFinishReasonChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("ollama-unknown-finish-reason", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(CreateFailure());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return await Task.FromException<ChatResponseUpdate>(CreateFailure()).ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }

        private static ArgumentOutOfRangeException CreateFailure()
            => new("value", "Unknown ChatFinishReason value returned by the Ollama OpenAI-compatible endpoint.");
    }
}
