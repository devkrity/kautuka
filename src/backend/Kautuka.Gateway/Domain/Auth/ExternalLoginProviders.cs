using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Domain.Auth;

internal sealed class ExternalLoginProviders
{
    private ExternalLoginProviders(bool google, bool microsoft, string? googleClientId, string? microsoftClientId, string? host)
    {
        Google = google;
        Microsoft = microsoft;
        GoogleClientId = googleClientId;
        MicrosoftClientId = microsoftClientId;
        Host = host;
    }

    public bool Google { get; }

    public bool Microsoft { get; }

    public string? GoogleClientId { get; }

    public string? MicrosoftClientId { get; }

    public string? Host { get; }

    public static ExternalLoginProviders Create(IConfiguration configuration, ConnectorAuthEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(endpoint);
        string? google = BlankToNull(configuration["Kautuka:Auth:Google:ClientId"]);
        string? microsoft = BlankToNull(configuration["Kautuka:Auth:Microsoft:ClientId"]);
        bool hosted = endpoint.IsHosted;
        return new ExternalLoginProviders(
            hosted && google is not null,
            hosted && microsoft is not null,
            google,
            microsoft,
            endpoint.Host);
    }

    public bool IsEnabled(string provider)
        => provider switch
        {
            "google" => Google,
            "microsoft" => Microsoft,
            _ => false,
        };

    public string? ClientId(string provider)
        => provider switch
        {
            "google" => GoogleClientId,
            "microsoft" => MicrosoftClientId,
            _ => null,
        };

    public static string? ConnectorId(string provider)
        => provider switch
        {
            "google" => "kautuka-login-google",
            "microsoft" => "kautuka-login-microsoft",
            _ => null,
        };

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
