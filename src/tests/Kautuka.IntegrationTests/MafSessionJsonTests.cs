using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class MafSessionJsonTests
{
    [Fact]
    public async Task StripAgentRequestMessageAttributionsRemovesAttributionBeforeSerialize()
    {
        ChatClientAgent agent = new(
            new NoOpChatClient(),
            new ChatClientAgentOptions
            {
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
            });

        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var history = (InMemoryChatHistoryProvider)agent.ChatHistoryProvider!;
        ChatMessage tagged = new ChatMessage(ChatRole.User, "roll a d999")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "test-user");
        history.SetMessages(session, [tagged]);

        Assert.True(tagged.AdditionalProperties?.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey) == true);

        MafSessionJson.StripAgentRequestMessageAttributions(agent, session);

        Assert.False(tagged.AdditionalProperties?.ContainsKey(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey) == true);

        JsonElement serialized = await agent.SerializeSessionAsync(session, cancellationToken: TestContext.Current.CancellationToken);
        string json = serialized.GetRawText();
        Assert.Contains("roll a d999", json, StringComparison.Ordinal);
        Assert.DoesNotContain("_attribution", json, StringComparison.Ordinal);
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Empty();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static async IAsyncEnumerable<ChatResponseUpdate> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
