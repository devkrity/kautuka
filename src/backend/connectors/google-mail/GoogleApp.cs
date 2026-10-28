namespace Kautuka.GoogleMail;

/// <summary>
/// Names the connector process reads from its environment. Sign-in settings live in connector.json.
/// </summary>
internal static class GoogleApp
{
    public const string AccessTokenEnvironmentKey = "KAUTUKA_GOOGLE_MAIL_ACCESS_TOKEN";

    public const string SendToolName = "gmail_send";

    public const string SignInRequiredMessage =
        "Google mail is not signed in. Use Sign in with Google on the Connectors page.";
}
