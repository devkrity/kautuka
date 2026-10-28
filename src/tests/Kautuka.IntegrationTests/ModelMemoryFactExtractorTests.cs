using Kautuka.Gateway.Domain.Memory;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ModelMemoryFactExtractorTests
{
    [Fact]
    public async Task ExtractAsyncBuildsPromptFromUserMessageOnly()
    {
        var client = new RecordingChatClient();
        var extractor = new ModelMemoryFactExtractor(
            new JsonMemoryResponseParser(NullLogger<JsonMemoryResponseParser>.Instance),
            NullLogger<ModelMemoryFactExtractor>.Instance);

        IReadOnlyList<GatewayMemoryFact> facts = await extractor.ExtractAsync(
            client,
            "agent",
            "agent-alpha",
            "owner-user",
            "I mainly work in C# and .NET.",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Empty(facts);
        Assert.NotNull(client.LastPrompt);
        Assert.Contains("I mainly work in C# and .NET.", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Reject aggressively", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Invoices should be paid within 30 days.", client.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("Pay invoice 1042 today", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Assistant:", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("assistant output should be ignored", client.LastPrompt, StringComparison.Ordinal);
        Assert.Equal(ChatResponseFormat.Json, client.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task ExtractAsyncReturnsOnlySinglePassDurableMemories()
    {
        var client = new RecordingChatClient(
            """
            {"memories":[
              {"title":"Invoice terms","content":"Invoices should be paid within 30 days.","category":"constraint","confidence":0.85}
            ]}
            """);
        var extractor = new ModelMemoryFactExtractor(
            new JsonMemoryResponseParser(NullLogger<JsonMemoryResponseParser>.Instance),
            NullLogger<ModelMemoryFactExtractor>.Instance);

        IReadOnlyList<GatewayMemoryFact> facts = await extractor.ExtractAsync(
            client,
            "agent",
            "agent-alpha",
            "owner-user",
            "I mainly work in C# and .NET.",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Single(facts);
        Assert.Equal("Invoice terms", facts[0].Title);
        Assert.Equal("Invoices should be paid within 30 days.", facts[0].Content);
        Assert.Equal(0.85, facts[0].Confidence, 2);
        Assert.Single(client.PromptHistory);
        Assert.DoesNotContain("Candidate memory:", client.PromptHistory[0], StringComparison.Ordinal);
        Assert.Contains("Do not emit candidate memories for later review", client.PromptHistory[0], StringComparison.Ordinal);
        Assert.Contains("Book me an aisle seat for this flight", client.PromptHistory[0], StringComparison.Ordinal);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ChatClientMetadata metadata = new("recording", null, null);
        private readonly Queue<string> responses;

        public RecordingChatClient(params string[] responses)
        {
            this.responses = new Queue<string>(responses);
        }

        public string LastPrompt { get; private set; } = string.Empty;

        public ChatOptions? LastOptions { get; private set; }

        public List<string> PromptHistory { get; } = [];

        public ChatClientMetadata Metadata => metadata;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join(
                "\n",
                messages
                    .SelectMany(static message => message.Contents)
                    .OfType<TextContent>()
                    .Select(static content => content.Text));
            PromptHistory.Add(LastPrompt);
            LastOptions = options;

            if (responses.Count == 0)
            {
                return Task.FromResult(new ChatResponse([]));
            }

            string responseText = responses.Dequeue();
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return Task.FromResult(new ChatResponse([]));
            }

            return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)]),
                ]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? key = null) => null;
    }
}
