namespace Kautuka.M365Mail;

/// <summary>
/// Names the connector process reads from its environment. Sign-in settings live in connector.json.
/// </summary>
internal static class MicrosoftApp
{
    public const string AccessTokenEnvironmentKey = "KAUTUKA_M365_ACCESS_TOKEN";

    public const string SendToolName = "mail_send";

    public const string SignInRequiredMessage =
        "Microsoft 365 mail is not signed in. Use Sign in with Microsoft on the Connectors page.";
}
