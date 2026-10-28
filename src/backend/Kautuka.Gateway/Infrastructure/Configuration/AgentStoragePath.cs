namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static class AgentStoragePath
{
    internal static string RequireAgentDirectory(string sharedStateRoot, string segment)
    {
        string agentsRoot = Path.GetFullPath(Path.Combine(sharedStateRoot, "agents"));
        string directory = Path.GetFullPath(Path.Combine(agentsRoot, segment));
        string? parent = Path.GetDirectoryName(directory);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.IsNullOrEmpty(segment)
            || segment is "." or ".."
            || parent is null
            || !string.Equals(Path.GetFullPath(parent), agentsRoot, comparison)
            || !string.Equals(Path.GetFileName(directory), segment, StringComparison.Ordinal)
            || HasCaseAlias(agentsRoot, segment))
        {
            throw new ArgumentException("Agent id is not a valid storage name.");
        }

        return directory;
    }

    private static bool HasCaseAlias(string agentsRoot, string segment)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(agentsRoot))
        {
            return false;
        }

        foreach (string existing in Directory.EnumerateDirectories(agentsRoot))
        {
            string name = Path.GetFileName(existing);
            if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
