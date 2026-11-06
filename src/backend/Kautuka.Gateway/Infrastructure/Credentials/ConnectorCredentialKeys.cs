using System.Security.Cryptography;
using System.Text;

namespace Kautuka.Gateway.Infrastructure.Credentials;

internal static class ConnectorCredentialKeys
{
    private const string httpUrlPrefix = "__connector_http_url__:";

    public static bool IsRemoteCredentialKey(string key) => key.StartsWith("__connector_remote__:", StringComparison.Ordinal);
    public static bool IsReservedConnectorKey(string key) => IsRemoteCredentialKey(key) || key.StartsWith(httpUrlPrefix, StringComparison.Ordinal);

    public static string BuildRemoteCredentialKey(string credentialNamespace, Uri endpoint, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialNamespace);
        ArgumentNullException.ThrowIfNull(endpoint);
        string destination = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
        return $"__connector_remote__:{credentialNamespace}:{destination}:{key}";
    }

    public static string BuildHttpUrlOverrideKey(string connectorId, string? credentialNamespace = null)
        => credentialNamespace is null ? $"{httpUrlPrefix}{connectorId.Trim()}" : $"{httpUrlPrefix}{credentialNamespace}:{connectorId.Trim()}";
}
