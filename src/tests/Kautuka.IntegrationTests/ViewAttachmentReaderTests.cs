using System.Text.Json;

using Kautuka.ChannelContracts.Assets;
using Kautuka.Gateway.Domain.Assets;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class ViewAttachmentReaderTests
{
    [Fact]
    public async Task ReadAttachmentReturnsTheFullParsedDocument()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-view-attachment-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "view-files", "abc123def456");
        Directory.CreateDirectory(folder);
        string body = new string('p', 5000);
        await File.WriteAllTextAsync(Path.Combine(folder, "invoice.pdf"), "raw-pdf", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string resultJson = """
            {"content":[{"type":"text","text":"Attachment invoice.pdf."}],"structured_content":{"view":{"blocks":[{"type":"file","fileId":"attachment","name":"invoice.pdf","mediaType":"application/pdf","role":"primary","path":"view-files/abc123def456/invoice.pdf"}]}}}
            """;

        string read = await ViewAttachmentReader.ApplyAsync(
            "mail_read_attachment",
            resultJson,
            root,
            "session-1",
            new ScriptedIngestion(body),
            documentParsingEnabled: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        using JsonDocument document = JsonDocument.Parse(read);
        string text = document.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.StartsWith("[invoice.pdf]\n", text, StringComparison.Ordinal);
        Assert.Equal(5000, text["[invoice.pdf]\n".Length..].Length);
        Assert.Equal(body, text["[invoice.pdf]\n".Length..]);
        Assert.Equal(
            "view-files/abc123def456/invoice.pdf",
            document.RootElement.GetProperty("structured_content").GetProperty("view").GetProperty("blocks")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("raw-pdf", text, StringComparison.Ordinal);
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("mail_read_attachment")]
    [InlineData("gmail_read_attachment")]
    public async Task ReadAttachmentHandsTheImageToTheNextModelCall(string toolName)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-view-attachment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "view-files", "abc123def456"));
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        await File.WriteAllBytesAsync(Path.Combine(root, "view-files", "abc123def456", "photo.jpg"), jpeg, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var images = new MailImageHandoff();
        string resultJson = """
            {"content":[{"type":"text","text":"Attachment photo.jpg."}],"structured_content":{"view":{"blocks":[{"type":"file","name":"photo.jpg","mediaType":"image/jpeg","role":"primary","path":"view-files/abc123def456/photo.jpg"}]}}}
            """;

        string read = await ViewAttachmentReader.ApplyAsync(
            toolName,
            resultJson,
            root,
            "session-1",
            new ScriptedIngestion("unused"),
            documentParsingEnabled: true,
            TestContext.Current.CancellationToken,
            images,
            "call-photo").ConfigureAwait(true);

        Assert.Contains("[photo.jpg]", read, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", read, StringComparison.Ordinal);
        Assert.True(images.TryTake("call-photo", out MailImageHandoff.Pending held));
        Assert.Equal(jpeg, held.Bytes);
        images.Hold("call-photo", held.Name, held.MediaType, held.Bytes);
        List<ChatMessage> messages =
        [
            new(ChatRole.Tool, [new FunctionResultContent("call-photo", read)]),
        ];
        IEnumerable<ChatMessage> attached = MailImageChatClient.Attach(messages, images);
        ChatMessage picture = Assert.Single(attached, static message => message.Role == ChatRole.User);
        DataContent content = Assert.IsType<DataContent>(Assert.Single(picture.Contents));
        Assert.Equal("image/jpeg", content.MediaType);
        Assert.Equal("photo.jpg", content.Name);
        Assert.Equal(jpeg, content.Data.ToArray());
        Assert.False(images.TryTake("call-photo", out _));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ReadAttachmentReturnsTextAndIgnoresAPathOutsideTheWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-view-attachment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "view-files", "abc123def456"));
        await File.WriteAllTextAsync(Path.Combine(root, "view-files", "abc123def456", "notes.txt"), "hello from the file", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string outsideName = "kautuka-secret-" + Guid.NewGuid().ToString("N") + ".txt";
        string outside = Path.Combine(Path.GetTempPath(), outsideName);
        await File.WriteAllTextAsync(outside, "secret", TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            string notes = await ViewAttachmentReader.ApplyAsync(
                "mail_read_attachment",
                """
                {"content":[{"type":"text","text":"Attachment notes.txt."}],"structured_content":{"view":{"blocks":[{"type":"file","name":"notes.txt","mediaType":"text/plain","role":"primary","path":"view-files/abc123def456/notes.txt"}]}}}
                """,
                root,
                "session-1",
                new ScriptedIngestion("unused"),
                documentParsingEnabled: true,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("hello from the file", notes, StringComparison.Ordinal);

            string escaped = await ViewAttachmentReader.ApplyAsync(
                "mail_read_attachment",
                "{\"content\":[{\"type\":\"text\",\"text\":\"Attachment secret.txt.\"}],\"structured_content\":{\"view\":{\"blocks\":[{\"type\":\"file\",\"name\":\"secret.txt\",\"mediaType\":\"text/plain\",\"role\":\"primary\",\"path\":\"view-files/../../" + outsideName + "\"}]}}}",
                root,
                "session-1",
                new ScriptedIngestion("unused"),
                documentParsingEnabled: true,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            using JsonDocument escapedDocument = JsonDocument.Parse(escaped);
            Assert.Equal(
                "Attachment secret.txt.",
                escapedDocument.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    private sealed class ScriptedIngestion(string markdown) : IConversationAssetIngestionService
    {
        public AttachmentCategory ClassifyAttachment(string fileName, string contentType)
            => AttachmentCategoryClassifier.Classify(fileName, contentType);

        public Task<ParsedAsset> IngestParseRequiredAsync(
            string sessionId,
            string assetId,
            string fileName,
            Stream content,
            CancellationToken cancellationToken)
        {
            _ = content;
            return Task.FromResult(new ParsedAsset(
                assetId,
                sessionId,
                ParsedAssetStatus.Success,
                "test",
                "1",
                markdown,
                null,
                null,
                null,
                "now",
                "now"));
        }

        public Task<ParsedAsset?> GetCachedParsedAssetAsync(string sessionId, string assetId, CancellationToken cancellationToken)
            => Task.FromResult<ParsedAsset?>(null);
    }
}
