using System.Text.Json;
using System.Threading.Channels;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ModelTurnProgressChatClientTests
{
    [Fact]
    public async Task StreamingTurnPublishesPublicAgentTurnProgressWithToolCount()
    {
        var publisher = new RecordingNaradEventPublisher();
        var inner = new ScriptedOllamaChatClient(
        [
            new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent("call-1", "alpha"),
                    new FunctionCallContent("call-2", "beta"),
                    new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 3 }),
                ],
            },
        ]);
        var client = new ModelTurnProgressChatClient(inner);
        var context = new NaradEventContext("agent-1", "session-1", "user-1", "run-1", "run-1");

        using (ModelTurnProgressContext.Begin(publisher, context))
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "go")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }
        }

        Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, item => Assert.Equal(NaradEventTypes.ProgressRecorded, item.EventType));

        JsonElement started = NaradPersistedEventReader.ParseBody(publisher.Published[0]);
        Assert.Equal(NaradProgressCategories.AgentTurn, started.GetProperty("category").GetString());
        Assert.Equal("agent-turn:run-1:1", started.GetProperty("progressId").GetString());
        Assert.Equal("Agent turn 1", started.GetProperty("label").GetString());
        Assert.Equal("active", started.GetProperty("status").GetString());
        Assert.Equal("public", started.GetProperty("privacy").GetString());

        JsonElement completed = NaradPersistedEventReader.ParseBody(publisher.Published[1]);
        Assert.Equal("agent-turn:run-1:1", completed.GetProperty("progressId").GetString());
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal("2 tool calls: alpha, beta", completed.GetProperty("detail").GetString());
        Assert.Equal(12, completed.GetProperty("usage").GetProperty("promptTokens").GetInt64());
        Assert.Equal(3, completed.GetProperty("usage").GetProperty("completionTokens").GetInt64());
        Assert.Equal(15, completed.GetProperty("usage").GetProperty("totalTokens").GetInt64());
    }

    [Fact]
    public async Task StreamingTurnCountsUniqueToolsWhenProviderRepeatsTheSameCall()
    {
        var publisher = new RecordingNaradEventPublisher();
        var arguments = new Dictionary<string, object?> { ["url"] = "https://www.bbc.com" };
        var inner = new ScriptedOllamaChatClient(
        [
            new ChatResponseUpdate { Contents = [new FunctionCallContent("a", "kautuka_web_fetch", arguments)] },
            new ChatResponseUpdate { Contents = [new FunctionCallContent("b", "kautuka_web_fetch", arguments)] },
            new ChatResponseUpdate { Contents = [new FunctionCallContent("c", "kautuka_web_fetch", arguments)] },
        ]);
        var client = new ModelTurnProgressChatClient(inner);
        var context = new NaradEventContext("agent-1", "session-1", "user-1", "run-1", "run-1");

        using (ModelTurnProgressContext.Begin(publisher, context))
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "go")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }
        }

        JsonElement completed = NaradPersistedEventReader.ParseBody(publisher.Published[1]);
        Assert.Equal(
            "1 tool call: kautuka_web_fetch url=https://www.bbc.com [streamed 3]",
            completed.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task SequentialRequestsNumberTurnsAndStaySilentWithoutScope()
    {
        var publisher = new RecordingNaradEventPublisher();
        var inner = new ScriptedOllamaChatClient(
            [new ChatResponseUpdate { Contents = [new FunctionCallContent("call-a", "alpha")] }],
            [new ChatResponseUpdate { Contents = [new TextContent("done")] }]);
        var client = new ModelTurnProgressChatClient(inner);

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "ungated")],
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
        }

        Assert.Empty(publisher.Published);

        inner = new ScriptedOllamaChatClient(
            [new ChatResponseUpdate { Contents = [new FunctionCallContent("call-a", "alpha")] }],
            [new ChatResponseUpdate { Contents = [new TextContent("done")] }]);
        client = new ModelTurnProgressChatClient(inner);
        var context = new NaradEventContext(
            "agent-1", "session-1", "user-1", "run-child", "run-root", ParentRunId: "run-root");

        using (ModelTurnProgressContext.Begin(publisher, context))
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "one")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }

            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "two")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }
        }

        Assert.Equal(4, publisher.Published.Count);
        JsonElement secondStart = NaradPersistedEventReader.ParseBody(publisher.Published[2]);
        Assert.Equal("agent-turn:run-child:2", secondStart.GetProperty("progressId").GetString());
        Assert.Equal("Nested agent turn 2", secondStart.GetProperty("label").GetString());
        JsonElement secondDone = NaradPersistedEventReader.ParseBody(publisher.Published[3]);
        Assert.Equal("no tool calls", secondDone.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task HitlContinuationKeepsMonotonicProgressIdsAndMirrorsOntoTheProduceChannel()
    {
        var publisher = new RecordingNaradEventPublisher();
        var ordinals = new ModelTurnOrdinalCounter();
        var channel = Channel.CreateUnbounded<NaradPersistedEvent>();
        var inner = new ScriptedOllamaChatClient(
            [new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 })] }],
            [new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 20, OutputTokenCount = 3 })] }]);
        var client = new ModelTurnProgressChatClient(inner);
        var context = new NaradEventContext("agent-1", "session-1", "user-1", "run-1", "run-1");

        using (ModelTurnProgressContext.Begin(publisher, context, channel.Writer, ordinals))
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "first")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }
        }

        using (ModelTurnProgressContext.Begin(publisher, context, channel.Writer, ordinals))
        {
            await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "resume")],
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
            }
        }

        channel.Writer.TryComplete();
        var mirrored = new List<NaradPersistedEvent>();
        while (await channel.Reader.WaitToReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            while (channel.Reader.TryRead(out NaradPersistedEvent? persisted))
            {
                mirrored.Add(persisted);
            }
        }

        Assert.Equal(4, publisher.Published.Count);
        Assert.Equal(
            ["agent-turn:run-1:1", "agent-turn:run-1:1", "agent-turn:run-1:2", "agent-turn:run-1:2"],
            publisher.Published.Select(static item => NaradPersistedEventReader.ParseBody(item).GetProperty("progressId").GetString()));
        Assert.Equal("Agent turn 2", NaradPersistedEventReader.ParseBody(publisher.Published[2]).GetProperty("label").GetString());
        Assert.Equal(20, NaradPersistedEventReader.ParseBody(publisher.Published[3]).GetProperty("usage").GetProperty("promptTokens").GetInt64());
        Assert.Equal(
            publisher.Published.Select(static item => item.EventId),
            mirrored.Select(static item => item.EventId));
    }

    private sealed class RecordingNaradEventPublisher : IRuntimeEventPublisher
    {
        private readonly TestNaradEventPublisher inner = new();

        public List<NaradPersistedEvent> Published { get; } = [];

        public async ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            NaradPersistedEvent persisted = await inner.PublishAsync(draft, context, cancellationToken)
                .ConfigureAwait(true);
            Published.Add(persisted);
            return persisted;
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(
                PublishAsync, eventId, eventType, body, context, cancellationToken);
    }
}
