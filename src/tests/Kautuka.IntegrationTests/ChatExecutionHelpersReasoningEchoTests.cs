using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class ChatExecutionHelpersReasoningEchoTests
{
    [Fact]
    public void IsNativeReasoningEchoMatchesNewlineJoinedThinkingTokens()
    {
        const string thinking =
            "The user is asking me to do three things simultaneously:\n1. Roll a d20\n2. Roll a d30\n3. Fetch bbc.com\n4. Fetch cnn.com\n\nSince these are all independent operations, I can call them all in parallel.";
        const string echoed =
            "The\n user\n is\n asking\n me\n to\n do\n three\n things\n simultaneously\n:\n\n1\n.\n Roll\n a\n d\n2\n0\n\n2\n.\n Roll\n a\n d\n3\n0\n\n3\n.\n Fetch\n bbc\n.com\n\n4\n.\n Fetch\n cnn\n.com\n\n\nSince\n these\n are\n all\n independent\n operations\n,\n I\n can\n call\n them\n all\n in\n parallel\n.";

        Assert.True(ChatReasoningEcho.IsNativeReasoningEcho(echoed, thinking));
    }

    [Fact]
    public void IsNativeReasoningEchoRejectsRealAssistantText()
    {
        const string thinking = "The user wants four fetches in parallel.";
        const string reply = "Here are the results:\n- example.com — fetch failed";

        Assert.False(ChatReasoningEcho.IsNativeReasoningEcho(reply, thinking));
        Assert.False(ChatReasoningEcho.IsNativeReasoningEcho(thinking, string.Empty));
        Assert.False(ChatReasoningEcho.IsNativeReasoningEcho(string.Empty, thinking));
    }
}
