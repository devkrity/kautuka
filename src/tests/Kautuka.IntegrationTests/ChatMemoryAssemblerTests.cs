using Kautuka.Gateway.Domain.Memory;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChatMemoryAssemblerTests
{
    [Fact]
    public void IsEmbeddingRankingEnabledReturnsFalseWhenEmbeddingsDisabled()
    {
        bool enabled = ChatMemoryAssembler.IsEmbeddingRankingEnabled(false, "agent", "hello");

        Assert.False(enabled);
    }

    [Fact]
    public void IsEmbeddingRankingEnabledReturnsFalseForNonAgentTargets()
    {
        bool enabled = ChatMemoryAssembler.IsEmbeddingRankingEnabled(true, "session", "hello");

        Assert.False(enabled);
    }

    [Fact]
    public void IsEmbeddingRankingEnabledReturnsTrueForAgentQueriesWhenEnabled()
    {
        bool enabled = ChatMemoryAssembler.IsEmbeddingRankingEnabled(true, "agent", "hello");

        Assert.True(enabled);
    }

    [Fact]
    public void IsRecoverableEmbeddingFailureReturnsTrueForTimeoutWrappedAggregate()
    {
        using CancellationTokenSource cts = new();
        Exception exception = new AggregateException(
            new TimeoutException("embedding request timed out"),
            new TaskCanceledException("embedding request cancelled by timeout"));

        bool recoverable = ChatMemoryAssembler.IsRecoverableEmbeddingFailure(exception, cts.Token);

        Assert.True(recoverable);
    }

    [Fact]
    public void IsRecoverableEmbeddingFailureReturnsFalseForCallerCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        bool recoverable = ChatMemoryAssembler.IsRecoverableEmbeddingFailure(new TaskCanceledException(), cts.Token);

        Assert.False(recoverable);
    }

    [Fact]
    public async Task AssembleOmitsAnotherUsersFactsOnASharedAgent()
    {
        var store = new GatewayMemoryStore();
        store.AddRange("agent", "shared-agent",
        [
            Fact("fact-own", "user-a", "own-secret"),
            Fact("fact-foreign", "user-b", "foreign-secret"),
            Fact("fact-blank", "", "blank-owner-secret"),
        ]);
        var assembler = new ChatMemoryAssembler(
            store,
            new DefaultMemoryScopePolicy(),
            new FixedMemoryConfiguration(),
            null!,
            null!,
            null!,
            NullLogger<ChatMemoryAssembler>.Instance);

        IReadOnlyList<InjectedMemoryFact> facts = await assembler.AssembleAsync(
            "agent",
            "shared-agent",
            "hello",
            "user-a",
            TestContext.Current.CancellationToken);

        Assert.Equal(["own-secret"], facts.Select(static fact => fact.Content).ToArray());
    }

    private static GatewayMemoryFact Fact(string id, string ownerUserId, string content)
        => new(
            id,
            "agent",
            "shared-agent",
            ownerUserId,
            content,
            content,
            "general",
            "model",
            0.9,
            "2026-10-10T00:00:00Z",
            "2026-10-10T00:00:00Z");

    private sealed class FixedMemoryConfiguration : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => new();
    }
}
