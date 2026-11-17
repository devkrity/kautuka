namespace Kautuka.Gateway.Domain.Connectors;

internal sealed record ConnectorToolDefinition(string Name, string Description, string JsonSchema);

internal sealed record McpConnectorTestResult(
    bool Success,
    string Message,
    int? ToolCount = null,
    string? PackageChecked = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyDictionary<string, string?>? ToolDescriptions = null,
    bool NetworkApprovalRequired = false,
    IReadOnlyList<ConnectorToolDefinition>? ToolDefinitions = null,
    string? ImageDigest = null,
    string? SourceDigest = null);

internal interface IMcpConnectorTestService
{
    Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct);
}
