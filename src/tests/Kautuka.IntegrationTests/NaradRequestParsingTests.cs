using System.Text.Json;

using Kautuka.Gateway.Endpoints.Runtime;

namespace Kautuka.IntegrationTests;

public sealed class NaradRequestParsingTests
{
    [Fact]
    public void ExtractAssetIdsReadsDirectAssetIdList()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "message": "Review this file.",
              "assetIds": ["asset-1", "asset-2"]
            }
            """);

        IReadOnlyList<string> assetIds = NaradRequestParsing.ExtractAssetIds(document.RootElement);
        Assert.Equal(["asset-1", "asset-2"], assetIds);
    }

    [Fact]
    public void EmptyMessageIsAcceptedWhenExtractingUserMessage()
    {
        using JsonDocument document = JsonDocument.Parse("""{"message":"   "}""");

        NaradUserMessage? message = NaradRequestParsing.TryExtractUserMessage(document.RootElement);
        Assert.NotNull(message);
        Assert.Equal(string.Empty, message.Content);
    }

    [Fact]
    public void DirectMessageFieldIsPreferredOverMessagesArray()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "message": "direct",
              "messages": [{ "role": "user", "content": "ignored" }]
            }
            """);

        NaradUserMessage? message = NaradRequestParsing.TryExtractUserMessage(document.RootElement);
        Assert.NotNull(message);
        Assert.Equal("direct", message.Content);
    }

    [Fact]
    public void ExtractWorkspaceFilePathsReadsHashReferencesFromMessageText()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "message": "Summarize #reports/weekly notes.txt please"
            }
            """);

        IReadOnlyList<string> paths = NaradRequestParsing.ExtractWorkspaceFilePaths(document.RootElement);
        string[] variants = [.. NaradRequestParsing.GetWorkspaceFileCandidateVariants(paths.Single())];

        Assert.Equal(["reports/weekly notes.txt please"], paths);
        Assert.Contains("reports/weekly notes.txt", variants);
    }

    [Fact]
    public void OversizeMessageThrowsValidationException()
    {
        string huge = new('x', NaradRequestParsing.MaxMessageLength + 1);
        using JsonDocument document = JsonDocument.Parse($$"""{"message":"{{huge}}"}""");

        Assert.Throws<NaradRequestValidationException>(() => NaradRequestParsing.TryExtractUserMessage(document.RootElement));
    }
}
