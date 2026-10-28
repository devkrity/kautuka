using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.Gateway.Infrastructure.Assets;

internal sealed class MarkItDownMcpClient(
    IContainerMcpConnectorClient connectorClient,
    GatewayState gatewayState,
    ILogger<MarkItDownMcpClient> logger) : IDocumentParsingClient
{
    private const string parserVersion = "internal-mcp-parser/1.0";
    private readonly IContainerMcpConnectorClient connectorClient = connectorClient;
    private readonly GatewayState gatewayState = gatewayState;
    private readonly ILogger<MarkItDownMcpClient> logger = logger;

    public async Task<DocumentParsingResult> ParseAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            string connectorId = gatewayState.SystemSettings.DocumentIngestionInternalParserConnectorId;
            if (string.IsNullOrWhiteSpace(connectorId))
            {
                return Failed("DocumentIngestion.InternalParserConnectorId is not configured.");
            }

            if (!gatewayState.McpServers.TryGetValue(connectorId, out GatewayMcpServer? connector)
                || !connector.IsInternalSystemConnector)
            {
                return Failed($"Internal parser connector '{connectorId}' is not available as an internal system connector.");
            }

            byte[] bytes = await ReadAllBytesAsync(content, cancellationToken).ConfigureAwait(false);
            string base64 = Convert.ToBase64String(bytes);
            int timeoutSeconds = Math.Max(1, gatewayState.SystemSettings.DocumentIngestionParserTimeoutSeconds);

            MarkItDownMcpClientLog.ParsingRequested(logger, fileName, connectorId, connector.Enabled);

            var listResult = await connectorClient
                .ListMcpConnectorToolsAsync(connectorId, [], timeoutSeconds, cancellationToken, allowInternalSystemConnector: true)
                .ConfigureAwait(false);

            if (!listResult.Ok)
            {
                MarkItDownMcpClientLog.ListToolsFailed(logger, connectorId, listResult.Message);
                return Failed($"Internal parser connector unavailable: {listResult.Message}");
            }

            string toolName = ResolveToolName(listResult.ToolNames);
            string inputJson = BuildToolInputJson(fileName, base64);

            var callResult = await connectorClient
                .CallMcpConnectorToolAsync(connectorId, [], toolName, inputJson, timeoutSeconds, cancellationToken, allowInternalSystemConnector: true)
                .ConfigureAwait(false);

            if (!callResult.Ok)
            {
                MarkItDownMcpClientLog.ToolCallFailed(logger, connectorId, toolName, callResult.Message);
                return Failed($"Internal parser tool call failed: {callResult.Message}");
            }

            string resultJson = string.IsNullOrWhiteSpace(callResult.ResultJson)
                ? "{}"
                : callResult.ResultJson;

            string? markdown = TryExtractMarkdown(resultJson);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return Failed("MarkItDown MCP returned no markdown content.");
            }

            return new DocumentParsingResult
            {
                Success = true,
                NormalizedMarkdown = markdown,
                MetadataJson = resultJson,
                ParserVersion = parserVersion,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException ex)
        {
            MarkItDownMcpClientLog.ParseFailed(logger, ex, fileName);
            return Failed($"Parsing service error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            MarkItDownMcpClientLog.ParseFailed(logger, ex, fileName);
            return Failed($"Parsing service error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            MarkItDownMcpClientLog.ParseFailed(logger, ex, fileName);
            return Failed($"Parsing service error: {ex.Message}");
        }
        catch (RpcException ex)
        {
            MarkItDownMcpClientLog.ParseFailed(logger, ex, fileName);
            return Failed($"Parsing service error: {ex.Message}");
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
    {
        string connectorId = gatewayState.SystemSettings.DocumentIngestionInternalParserConnectorId;
        if (string.IsNullOrWhiteSpace(connectorId))
        {
            return false;
        }

        try
        {
            int timeoutSeconds = Math.Max(1, gatewayState.SystemSettings.DocumentIngestionParserTimeoutSeconds);
            var listResult = await connectorClient
                .ListMcpConnectorToolsAsync(connectorId, [], timeoutSeconds, cancellationToken, allowInternalSystemConnector: true)
                .ConfigureAwait(false);

            return listResult.Ok;
        }
        catch (RpcException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream content, CancellationToken cancellationToken)
    {
        if (content is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment))
        {
            return segment.ToArray();
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private string ResolveToolName(IReadOnlyList<string> availableTools)
    {
        string configuredToolName = gatewayState.SystemSettings.DocumentIngestionInternalParserToolName;
        if (!string.IsNullOrWhiteSpace(configuredToolName)
            && availableTools.Any(tool => string.Equals(tool, configuredToolName, StringComparison.OrdinalIgnoreCase)))
        {
            return configuredToolName;
        }

        string? inferred = availableTools.FirstOrDefault(tool =>
            tool.Contains("markdown", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("convert", StringComparison.OrdinalIgnoreCase)
            || tool.Contains("parse", StringComparison.OrdinalIgnoreCase));

        return inferred ?? configuredToolName;
    }

    private static string BuildToolInputJson(string fileName, string base64)
    {
        string mimeType = GetMimeType(fileName);
        string dataUri = $"data:{mimeType};base64,{base64}";

        var input = new JsonObject
        {
            ["uri"] = dataUri,
            ["file_name"] = fileName,
        };

        return input.ToJsonString();
    }

    private static string GetMimeType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToUpperInvariant();
        return extension switch
        {
            ".PDF" => "application/pdf",
            ".DOCX" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".DOC" => "application/msword",
            ".PPTX" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".PPT" => "application/vnd.ms-powerpoint",
            ".XLSX" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".XLS" => "application/vnd.ms-excel",
            ".TXT" => "text/plain",
            ".MD" => "text/markdown",
            _ => "application/octet-stream",
        };
    }

    private static string? TryExtractMarkdown(string resultJson)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(resultJson);
            if (node is null)
            {
                return null;
            }

            // Handles direct result payloads, content arrays, and wrapped structures.
            string? direct = FindFirstString(node, ["normalized_markdown", "markdown", "result", "text", "content"]);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            if (node["Content"] is JsonArray content)
            {
                var textParts = content
                    .Select(item => item?["Text"]?.GetValue<string>())
                    .Where(static value => !string.IsNullOrWhiteSpace(value));

                string merged = string.Join("\n\n", textParts!);
                if (!string.IsNullOrWhiteSpace(merged))
                {
                    return merged;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindFirstString(JsonNode node, IReadOnlyList<string> names)
    {
        if (node is JsonObject obj)
        {
            foreach (string name in names)
            {
                if (obj[name] is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                string? nested = FindFirstString(pair.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }

            return null;
        }

        if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                if (item is null)
                {
                    continue;
                }

                string? nested = FindFirstString(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static DocumentParsingResult Failed(string error)
        => new()
        {
            Success = false,
            Error = error,
            ParserVersion = parserVersion,
        };
}

internal static partial class MarkItDownMcpClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to parse document {FileName} via MarkItDown MCP")]
    public static partial void ParseFailed(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "[InternalParser] Parsing '{FileName}' via connector '{ConnectorId}' (enabled={Enabled})")]
    public static partial void ParsingRequested(ILogger logger, string fileName, string connectorId, bool enabled);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "[InternalParser] Tool listing failed for connector '{ConnectorId}': {Message}")]
    public static partial void ListToolsFailed(ILogger logger, string connectorId, string message);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "[InternalParser] Tool call failed for connector '{ConnectorId}' tool '{ToolName}': {Message}")]
    public static partial void ToolCallFailed(ILogger logger, string connectorId, string toolName, string message);
}
