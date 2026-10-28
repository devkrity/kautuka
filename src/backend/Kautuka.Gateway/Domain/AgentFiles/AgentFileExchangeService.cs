using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Stores;
namespace Kautuka.Gateway.Domain.AgentFiles;

internal sealed class AgentFileExchangeService(
    IAgentFilePathAuthorizationService pathAuthorizationService,
    IConversationAssetStore conversationAssetStore) : IAgentFileExchangeService
{
    private const long maxUploadBytes = 15L * 1024L * 1024L;
    private const int defaultSearchLimit = 8;
    private const int maxSearchLimit = 25;
    private const string folderCreationFailedMessage = "Folder could not be created in the selected location.";
    private const string referencedAttachmentDeleteBlockedMessage = "File cannot be deleted while it is referenced by a retained chat session.";
    private static readonly HashSet<string> reservedInternalEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".kautuka",
    };

    private readonly record struct DirectoryAggregate(int FileCount, long TotalSizeBytes);

    public AgentFileListOperationResult ListDirectory(string agentId, GatewayUser user, string? relativePath)
    {
        AgentFilePathAuthorizationResult authorization = pathAuthorizationService.Authorize(agentId, user, relativePath);
        if (authorization.Status is not AgentFileOperationStatus.Success || authorization.Agent is null)
        {
            return new AgentFileListOperationResult(authorization.Status, ErrorMessage: authorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(authorization.Agent.Sandbox);
        if (!sandbox.Enabled)
        {
            return new AgentFileListOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    authorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        Directory.CreateDirectory(authorization.RootPath);

        if (!Directory.Exists(authorization.ResolvedPath))
        {
            return new AgentFileListOperationResult(AgentFileOperationStatus.NotFound, ErrorMessage: "Folder not found.");
        }

        var directoryInfo = new DirectoryInfo(authorization.ResolvedPath);
        DirectoryAggregate workspaceSummary = SummarizeDirectory(new DirectoryInfo(authorization.RootPath));
        GatewayAgentFileListEntry[] entries = [.. directoryInfo
            .EnumerateFileSystemInfos()
            .Where(static entry => !IsSkippedWorkspaceEntry(entry))
            .Select(entry => new GatewayAgentFileListEntry(
                entry.Name,
                string.IsNullOrWhiteSpace(authorization.RelativePath) ? entry.Name : $"{authorization.RelativePath}/{entry.Name}",
                entry is DirectoryInfo ? "directory" : "file",
                entry is FileInfo fileInfo ? fileInfo.Length : null,
                new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero),
                entry is DirectoryInfo childDirectory ? SummarizeDirectory(childDirectory).FileCount : null))];

        return new AgentFileListOperationResult(
            AgentFileOperationStatus.Success,
            new GatewayAgentFileListResponse(authorization.Agent.Id, authorization.RelativePath, entries, workspaceSummary.FileCount, workspaceSummary.TotalSizeBytes));
    }

    public AgentFileSearchOperationResult SearchWorkspace(string agentId, GatewayUser user, string query, int limit)
    {
        AgentFilePathAuthorizationResult authorization = pathAuthorizationService.Authorize(agentId, user, null);
        if (authorization.Status is not AgentFileOperationStatus.Success || authorization.Agent is null)
        {
            return new AgentFileSearchOperationResult(authorization.Status, ErrorMessage: authorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(authorization.Agent.Sandbox);
        if (!sandbox.Enabled)
        {
            return new AgentFileSearchOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    authorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        string normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new AgentFileSearchOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: "Search query is required.");
        }

        Directory.CreateDirectory(authorization.RootPath);

        int boundedLimit = limit <= 0 ? defaultSearchLimit : Math.Min(limit, maxSearchLimit);
        string normalizedSearchTerm = normalizedQuery.ToUpperInvariant();
        List<GatewayAgentFileListEntry> entries = [.. EnumerateSearchEntries(new DirectoryInfo(authorization.RootPath), authorization.RootPath)
            .Where(entry => IsSearchMatch(entry, normalizedSearchTerm))
            .OrderBy(entry => GetSearchRank(entry, normalizedSearchTerm))
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Take(boundedLimit)];

        return new AgentFileSearchOperationResult(
            AgentFileOperationStatus.Success,
            new GatewayAgentFileSearchResponse(authorization.Agent.Id, normalizedQuery, entries.ToArray()));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The opened stream is disposed by await using after ownership moves off the nullable local.")]
    public async Task<AgentFileUploadOperationResult> UploadFileAsync(
        string agentId,
        GatewayUser user,
        string? relativePath,
        AgentFileUploadRequest request,
        CancellationToken cancellationToken)
    {
        AgentFilePathAuthorizationResult authorization = pathAuthorizationService.Authorize(agentId, user, relativePath);
        if (authorization.Status is not AgentFileOperationStatus.Success || authorization.Agent is null)
        {
            return new AgentFileUploadOperationResult(authorization.Status, ErrorMessage: authorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(authorization.Agent.Sandbox);
        if (!sandbox.Enabled)
        {
            return new AgentFileUploadOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    authorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        if (request.SizeBytes <= 0 || request.SizeBytes > maxUploadBytes)
        {
            return new AgentFileUploadOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: $"File size must be between 1 byte and {maxUploadBytes} bytes.");
        }

        if (!Directory.Exists(authorization.ResolvedPath))
        {
            return new AgentFileUploadOperationResult(AgentFileOperationStatus.NotFound, ErrorMessage: "Folder not found.");
        }

        string safeFileName;
        try
        {
            safeFileName = WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName(request.FileName, reservedNames: reservedInternalEntryNames);
        }
        catch (InvalidOperationException ex)
        {
            return new AgentFileUploadOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: ex.Message);
        }

        string contentType;
        try
        {
            contentType = WorkspaceAttachmentUploadPolicy.NormalizeContentType(request.ContentType, safeFileName, enforceBlockedUploadPolicy: true);
        }
        catch (InvalidOperationException ex)
        {
            return new AgentFileUploadOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: ex.Message);
        }

        string fullPath = Path.Combine(authorization.ResolvedPath, safeFileName);
        if (File.Exists(fullPath) && !request.Overwrite)
        {
            return new AgentFileUploadOperationResult(AgentFileOperationStatus.Conflict, ErrorMessage: "A file with that name already exists in the selected folder.");
        }

        FileMode mode = request.Overwrite ? FileMode.Create : FileMode.CreateNew;
        FileStream? opened = null;
        try
        {
            try
            {
                opened = WorkspaceFile.OpenWrite(fullPath, mode);
            }
            catch (IOException)
            {
                return new AgentFileUploadOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: "Requested path is not accessible.");
            }

            FileStream stream = opened;
            opened = null;
            await using (stream.ConfigureAwait(false))
            {
                await request.Content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
            }
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new AgentFileUploadOperationResult(
            AgentFileOperationStatus.Success,
            new GatewayAgentFileUploadResponse(
                authorization.Agent.Id,
                string.IsNullOrWhiteSpace(authorization.RelativePath) ? safeFileName : $"{authorization.RelativePath}/{safeFileName}",
                safeFileName,
                contentType,
                request.SizeBytes));
    }

    public AgentFileDownloadOperationResult DownloadFile(string agentId, GatewayUser user, string relativePath)
    {
        AgentFilePathAuthorizationResult authorization = pathAuthorizationService.Authorize(agentId, user, relativePath);
        if (authorization.Status is not AgentFileOperationStatus.Success || authorization.Agent is null)
        {
            return new AgentFileDownloadOperationResult(authorization.Status, ErrorMessage: authorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(authorization.Agent.Sandbox);
        if (!sandbox.Enabled && !IsHostViewFile(authorization.RelativePath))
        {
            return new AgentFileDownloadOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    authorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        if (!File.Exists(authorization.ResolvedPath))
        {
            return new AgentFileDownloadOperationResult(AgentFileOperationStatus.NotFound, ErrorMessage: "File not found.");
        }

        string fileName = Path.GetFileName(authorization.ResolvedPath);
        FileStream? stream = null;
        try
        {
            stream = WorkspaceFile.OpenRead(authorization.ResolvedPath);
            AgentFileDownloadOperationResult result = new(
                AgentFileOperationStatus.Success,
                new AgentFileDownloadResult(stream, WorkspaceAttachmentUploadPolicy.InferContentType(fileName), fileName));
            stream = null;
            return result;
        }
        catch (IOException)
        {
            return new AgentFileDownloadOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: "Requested path is not accessible.");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static bool IsHostViewFile(string relativePath)
    {
        string[] segments = relativePath.Split('/');
        return segments.Length >= 2 && string.Equals(segments[0], "view-files", StringComparison.Ordinal);
    }

    public AgentFileDeleteOperationResult DeleteEntry(string agentId, GatewayUser user, string? relativePath)
    {
        AgentFilePathAuthorizationResult authorization = pathAuthorizationService.Authorize(agentId, user, relativePath);
        if (authorization.Status is not AgentFileOperationStatus.Success || authorization.Agent is null)
        {
            return new AgentFileDeleteOperationResult(authorization.Status, ErrorMessage: authorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(authorization.Agent.Sandbox);
        if (!sandbox.Enabled)
        {
            return new AgentFileDeleteOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    authorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        if (string.IsNullOrWhiteSpace(authorization.RelativePath))
        {
            return new AgentFileDeleteOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: "Workspace root cannot be deleted.");
        }

        if (File.Exists(authorization.ResolvedPath))
        {
            if (IsProtectedAttachmentPath(authorization.RelativePath)
                && conversationAssetStore.HasRetainedWorkspaceReference(agentId, authorization.RelativePath, user.Id))
            {
                return new AgentFileDeleteOperationResult(AgentFileOperationStatus.Conflict, ErrorMessage: referencedAttachmentDeleteBlockedMessage);
            }

            File.Delete(authorization.ResolvedPath);
            return new AgentFileDeleteOperationResult(AgentFileOperationStatus.Success);
        }

        if (Directory.Exists(authorization.ResolvedPath))
        {
            if (WorkspaceReparseGuard.ContainsFile(authorization.ResolvedPath))
            {
                return new AgentFileDeleteOperationResult(AgentFileOperationStatus.Conflict, ErrorMessage: "Folder cannot be deleted while it contains files.");
            }

            Directory.Delete(authorization.ResolvedPath, recursive: true);
            return new AgentFileDeleteOperationResult(AgentFileOperationStatus.Success);
        }

        return new AgentFileDeleteOperationResult(AgentFileOperationStatus.NotFound, ErrorMessage: "File or folder not found.");
    }

    public AgentFileCreateFolderOperationResult CreateFolder(string agentId, GatewayUser user, string? relativePath, string folderName)
    {
        AgentFilePathAuthorizationResult parentAuthorization = pathAuthorizationService.Authorize(agentId, user, relativePath);
        if (parentAuthorization.Status is not AgentFileOperationStatus.Success || parentAuthorization.Agent is null)
        {
            return new AgentFileCreateFolderOperationResult(parentAuthorization.Status, ErrorMessage: parentAuthorization.ErrorMessage);
        }

        GatewayAgentSandboxPayload sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(parentAuthorization.Agent.Sandbox);
        if (!sandbox.Enabled)
        {
            return new AgentFileCreateFolderOperationResult(
                AgentFileOperationStatus.SandboxDisabled,
                DisabledResponse: new GatewayAgentFilesDisabledResponse(
                    parentAuthorization.Agent.Id,
                    "sandbox_disabled",
                    "Agent file exchange is unavailable because the sandbox is disabled."));
        }

        Directory.CreateDirectory(parentAuthorization.RootPath);

        if (!Directory.Exists(parentAuthorization.ResolvedPath))
        {
            return new AgentFileCreateFolderOperationResult(AgentFileOperationStatus.NotFound, ErrorMessage: "Folder not found.");
        }

        string combinedRelativePath;
        try
        {
            combinedRelativePath = CombineRelativePath(parentAuthorization.RelativePath, folderName);
        }
        catch (InvalidOperationException ex)
        {
            return new AgentFileCreateFolderOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: ex.Message);
        }

        AgentFilePathAuthorizationResult targetAuthorization = pathAuthorizationService.Authorize(agentId, user, combinedRelativePath);
        if (targetAuthorization.Status is not AgentFileOperationStatus.Success || targetAuthorization.Agent is null)
        {
            return new AgentFileCreateFolderOperationResult(targetAuthorization.Status, ErrorMessage: targetAuthorization.ErrorMessage);
        }

        if (Directory.Exists(targetAuthorization.ResolvedPath) || File.Exists(targetAuthorization.ResolvedPath))
        {
            return new AgentFileCreateFolderOperationResult(AgentFileOperationStatus.Conflict, ErrorMessage: "A file system entry with that name already exists in the selected folder.");
        }

        try
        {
            Directory.CreateDirectory(targetAuthorization.ResolvedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AgentFileCreateFolderOperationResult(AgentFileOperationStatus.InvalidPath, ErrorMessage: folderCreationFailedMessage);
        }

        return new AgentFileCreateFolderOperationResult(
            AgentFileOperationStatus.Success,
            new GatewayAgentFileCreateFolderResponse(
                targetAuthorization.Agent.Id,
                targetAuthorization.RelativePath,
                Path.GetFileName(targetAuthorization.RelativePath)));
    }

    private static string CombineRelativePath(string currentRelativePath, string folderName)
    {
        string candidate = string.IsNullOrWhiteSpace(folderName) ? string.Empty : folderName.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("Folder name is required.");
        }

        string[] segments = candidate
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new InvalidOperationException("Folder name is required.");
        }

        string normalizedCandidate = string.Join('/', segments.Select(segment => WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName(segment, "Folder name", reservedInternalEntryNames)));

        return string.IsNullOrWhiteSpace(currentRelativePath)
            ? normalizedCandidate
            : $"{currentRelativePath}/{normalizedCandidate}";
    }

    private static bool IsSkippedWorkspaceEntry(FileSystemInfo entry)
        => string.Equals(entry.Name, ".kautuka", StringComparison.OrdinalIgnoreCase)
            || WorkspaceReparseGuard.HasReparsePoint(entry.FullName);

    private static bool IsProtectedAttachmentPath(string relativePath)
        => !string.IsNullOrWhiteSpace(relativePath)
            && (string.Equals(relativePath, "attachments", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<GatewayAgentFileListEntry> EnumerateSearchEntries(DirectoryInfo directory, string rootPath)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (IsSkippedWorkspaceEntry(entry))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(rootPath, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (entry is FileInfo fileInfo)
            {
                yield return new GatewayAgentFileListEntry(
                    fileInfo.Name,
                    relativePath,
                    "file",
                    fileInfo.Length,
                    new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero),
                    null);
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                yield return new GatewayAgentFileListEntry(
                    childDirectory.Name,
                    relativePath,
                    "directory",
                    null,
                    new DateTimeOffset(childDirectory.LastWriteTimeUtc, TimeSpan.Zero),
                    SummarizeDirectory(childDirectory).FileCount);

                foreach (GatewayAgentFileListEntry nested in EnumerateSearchEntries(childDirectory, rootPath))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsSearchMatch(GatewayAgentFileListEntry entry, string normalizedSearchTerm)
    {
        string normalizedName = entry.Name.ToUpperInvariant();
        string normalizedPath = entry.Path.ToUpperInvariant();
        return normalizedName.StartsWith(normalizedSearchTerm, StringComparison.Ordinal)
            || normalizedPath.StartsWith(normalizedSearchTerm, StringComparison.Ordinal)
            || normalizedName.Contains(normalizedSearchTerm, StringComparison.Ordinal)
            || normalizedPath.Contains(normalizedSearchTerm, StringComparison.Ordinal);
    }

    private static int GetSearchRank(GatewayAgentFileListEntry entry, string normalizedSearchTerm)
    {
        string normalizedName = entry.Name.ToUpperInvariant();
        string normalizedPath = entry.Path.ToUpperInvariant();

        if (normalizedName.StartsWith(normalizedSearchTerm, StringComparison.Ordinal))
        {
            return entry.EntryType == "file" ? 0 : 1;
        }

        if (normalizedPath.StartsWith(normalizedSearchTerm, StringComparison.Ordinal))
        {
            return entry.EntryType == "file" ? 2 : 3;
        }

        if (normalizedName.Contains(normalizedSearchTerm, StringComparison.Ordinal))
        {
            return entry.EntryType == "file" ? 4 : 5;
        }

        return entry.EntryType == "file" ? 6 : 7;
    }

    private static DirectoryAggregate SummarizeDirectory(DirectoryInfo directory)
    {
        int fileCount = 0;
        long totalSizeBytes = 0;

        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if (IsSkippedWorkspaceEntry(entry))
            {
                continue;
            }

            if (entry is FileInfo fileInfo)
            {
                fileCount++;
                totalSizeBytes += fileInfo.Length;
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DirectoryAggregate nested = SummarizeDirectory(childDirectory);
                fileCount += nested.FileCount;
                totalSizeBytes += nested.TotalSizeBytes;
            }
        }

        return new DirectoryAggregate(fileCount, totalSizeBytes);
    }
}
