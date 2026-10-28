using Kautuka.ChannelContracts.Assets;

namespace Kautuka.Gateway.Domain.Assets;

internal sealed class ConversationAssetIngestionService(
    IDocumentParsingClient parsingClient,
    IParsedAssetStore parsedAssetStore,
    GatewayState gatewayState,
    ILogger<ConversationAssetIngestionService> logger) : IConversationAssetIngestionService
{
    public AttachmentCategory ClassifyAttachment(string fileName, string contentType)
    {
        string rawExtensions = gatewayState.SystemSettings.DocumentIngestionParseRequiredExtensions;
        HashSet<string> parseRequiredExtensions = rawExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static ext => ext.StartsWith('.') ? ext : $".{ext}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return AttachmentCategoryClassifier.Classify(fileName, contentType, parseRequiredExtensions);
    }

    public async Task<ParsedAsset> IngestParseRequiredAsync(
        string sessionId,
        string assetId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        // Check if we already have a successful parsed artifact for this asset
        var cached = await parsedAssetStore.GetAsync(sessionId, assetId, cancellationToken).ConfigureAwait(false);
        if (cached?.IsSuccessful == true)
        {
            ConversationAssetIngestionLog.UsingCachedParsedArtifact(logger, assetId);
            return cached;
        }

        // Parse the document
        ConversationAssetIngestionLog.ParsingDocument(logger, fileName, assetId);
        var parseResult = await parsingClient.ParseAsync(fileName, content, cancellationToken).ConfigureAwait(false);

        // Convert parse result to parsed artifact
        var status = parseResult.Success ? ParsedAssetStatus.Success : ParsedAssetStatus.Failed;
        var command = new ParsedAssetCreationCommand(
            AssetId: assetId,
            SessionId: sessionId,
            Status: status,
            ParserName: "internal-mcp-parser",
            ParserVersion: parseResult.ParserVersion,
            NormalizedMarkdown: parseResult.NormalizedMarkdown,
            MetadataJson: parseResult.MetadataJson,
            Warning: parseResult.Warning,
            Error: parseResult.Error);

        var artifact = await parsedAssetStore.StoreAsync(command, cancellationToken).ConfigureAwait(false);
        ConversationAssetIngestionLog.StoredParsedArtifact(logger, assetId, artifact.Status);

        return artifact;
    }

    public async Task<ParsedAsset?> GetCachedParsedAssetAsync(
        string sessionId,
        string assetId,
        CancellationToken cancellationToken)
    {
        return await parsedAssetStore.GetAsync(sessionId, assetId, cancellationToken).ConfigureAwait(false);
    }
}

internal static partial class ConversationAssetIngestionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Using cached parsed artifact for asset {AssetId}")]
    public static partial void UsingCachedParsedArtifact(ILogger logger, string assetId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Parsing document {FileName} (asset: {AssetId})")]
    public static partial void ParsingDocument(ILogger logger, string fileName, string assetId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stored parsed artifact for asset {AssetId}: status={Status}")]
    public static partial void StoredParsedArtifact(ILogger logger, string assetId, ParsedAssetStatus status);
}
