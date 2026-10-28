namespace Kautuka.Gateway.Infrastructure.Credentials;

internal static class ConnectorCredentialKeys
{
    private const string httpUrlPrefix = "__connector_http_url__:";

    public static string BuildHttpUrlOverrideKey(string connectorId)
        => $"{httpUrlPrefix}{connectorId.Trim()}";
}
