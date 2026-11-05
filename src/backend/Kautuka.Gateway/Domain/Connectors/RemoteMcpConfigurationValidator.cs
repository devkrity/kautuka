using System.Text;

namespace Kautuka.Gateway.Domain.Connectors;

internal static class RemoteMcpConfigurationValidator
{
    private static readonly HashSet<string> CredentialQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "api-key", "apikey", "access_token", "access-token", "accesstoken",
        "refresh_token", "refresh-token", "refreshtoken", "token", "secret", "password",
        "authorization", "tavilyApiKey", "exaApiKey"
    };

    public static void ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new InvalidOperationException("Use an HTTP or HTTPS MCP endpoint without embedded credentials or a fragment.");
        if ((endpoint.IdnHost.Equals("mcp.exa.ai", StringComparison.OrdinalIgnoreCase)
                && Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(endpoint.Query).ContainsKey("login"))
            || (endpoint.IdnHost.Equals("mcp.firecrawl.dev", StringComparison.OrdinalIgnoreCase)
                && endpoint.AbsolutePath.TrimEnd('/').Equals("/v2/mcp-oauth", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This remote MCP URL uses OAuth sign-in, which isn't supported yet. Use its API-key or keyless MCP endpoint.");
        if (Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(endpoint.Query).Keys.Any(CredentialQueryNames.Contains))
            throw new InvalidOperationException("Remove credentials from the MCP URL and configure credential-backed headers.");
    }

    private static readonly HashSet<string> Controlled = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Proxy-Connection",
        "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Mcp-Session-Id", "Mcp-Protocol-Version",
        "Accept", "Content-Type", "Last-Event-ID"
    };
    private static bool IsSensitive(string name)
        => name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    public static void Validate(string transport, IReadOnlyList<RemoteMcpCredential> credentials, IReadOnlyList<RemoteMcpHeader> headers)
    {
        if (!string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (credentials.Count != 0 || headers.Count != 0)
                throw new InvalidOperationException("Remote credentials and headers require HTTP transport.");
            return;
        }
        if (headers.Count > 20) throw new InvalidOperationException("At most 20 connector headers are allowed.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (RemoteMcpCredential credential in credentials)
        {
            if (credential is null || string.IsNullOrWhiteSpace(credential.Key) || string.IsNullOrWhiteSpace(credential.Label)
                || credential.Key.Length > 137 || credential.Key != credential.Key.Trim() || credential.Key.Any(char.IsControl) || credential.Label.Any(char.IsControl))
                throw new InvalidOperationException("Credential key and label must be nonempty and valid.");
            if (!keys.Add(credential.Key)) throw new InvalidOperationException("Credential keys must be unique.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            RemoteMcpHeader header = headers[i];
            if (header is null || string.IsNullOrEmpty(header.Name) || Encoding.UTF8.GetByteCount(header.Name) > 256 || !header.Name.All(IsToken))
                throw new InvalidOperationException($"Header at index {i} has an invalid name or exceeds 256 bytes.");
            string name = header.Name;
            if (!names.Add(name)) throw new InvalidOperationException($"Header '{name}' is duplicated.");
            if (Controlled.Contains(name)) throw new InvalidOperationException($"Header '{name}' is controlled by the transport.");
            bool literal = header.Value is not null;
            bool reference = header.CredentialKey is not null;
            if (literal == reference || (literal && header.Prefix is not null))
                throw new InvalidOperationException($"Header '{name}' must have exactly one valid source.");
            if (reference && !keys.Contains(header.CredentialKey!))
                throw new InvalidOperationException($"Header '{name}' references an undeclared credential.");
            if (literal && IsSensitive(name))
                throw new InvalidOperationException($"Header '{name}' requires a credential reference.");
            string value = literal ? header.Value! : header.Prefix ?? string.Empty;
            if (value.Any(c => c == '\r' || c == '\n' || (char.IsControl(c) && c != '\t')))
                throw new InvalidOperationException($"Header '{name}' contains invalid characters.");
            int bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > 8192) throw new InvalidOperationException($"Header '{name}' exceeds 8192 bytes.");
            total += Encoding.UTF8.GetByteCount(name) + bytes;
        }
        if (total > 32768) throw new InvalidOperationException("Connector headers exceed 32768 bytes.");
    }
    private static bool IsToken(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);
}
