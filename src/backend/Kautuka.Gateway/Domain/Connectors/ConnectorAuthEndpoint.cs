namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Where mailbox sign-in is completed. A checkout with no host uses the developer's own
/// OAuth apps and a loopback callback. A release build names the operator's auth host,
/// which inserts the client id.
/// </summary>
internal sealed class ConnectorAuthEndpoint
{
    public const string LoopbackCallbackPath = "/api/connectors/oauth/callback";

    public static ConnectorAuthEndpoint Local { get; } = new(null, misconfigured: false);

    public static ConnectorAuthEndpoint Misconfigured { get; } = new(null, misconfigured: true);

    private ConnectorAuthEndpoint(string? host, bool misconfigured)
    {
        Host = host;
        IsMisconfigured = misconfigured;
    }

    public string? Host { get; }

    public bool IsHosted => Host is not null;

    public bool IsMisconfigured { get; }

    public string ProviderCallback => Host + "/callback";

    public static ConnectorAuthEndpoint Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Local;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not "/" and not ""
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return Misconfigured;
        }

        return new ConnectorAuthEndpoint(uri.GetLeftPart(UriPartial.Authority), misconfigured: false);
    }

    public string? BrokerUrl(string connectorId)
    {
        if (!IsHosted)
        {
            return null;
        }

        string? path = connectorId switch
        {
            "kautuka-google-mail" => "/google-mail/token",
            "kautuka-m365-mail" => "/microsoft-mail/token",
            _ => null,
        };
        return path is null ? null : Host + path;
    }

    public static string? KeyRoute(string connectorId)
        => connectorId switch
        {
            "kautuka-google-mail" => "google-mail",
            "kautuka-m365-mail" => "microsoft-mail",
            _ => null,
        };

    public static bool TryLoopbackCallback(string origin, out string callback)
    {
        callback = string.Empty;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            || uri.Host is not ("127.0.0.1" or "localhost")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not "/" and not ""
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        callback = uri.GetLeftPart(UriPartial.Authority) + LoopbackCallbackPath;
        return true;
    }

    public static bool IsLoopbackCallback(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && uri.Host is "127.0.0.1" or "localhost"
            && string.Equals(uri.AbsolutePath, LoopbackCallbackPath, StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }
}
