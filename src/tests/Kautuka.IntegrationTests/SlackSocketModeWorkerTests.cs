using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.Protos;

namespace Kautuka.IntegrationTests;

public sealed class SlackSocketModeWorkerTests
{
    [Fact]
    public void TryCreateEventsApiMessageForTestDropsBotEchoWhenBotUserIdMatches()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            event_id = "evt-bot-1",
            @event = new
            {
                type = "message",
                channel_type = "im",
                user = "U_BOT_1",
                channel = "D123",
                text = "agent reply",
            },
        });
        JsonElement settings = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["bot_user_id"] = "U_BOT_1",
        });

        ChannelInboundEventMessage? message = SlackSocketModeWorker.TryCreateEventsApiMessageForTest(
            "slack:user-1",
            "user-1",
            payload,
            settings);

        Assert.Null(message);
    }

    [Fact]
    public void TryCreateEventsApiMessageForTestAllowsHumanMessage()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            event_id = "evt-human-1",
            @event = new
            {
                type = "message",
                channel_type = "im",
                user = "U_HUMAN_1",
                channel = "D123",
                text = "hello from slack",
                thread_ts = "1710000000.000100",
            },
        });
        JsonElement settings = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["bot_user_id"] = "U_BOT_1",
        });

        ChannelInboundEventMessage? message = SlackSocketModeWorker.TryCreateEventsApiMessageForTest(
            "slack:user-1",
            "user-1",
            payload,
            settings);

        Assert.NotNull(message);
        Assert.Equal("evt-human-1", message!.EventId);
        Assert.Equal("U_HUMAN_1", message.ExternalUserId);
        Assert.Equal("D123", message.ConversationId);
        Assert.Equal("hello from slack", message.Text);
    }

    [Theory]
    [InlineData("app_mention", "im")]
    [InlineData("message", "channel")]
    [InlineData("message", "group")]
    [InlineData("message", "mpim")]
    public void TryCreateEventsApiMessageForTestDropsNonPrivateEvents(string type, string channelType)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            event_id = "evt-rejected",
            @event = new { type, channel_type = channelType, user = "U_OTHER", channel = "C123", text = "must not route" },
        });

        Assert.Null(SlackSocketModeWorker.TryCreateEventsApiMessageForTest("slack-1", "owner", payload));
    }
}
