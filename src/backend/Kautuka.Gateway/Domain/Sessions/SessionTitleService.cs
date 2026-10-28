namespace Kautuka.Gateway.Domain.Sessions;

internal sealed class SessionTitleService : ISessionTitleService
{
    private const int maxTitleLength = 60;
    private const string defaultTitle = "New Chat";

    public string InitialTitle(string? originator = null) => FormatGeneratedTitle(defaultTitle, originator);

    public string ResolveTitle(string currentTitle, string? originator, string targetType, string targetId, IReadOnlyList<GatewayMessage> messages)
    {
        if (!ShouldGenerateTitle(currentTitle, originator, targetType, targetId))
        {
            return currentTitle;
        }

        GatewayMessage? firstUserMessage = messages.FirstOrDefault(static item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
            && !item.Internal);
        if (firstUserMessage is null || string.IsNullOrWhiteSpace(firstUserMessage.Content))
        {
            return currentTitle;
        }

        string normalized = firstUserMessage.Content.Trim();
        string generatedTitle = normalized.Length <= maxTitleLength
            ? normalized
            : $"{normalized[..maxTitleLength]}...";
        return FormatGeneratedTitle(generatedTitle, originator);
    }

    private static bool ShouldGenerateTitle(string currentTitle, string? originator, string targetType, string targetId)
    {
        if (string.IsNullOrWhiteSpace(currentTitle))
        {
            return true;
        }

        if (string.Equals(currentTitle, FormatGeneratedTitle(defaultTitle, originator), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(currentTitle, defaultTitle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(currentTitle, $"{targetType}:{targetId}", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatGeneratedTitle(string title, string? originator)
    {
        string prefix = ResolvePrefix(originator);
        return string.IsNullOrEmpty(prefix) ? title : $"[{prefix}] {title}";
    }

    private static string ResolvePrefix(string? originator)
        => NormalizeOrigin(originator) switch
        {
            "AUTOPILOT" => "Autopilot",
            "TELEGRAM" => "Telegram",
            "SLACK" => "Slack",
            "AGENT_TOOL" => "Tool",
            _ => "Chat",
        };

    private static string NormalizeOrigin(string? originator)
    {
        if (string.IsNullOrWhiteSpace(originator))
        {
            return "CHAT";
        }

        return originator.Trim().ToUpperInvariant();
    }
}
