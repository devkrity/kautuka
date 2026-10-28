namespace Kautuka.ChannelContracts.Channels;

/// <summary>
/// Canonical bot/slash command catalogue shared by Gateway provisioning and ChannelHost owner sync.
/// </summary>
public static class ChannelBotCommandCatalogue
{
    private const string quickActionPrefix = "kautuka:quick:";

    public static IReadOnlyList<string> QuickActionCommands { get; } =
    [
        "agents",
        "sessions",
        "new",
        "context",
        "queue",
    ];

    public static IReadOnlyList<ChannelCommandDescriptor> Default { get; } =
    [
        new("compact", "Compact the current session context"),
        new("help", "Show routing and HITL usage"),
        new("new", "Start a new session"),
        new("agents", "Choose the agent for your next prompt"),
        new("queue", "View and manage queued messages"),
        new("sessions", "View and switch agent sessions"),
        new("task", "Start a focused transient thread"),
        new("context", "Show the selected agent and session"),
    ];

    public static string CreateQuickActionCallback(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        string normalized = command.Trim().TrimStart('/');
        if (!QuickActionCommands.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported quick action command.", nameof(command));
        return quickActionPrefix + normalized;
    }

    public static bool TryGetQuickActionCommand(string? callbackData, out string command)
    {
        command = string.Empty;
        if (callbackData is null || !callbackData.StartsWith(quickActionPrefix, StringComparison.Ordinal))
            return false;

        string name = callbackData[quickActionPrefix.Length..];
        if (!QuickActionCommands.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        command = "/" + name;
        return true;
    }
}
