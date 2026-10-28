using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class ManagedConnectorSettingsTests
{
    [Fact]
    public void RegistrationDropsUserLaunchSettingsWhenNoBundleIsInstalled()
    {
        GatewayConnectorUpsertRequest request = ManagedConnectorSettings.Registration(Profile(), Existing(
            image: "mcp/markitdown",
            command: "mcp",
            args: ["--transport", "sse"],
            sidecar: "markitdown",
            bundlePath: null,
            enabled: false));

        Assert.Equal("Microsoft 365 mail", request.Name);
        Assert.Null(request.ContainerImage);
        Assert.Equal("kautuka-m365-mail", request.ContainerCommand);
        Assert.Empty(request.ContainerArgs!);
        Assert.Null(request.ContainerSidecarName);
        Assert.Null(request.HealthcheckUrl);
        Assert.False(request.Enabled);
        Assert.NotNull(request.EnvKeys);
        Assert.Equal(["KAUTUKA_M365_REFRESH_TOKEN"], request.EnvKeys);
    }

    [Fact]
    public void RegistrationKeepsAnInstalledBundle()
    {
        GatewayConnectorUpsertRequest request = ManagedConnectorSettings.Registration(Profile(), Existing(
            image: "kautuka/m365-mail:1",
            command: "kautuka-m365-mail",
            args: [],
            sidecar: null,
            bundlePath: "/opt/kautuka/kautuka-m365-mail.mcpb",
            enabled: true));

        Assert.Equal("kautuka/m365-mail:1", request.ContainerImage);
        Assert.Equal("/opt/kautuka/kautuka-m365-mail.mcpb", request.BundlePath);
    }

    [Fact]
    public void EnableChangeKeepsLaunchSettings()
    {
        GatewayMcpServer existing = Existing(
            image: "kautuka/m365-mail:1",
            command: "kautuka-m365-mail",
            args: [],
            sidecar: null,
            bundlePath: "/opt/kautuka/kautuka-m365-mail.mcpb",
            enabled: true);

        GatewayConnectorUpsertRequest request = ManagedConnectorSettings.WithEnabled(existing, false);

        Assert.False(request.Enabled);
        Assert.Equal("kautuka/m365-mail:1", request.ContainerImage);
        Assert.Equal("kautuka-m365-mail", request.ContainerCommand);
        Assert.Equal("/opt/kautuka/kautuka-m365-mail.mcpb", request.BundlePath);
    }

    private static ConnectorAuthProfile Profile() => new()
    {
        Id = "kautuka-m365-mail",
        Name = "Microsoft 365 mail",
        Description = "Read, draft, and send mail.",
        Grant = "authorization_code",
        AuthorizeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        Scopes = "offline_access Mail.ReadWrite Mail.Send User.Read",
        RedirectUri = "https://auth.kautuka.io/callback",
        StartUrl = "https://auth.kautuka.io/start",
        RefreshCredentialKey = "KAUTUKA_M365_REFRESH_TOKEN",
        AccessTokenEnvironmentKey = "KAUTUKA_M365_ACCESS_TOKEN",
        RevokeUrl = "https://myaccount.microsoft.com/permissions",
        SignInLabel = "Sign in with Microsoft",
        EntryPoint = "kautuka-m365-mail",
        ToolNames = ["mail_list", "mail_send"],
        AskFirst = ["mail_send"],
    };

    private static GatewayMcpServer Existing(
        string? image,
        string? command,
        string[] args,
        string? sidecar,
        string? bundlePath,
        bool enabled)
        => new(
            "kautuka-m365-mail",
            "Renamed",
            "edited",
            string.Empty,
            "kautuka-m365-mail",
            "container",
            null,
            image,
            command,
            args,
            sidecar,
            bundlePath,
            "binary",
            45,
            600,
            "http://127.0.0.1:20011/health",
            2000,
            enabled,
            true,
            "2026-10-06T00:00:00.0000000+00:00",
            "2026-10-06T00:00:00.0000000+00:00",
            "system");
}
