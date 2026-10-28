namespace Kautuka.Gateway.Domain.Channels;

internal static class ChannelCommandParser
{
    public static bool IsCommand(string message, string command)
        => string.Equals(message.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split('@', 2)[0], command, StringComparison.OrdinalIgnoreCase);

}
