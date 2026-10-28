namespace Kautuka.Gateway.Domain.Connectors;

internal sealed record McpConnectorTestResult(
    bool Success,
    string Message,
    int? ToolCount = null,
    string? PackageChecked = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyDictionary<string, string?>? ToolDescriptions = null);

internal interface IMcpConnectorTestService
{
    Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct);
}
