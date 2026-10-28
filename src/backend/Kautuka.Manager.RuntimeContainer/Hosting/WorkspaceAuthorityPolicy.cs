using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class WorkspaceAuthorityPolicy
{
    public static void ValidateDesiredState(
        ManagerRuntimeSettings runtimeSettings,
        IReadOnlyList<ContainerContext> desiredWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(desiredWorkspaces);

        string root = CanonicalizeRoot(runtimeSettings.Workspace.BaseRoot);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ContainerContext workspace in desiredWorkspaces)
        {
            ValidateStableId(workspace.ScopeType, nameof(workspace.ScopeType));
            ValidateStableId(workspace.ScopeId, nameof(workspace.ScopeId));
            string identity = string.Join('|', workspace.ScopeType, workspace.ScopeId, workspace.UserId ?? string.Empty);
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException("Desired state contains a duplicate workspace identity.");
            }

            string hostPath = ValidateDescendant(root, workspace.HostWorkspacePath);
            if (!hostPaths.Add(hostPath))
            {
                throw new InvalidOperationException("Desired state contains two workspaces for the same host path.");
            }
            if (string.IsNullOrWhiteSpace(workspace.ContainerWorkspacePath)
                || workspace.ContainerWorkspacePath[0] != '/')
            {
                throw new InvalidOperationException("Workspace container path must be an absolute container path.");
            }
        }
    }

    public static string CanonicalizeRoot(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("Workspace root is not configured.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if ((Directory.Exists(root) || File.Exists(root))
            && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Workspace root cannot be a symbolic link or reparse point.");
        }

        return root;
    }

    public static string ValidateDescendant(string canonicalRoot, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        string relative = Path.GetRelativePath(canonicalRoot, candidate);
        if (string.Equals(relative, ".", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workspace path is outside the configured workspace root.");
        }

        RejectExistingReparsePoints(canonicalRoot, candidate);
        return candidate;
    }

    private static void ValidateStableId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException($"{parameterName} is not a valid stable identifier.");
        }
    }

    private static void RejectExistingReparsePoints(string root, string candidate)
    {
        string current = root;
        string relative = Path.GetRelativePath(root, candidate);
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Workspace path traverses a symbolic link or reparse point.");
            }
        }
    }
}
