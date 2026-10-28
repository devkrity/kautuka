namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Date versions accepted by ModelContextProtocol.Core 2.2.0.
/// An empty value leaves the client on its automatic handshake.
/// </summary>
internal static class McpProtocolVersions
{
    private static readonly string[] supported =
    [
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        "2025-11-25",
        "2026-07-28",
    ];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        foreach (string version in supported)
        {
            if (string.Equals(trimmed, version, StringComparison.Ordinal))
            {
                return version;
            }
        }

        throw new InvalidOperationException(
            $"Unsupported MCP protocol version '{trimmed}'. Supported versions are {string.Join(", ", supported)}.");
    }
}
