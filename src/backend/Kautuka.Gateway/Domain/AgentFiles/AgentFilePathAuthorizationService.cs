using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.AgentFiles;

internal sealed class AgentFilePathAuthorizationService(
    IGatewayCatalogStore catalog,
    IWorkspaceRuntimeTool workspaceTool) : IAgentFilePathAuthorizationService
{
    private static readonly string[] deniedSegments = [".kautuka"];

    public AgentFilePathAuthorizationResult Authorize(string agentId, GatewayUser user, string? relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(user);

        if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent) || agent is null)
        {
            return new AgentFilePathAuthorizationResult(
                AgentFileOperationStatus.Forbidden,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                "Forbidden.");
        }

        WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, user.Id);
        string rootPath = Path.GetFullPath(workspace.HostWorkspacePath);

        if (!TryNormalizeRelativePath(relativePath, out string normalizedPath, out string? errorMessage))
        {
            return new AgentFilePathAuthorizationResult(
                AgentFileOperationStatus.InvalidPath,
                agent,
                rootPath,
                rootPath,
                string.Empty,
                errorMessage ?? "Requested path is not accessible.");
        }

        if (ContainsDeniedSegment(normalizedPath))
        {
            return new AgentFilePathAuthorizationResult(
                AgentFileOperationStatus.InvalidPath,
                agent,
                rootPath,
                rootPath,
                string.Empty,
                "Requested path is not accessible.");
        }

        string resolvedPath = normalizedPath.Length == 0
            ? rootPath
            : Path.GetFullPath(Path.Combine(rootPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsUnderRoot(rootPath, resolvedPath)
            || ContainsDeniedSegment(Path.GetRelativePath(rootPath, resolvedPath).Replace('\\', '/'))
            || WorkspaceFile.NamesExistingWindowsAlias(rootPath, resolvedPath)
            || WorkspaceReparseGuard.ContainsReparsePoint(rootPath, resolvedPath))
        {
            return new AgentFilePathAuthorizationResult(
                AgentFileOperationStatus.InvalidPath,
                agent,
                rootPath,
                rootPath,
                string.Empty,
                "Requested path is not accessible.");
        }

        return new AgentFilePathAuthorizationResult(
            AgentFileOperationStatus.Success,
            agent,
            rootPath,
            resolvedPath,
            normalizedPath);
    }

    private static bool TryNormalizeRelativePath(string? relativePath, out string normalizedPath, out string? errorMessage)
    {
        normalizedPath = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        string candidate = relativePath.Trim();
        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            errorMessage = "Requested path is not accessible.";
            return false;
        }

        try
        {
            candidate = Uri.UnescapeDataString(candidate);
        }
        catch (UriFormatException)
        {
            errorMessage = "Requested path is not accessible.";
            return false;
        }

        if (Path.IsPathRooted(candidate))
        {
            errorMessage = "Requested path is not accessible.";
            return false;
        }

        string[] segments = candidate
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Any(static segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment) || IsUnsafeSegment(segment)))
        {
            errorMessage = "Requested path is not accessible.";
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    private static bool IsUnsafeSegment(string segment)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (segment.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        string name = segment.TrimEnd(' ', '.');
        return name.Length != segment.Length || WorkspaceAttachmentUploadPolicy.IsReservedWindowsDeviceName(name);
    }

    private static bool ContainsDeniedSegment(string normalizedPath)
        => normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => deniedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static bool IsUnderRoot(string rootPath, string resolvedPath)
    {
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, resolvedPath, StringComparison.Ordinal)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
