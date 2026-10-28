using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Domain.Assets;

/// <summary>
/// Status of a document parsing operation.
/// </summary>
internal enum ParsedAssetStatus
{
    /// <summary>
    /// Parsing is in progress.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Parsing succeeded and normalized content is available.
    /// </summary>
    Success = 1,

    /// <summary>
    /// Parsing failed with an error.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The format is recognized but unsupported by the current parser.
    /// </summary>
    UnsupportedFormat = 3,
}

/// <summary>
/// Represents a parsed artifact: the normalized markdown and metadata extracted from a source document.
/// Keyed by asset_id, separate from the original conversation_assets record.
/// </summary>
internal sealed record ParsedAsset(
    string AssetId,
    string SessionId,
    ParsedAssetStatus Status,
    string ParserName,
    string ParserVersion,
    string? NormalizedMarkdown,
    string? MetadataJson,
    string? Warning,
    string? Error,
    string CreatedAt,
    string UpdatedAt)
{
    /// <summary>
    /// Summary for display; populated from Error or Warning.
    /// </summary>
    [JsonIgnore]
    public string? SummaryMessage => Error ?? Warning;

    /// <summary>
    /// True if parsing succeeded and content is available.
    /// </summary>
    [JsonIgnore]
    public bool IsSuccessful => Status == ParsedAssetStatus.Success && !string.IsNullOrEmpty(NormalizedMarkdown);
}

internal sealed record ParsedAssetCreationCommand(
    string AssetId,
    string SessionId,
    ParsedAssetStatus Status,
    string ParserName,
    string ParserVersion,
    string? NormalizedMarkdown,
    string? MetadataJson,
    string? Warning,
    string? Error);

/// <summary>
/// Persistence store for parsed artifacts.
/// </summary>
internal interface IParsedAssetStore
{
    /// <summary>
    /// Create or update a parsed artifact record.
    /// </summary>
    Task<ParsedAsset> StoreAsync(ParsedAssetCreationCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Get a parsed artifact by asset_id and session_id.
    /// </summary>
    Task<ParsedAsset?> GetAsync(string sessionId, string assetId, CancellationToken cancellationToken);

    /// <summary>
    /// Get all parsed artifacts for a session.
    /// </summary>
    Task<IReadOnlyList<ParsedAsset>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Delete all parsed artifacts for a session.
    /// </summary>
    Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken);
}
