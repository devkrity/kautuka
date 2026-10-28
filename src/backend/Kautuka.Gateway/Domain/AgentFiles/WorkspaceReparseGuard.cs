namespace Kautuka.Gateway.Domain.AgentFiles;

internal static class WorkspaceReparseGuard
{
    public static bool ContainsReparsePoint(string rootPath, string resolvedPath)
    {
        string current = Path.GetFullPath(rootPath);
        if (HasReparsePoint(current))
        {
            return true;
        }

        string relative = Path.GetRelativePath(current, Path.GetFullPath(resolvedPath));
        if (relative is "." or "")
        {
            return false;
        }

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Path.Exists(current))
            {
                return false;
            }

            if (HasReparsePoint(current))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasReparsePoint(string path)
    {
        try
        {
            // GetAttributes follows a Linux symlink, so a container-created link is invisible
            // to the reparse bit. LinkTarget reads the link itself on Windows and Linux.
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            {
                return true;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static IEnumerable<string> EnumerateFiles(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (HasReparsePoint(entry.FullName))
            {
                continue;
            }

            if (entry is FileInfo file)
            {
                yield return file.FullName;
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                foreach (string nested in EnumerateFiles(child.FullName))
                {
                    yield return nested;
                }
            }
        }
    }

    public static bool ContainsFile(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (HasReparsePoint(entry.FullName))
            {
                return true;
            }

            if (entry is FileInfo)
            {
                return true;
            }

            if (entry is DirectoryInfo child && ContainsFile(child.FullName))
            {
                return true;
            }
        }

        return false;
    }
}
