namespace Kautuka.Gateway.Domain.Configuration;

/// <summary>
/// Options for document parsing and ingestion.
/// </summary>
internal sealed class DocumentIngestionOptions
{
    public const string Section = "DocumentIngestion";

    /// <summary>
    /// Enable document parsing and ingestion (default: true).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Container MCP connector id to use for internal document parsing.
    /// </summary>
    public string InternalParserConnectorId { get; set; } = "markitdown";

    /// <summary>
    /// Preferred tool name on the internal parser MCP server.
    /// </summary>
    public string InternalParserToolName { get; set; } = "convert_to_markdown";

    /// <summary>
    /// Timeout for parsing requests (default: 30 seconds).
    /// </summary>
    public TimeSpan ParserTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Comma-separated list of file extensions requiring parsing (e.g., ".pdf,.docx,.pptx,.xlsx").
    /// </summary>
    public string ParseRequiredExtensions { get; set; } = ".pdf,.docx,.pptx,.xlsx";

    /// <summary>
    /// Enable PDF-to-image fallback for complex or scan-like PDFs (default: false).
    /// </summary>
    public bool PdfImageFallbackEnabled { get; set; }

    /// <summary>
    /// DPI for PDF-to-image conversion (default: 150).
    /// </summary>
    public int PdfImageDpi { get; set; } = 150;

    /// <summary>
    /// Maximum number of pages to render for PDF-to-image fallback (default: 50).
    /// </summary>
    public int PdfMaxImagePages { get; set; } = 50;

    /// <summary>
    /// Allow routing parsed documents through a vision-capable model (if fallback or vision path is needed).
    /// </summary>
    public bool VisionRouteAllowed { get; set; }

    public HashSet<string> ParseRequiredExtensionSet()
    {
        return new HashSet<string>(
            ParseRequiredExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }
}
