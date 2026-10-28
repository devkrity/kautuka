using Kautuka.Tui.Infrastructure.Gateway;

namespace Kautuka.IntegrationTests;

public sealed class TuiNaradAuthorityEventParserTests
{
    [Fact]
    public void ParseSseDataReturnsNullForHydrateRecords()
    {
        GatewayNaradAuthorityEvent? parsed = NaradAuthorityEventParser.ParseSseData(
            """{"type":"messages.snapshot","messages":[]}""");

        Assert.Null(parsed);
    }

    [Fact]
    public void ParseSseDataMapsNativeMessageTextDelta()
    {
        GatewayNaradAuthorityEvent? parsed = NaradAuthorityEventParser.ParseSseData(
            """
            {
              "type": "message.text.delta",
              "protocolVersion": "narad/v1",
              "eventId": "evt-2",
              "sessionId": "session-1",
              "sessionSequence": 2,
              "messageId": "assistant-1",
              "delta": "hello"
            }
            """);

        Assert.NotNull(parsed);
        Assert.Equal("message.text.delta", parsed.Type);
        Assert.Equal("evt-2", parsed.EventId);
        Assert.Equal("hello", parsed.Delta);
        Assert.Equal("assistant-1", parsed.MessageId);
    }

    [Fact]
    public void ParseSseDataMapsToolCallFinishedPayload()
    {
        GatewayNaradAuthorityEvent? parsed = NaradAuthorityEventParser.ParseSseData(
            """
            {
              "type": "tool.call.finished",
              "protocolVersion": "narad/v1",
              "eventId": "evt-3",
              "sessionId": "session-1",
              "sessionSequence": 3,
              "toolCallId": "tool-1",
              "toolName": "search",
              "outcome": "success",
              "content": "done"
            }
            """);

        Assert.NotNull(parsed);
        Assert.Equal("tool.call.finished", parsed.Type);
        Assert.Equal("tool-1", parsed.ToolCallId);
        Assert.Equal("search", parsed.ToolName);
        Assert.Equal("success", parsed.Outcome);
        Assert.Equal("done", parsed.Content);
    }

    [Theory]
    [InlineData("run.finished", true)]
    [InlineData("run.error", true)]
    [InlineData("run.cancelled", true)]
    [InlineData("message.text.delta", false)]
    public void TerminalRunDetectionUsesNativeNaradTypes(string eventType, bool terminal)
        => Assert.Equal(terminal, NaradAuthorityEventParser.IsTerminalRunEvent(eventType));
}
