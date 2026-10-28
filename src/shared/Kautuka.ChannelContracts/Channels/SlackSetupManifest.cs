using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelContracts.Channels;

public sealed record SlackSlashCommand(string Name, string GatewayCommand, string Description);

/// <summary>Single source of truth for the self-service Slack app setup surface.</summary>
public static class SlackSetupManifest
{
    public static readonly SlackSlashCommand[] SlashCommands =
    [
        new("compact", "/compact", "Compact the current session context"),
        new("help", "/help", "Show routing and approval usage"),
        new("new", "/new", "Start a new session"),
        new("agents", "/agents", "Choose the agent for your next prompt"),
        new("queue", "/queue", "View and manage queued messages"),
        new("sessions", "/sessions", "View and switch agent sessions"),
        new("context", "/context", "Show the selected agent and session"),
        new("task", "/task", "Start a focused transient thread"),
        new("start", "/start", "Show the Kautuka welcome message"),
        new("session", "/session", "Show and switch sessions"),
        new("targets", "/targets", "Choose an agent for your next prompt"),
        new("target", "/target", "Choose an agent for your next prompt"),
        new("whoami", "/whoami", "Show the selected agent and session"),
    ];

    public static readonly string[] RequiredBotScopes =
    [
        "im:history",
        "im:read",
        "files:read",
        "files:write",
        "chat:write",
        "commands",
        "users:read",
    ];

    public static string Json => BuildJson(null);

    public static string BuildJson(string? ownerDisplayName)
    {
        string ownerName = string.IsNullOrWhiteSpace(ownerDisplayName) ? string.Empty : ownerDisplayName.Trim();
        string appName = string.IsNullOrWhiteSpace(ownerName)
            ? "Kautuka"
            : $"Kautuka ({Truncate(ownerName, 25)})";
        string description = string.IsNullOrWhiteSpace(ownerName)
            ? "Private Kautuka assistant. Only responds to its owner."
            : $"Private Kautuka assistant for {Truncate(ownerName, 80)}. Only responds to its owner.";

        JsonObject manifest = new()
        {
            ["display_information"] = new JsonObject
            {
                ["name"] = Truncate(appName, 35),
                ["description"] = Truncate(description, 140),
            },
            ["features"] = new JsonObject
            {
                ["app_home"] = new JsonObject
                {
                    ["home_tab_enabled"] = false,
                    ["messages_tab_enabled"] = true,
                    ["messages_tab_read_only_enabled"] = false,
                },
                ["bot_user"] = new JsonObject
                {
                    ["display_name"] = Truncate(appName, 35),
                    ["always_online"] = false,
                },
                ["slash_commands"] = JsonNode.Parse($"[{SlashCommandsJson}]")!,
            },
            ["oauth_config"] = new JsonObject
            {
                ["scopes"] = new JsonObject
                {
                    ["bot"] = new JsonArray(RequiredBotScopes.Select(static scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
                },
            },
            ["settings"] = new JsonObject
            {
                ["event_subscriptions"] = new JsonObject { ["bot_events"] = new JsonArray(JsonValue.Create("message.im")) },
                ["interactivity"] = new JsonObject { ["is_enabled"] = true },
                ["org_deploy_enabled"] = false,
                ["socket_mode_enabled"] = true,
                ["token_rotation_enabled"] = false,
                ["app_level_token_rotation_enabled"] = false,
            },
        };
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Truncate(string value, int maximumCharacters)
    {
        StringBuilder result = new();
        foreach (Rune rune in value.EnumerateRunes().Take(maximumCharacters)) result.Append(rune.ToString());
        return result.ToString();
    }

    private static string SlashCommandsJson
        => string.Join(",\n              ", SlashCommands.Select(static command =>
            $"{{ \"command\": \"/kautuka-{command.Name}\", \"description\": \"{command.Description}\", \"should_escape\": false }}"));

    public static bool TryMapSlashCommand(string slashCommand, out string gatewayCommand)
    {
        SlackSlashCommand? mapping = SlashCommands.FirstOrDefault(command =>
            string.Equals(slashCommand, $"/kautuka-{command.Name}", StringComparison.OrdinalIgnoreCase));
        gatewayCommand = mapping?.GatewayCommand ?? slashCommand;
        return mapping is not null;
    }

    public static Uri CreateAppUrl(string? ownerDisplayName = null)
        => new($"https://api.slack.com/apps?new_app=1&manifest_json={Uri.EscapeDataString(BuildJson(ownerDisplayName))}", UriKind.Absolute);
}
