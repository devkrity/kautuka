using Kautuka.Gateway.Domain.Assets;

namespace Kautuka.Gateway.Domain.AgentFiles;

internal sealed record AgentWorkspaceFileReference(
    string RelativePath,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ResolvedPath)
{
    public static AgentWorkspaceFileReference FromAuthorizedPath(string relativePath, string resolvedPath)
    {
        FileInfo fileInfo = new(resolvedPath);
        return new AgentWorkspaceFileReference(
            relativePath,
            fileInfo.Name,
            WorkspaceAttachmentUploadPolicy.InferContentType(fileInfo.Name),
            fileInfo.Length,
            resolvedPath);
    }
}
