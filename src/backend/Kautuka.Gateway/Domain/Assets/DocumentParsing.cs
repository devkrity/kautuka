using System.Text.Json.Serialization;

using Kautuka.ChannelContracts.Assets;

namespace Kautuka.Gateway.Domain.Assets;

/// <summary>
/// Result of a document parsing operation.
/// </summary>
internal sealed record DocumentParsingResult
{
    /// <summary>
    /// True if parsing succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The normalized markdown content (when Success is true).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NormalizedMarkdown { get; init; }

    /// <summary>
    /// Parser metadata as JSON (e.g., page count, extracted tables, etc.).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataJson { get; init; }

    /// <summary>
    /// Non-fatal warning message (e.g., "Some tables skipped due to complexity").
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }

    /// <summary>
    /// Fatal error message (when Success is false).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>
    /// Parser name and version used.
    /// </summary>
    public required string ParserVersion { get; init; }
}

/// <summary>
/// Client abstraction for sending documents to the MarkItDown MCP connector.
/// </summary>
internal interface IDocumentParsingClient
{
    /// <summary>
    /// Parse a document and return normalized markdown.
    /// </summary>
    Task<DocumentParsingResult> ParseAsync(string fileName, Stream content, CancellationToken cancellationToken);

    /// <summary>
    /// Check if the parsing service is healthy.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates document classification, parsing, and persistence.
/// </summary>
internal interface IConversationAssetIngestionService
{
    /// <summary>
    /// Classify an attachment by file name and content type.
    /// </summary>
    AttachmentCategory ClassifyAttachment(string fileName, string contentType);

    /// <summary>
    /// Ingest a parse-required document: parse via the MarkItDown MCP connector, persist
    /// the result, and return the parsed artifact or error state.
    /// </summary>
    Task<ParsedAsset> IngestParseRequiredAsync(
        string sessionId,
        string assetId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get a cached parsed artifact for an asset, if it exists.
    /// </summary>
    Task<ParsedAsset?> GetCachedParsedAssetAsync(string sessionId, string assetId, CancellationToken cancellationToken);
}
