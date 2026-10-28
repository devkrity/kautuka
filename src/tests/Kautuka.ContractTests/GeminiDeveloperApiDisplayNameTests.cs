using Kautuka.Gateway.Infrastructure.AI;

using Microsoft.Extensions.AI;

namespace Kautuka.ContractTests;

public sealed class GeminiDeveloperApiDisplayNameTests
{
    [Fact]
    public void StripRemovesDataContentNameWithoutMutatingOriginal()
    {
        var original = new DataContent((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3 }, "image/png") { Name = "shot.png" };
        var message = new ChatMessage(ChatRole.User, [original]);

        IReadOnlyList<ChatMessage> stripped = GeminiDeveloperApiDisplayName.Strip([message]);

        Assert.Equal("shot.png", original.Name);
        DataContent rewritten = Assert.IsType<DataContent>(Assert.Single(stripped[0].Contents));
        Assert.True(string.IsNullOrWhiteSpace(rewritten.Name));
        Assert.Equal("image/png", rewritten.MediaType);
        Assert.Equal(original.Data.ToArray(), rewritten.Data.ToArray());
    }

    [Fact]
    public void StripLeavesTextOnlyMessagesUnchanged()
    {
        var message = new ChatMessage(ChatRole.User, "hello");
        IReadOnlyList<ChatMessage> stripped = GeminiDeveloperApiDisplayName.Strip([message]);
        Assert.Same(message, stripped[0]);
    }

    [Fact]
    public void StripRemovesHostedFileName()
    {
        var hosted = new HostedFileContent("files/abc") { Name = "notes.pdf", MediaType = "application/pdf" };
        IReadOnlyList<ChatMessage> stripped = GeminiDeveloperApiDisplayName.Strip(
            [new ChatMessage(ChatRole.User, [hosted])]);

        HostedFileContent rewritten = Assert.IsType<HostedFileContent>(Assert.Single(stripped[0].Contents));
        Assert.True(string.IsNullOrWhiteSpace(rewritten.Name));
        Assert.Equal("files/abc", rewritten.FileId);
        Assert.Equal("application/pdf", rewritten.MediaType);
    }
}
