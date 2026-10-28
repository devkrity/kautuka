namespace Kautuka.Telemetry.Telemetry;

public static class KautukaRepoPaths
{
    public static string? TryResolveRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        DirectoryInfo? current = new(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (IsRepositoryRoot(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsRepositoryRoot(string directoryPath)
    {
        return Directory.Exists(Path.Combine(directoryPath, ".git"))
            || File.Exists(Path.Combine(directoryPath, ".git"))
            || File.Exists(Path.Combine(directoryPath, "package.json"))
            || File.Exists(Path.Combine(directoryPath, "src", "Kautuka.slnx"));
    }
}
