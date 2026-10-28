using System.Text.Json;

using Kautuka.ChannelContracts.Channels;

namespace Kautuka.ChannelHost.Tests;

public sealed class SlackSetupManifestTests
{
    private static readonly string[] expectedScopes = ["chat:write", "commands", "files:read", "files:write", "im:history", "im:read", "users:read"];

    [Fact]
    public void ManifestContainsTheScopesAndFeaturesCheckedBySetup()
    {
        using JsonDocument manifest = JsonDocument.Parse(SlackSetupManifest.Json);
        JsonElement root = manifest.RootElement;
        string[] scopes = root.GetProperty("oauth_config").GetProperty("scopes").GetProperty("bot")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray();
        string[] events = root.GetProperty("settings").GetProperty("event_subscriptions").GetProperty("bot_events")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray();
        string[] commands = root.GetProperty("features").GetProperty("slash_commands")
            .EnumerateArray().Select(static item => item.GetProperty("command").GetString()!).ToArray();

        Assert.Equal(SlackSetupManifest.RequiredBotScopes.Order(StringComparer.Ordinal), scopes.Order(StringComparer.Ordinal));
        Assert.Equal(expectedScopes, scopes.Order(StringComparer.Ordinal));
        Assert.Equal(["message.im"], events);
        Assert.Contains("/kautuka-queue", commands);
        Assert.Contains("/kautuka-sessions", commands);
        Assert.Equal(
            SlackSetupManifest.SlashCommands.Select(static item => "/kautuka-" + item.Name).Order(StringComparer.Ordinal),
            commands.Order(StringComparer.Ordinal));
        foreach (ChannelCommandDescriptor command in ChannelBotCommandCatalogue.Default)
            Assert.Contains("/kautuka-" + command.Command, commands);
        foreach (SlackSlashCommand command in SlackSetupManifest.SlashCommands)
        {
            Assert.True(SlackSetupManifest.TryMapSlashCommand("/kautuka-" + command.Name, out string mapped));
            Assert.Equal(command.GatewayCommand, mapped);
        }
        Assert.True(root.GetProperty("settings").GetProperty("socket_mode_enabled").GetBoolean());
        Assert.True(root.GetProperty("features").GetProperty("app_home").GetProperty("messages_tab_enabled").GetBoolean());
        Assert.False(root.GetProperty("settings").GetProperty("token_rotation_enabled").GetBoolean());
        Assert.False(root.GetProperty("settings").GetProperty("app_level_token_rotation_enabled").GetBoolean());
        Assert.DoesNotContain("xoxb-", SlackSetupManifest.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("xapp-", SlackSetupManifest.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJsonUsesOwnerNameAndEnforcesSlackDisplayLimits()
    {
        using JsonDocument manifest = JsonDocument.Parse(SlackSetupManifest.BuildJson(new string('A', 500)));
        JsonElement root = manifest.RootElement;
        JsonElement display = root.GetProperty("display_information");
        string appName = display.GetProperty("name").GetString()!;
        string botName = root.GetProperty("features").GetProperty("bot_user").GetProperty("display_name").GetString()!;
        string description = display.GetProperty("description").GetString()!;

        Assert.StartsWith("Kautuka (", appName, StringComparison.Ordinal);
        Assert.EndsWith(")", appName, StringComparison.Ordinal);
        Assert.Equal(appName, botName);
        Assert.True(appName.Length <= 35);
        Assert.StartsWith("Private Kautuka assistant for ", description, StringComparison.Ordinal);
        Assert.EndsWith(". Only responds to its owner.", description, StringComparison.Ordinal);
        Assert.True(description.Length <= 140);
    }

    [Fact]
    public void BuildJsonFallsBackToKautukaWhenOwnerNameIsMissing()
    {
        using JsonDocument manifest = JsonDocument.Parse(SlackSetupManifest.BuildJson("  "));
        JsonElement root = manifest.RootElement;
        Assert.Equal("Kautuka", root.GetProperty("display_information").GetProperty("name").GetString());
        Assert.Equal("Kautuka", root.GetProperty("features").GetProperty("bot_user").GetProperty("display_name").GetString());
    }

    [Fact]
    public void CreateAppUrlEmbedsTheGeneratedManifest()
    {
        Uri uri = SlackSetupManifest.CreateAppUrl();
        string manifestParameter = Uri.UnescapeDataString(uri.Query.Split("manifest_json=", 2)[1]);

        Assert.Equal("api.slack.com", uri.Host);
        Assert.Contains("new_app=1", uri.Query, StringComparison.Ordinal);
        Assert.Equal(SlackSetupManifest.Json, manifestParameter);
    }

    [Fact]
    public void CreateAppUrlUsesTheOwnerSpecificManifest()
    {
        Uri uri = SlackSetupManifest.CreateAppUrl("Devkrity");
        string manifestParameter = Uri.UnescapeDataString(uri.Query.Split("manifest_json=", 2)[1]);
        using JsonDocument manifest = JsonDocument.Parse(manifestParameter);
        Assert.Equal("Kautuka (Devkrity)", manifest.RootElement.GetProperty("display_information").GetProperty("name").GetString());
    }
}
