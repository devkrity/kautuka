namespace Kautuka.Gateway.Domain.Connectors;

internal static class McpHttpAuthHeaders
{
    public static Dictionary<string, string>? Create(IReadOnlyDictionary<string, string> env)
    {
        if (!env.TryGetValue("MCP_AUTH_TOKEN", out string? token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string normalizedToken = NormalizeBearerToken(token);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {normalizedToken}",
        };
    }

    private static string NormalizeBearerToken(string token)
    {
        string trimmed = token.Trim();
        const string bearerPrefix = "Bearer ";

        return trimmed.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[bearerPrefix.Length..].Trim()
            : trimmed;
    }
}
