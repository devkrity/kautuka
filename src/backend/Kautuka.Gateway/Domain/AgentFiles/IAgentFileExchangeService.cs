namespace Kautuka.Gateway.Domain.AgentFiles;

internal interface IAgentFileExchangeService
{
    AgentFileListOperationResult ListDirectory(string agentId, GatewayUser user, string? relativePath);

    AgentFileSearchOperationResult SearchWorkspace(string agentId, GatewayUser user, string query, int limit);

    Task<AgentFileUploadOperationResult> UploadFileAsync(string agentId, GatewayUser user, string? relativePath, AgentFileUploadRequest request, CancellationToken cancellationToken);

    AgentFileDownloadOperationResult DownloadFile(string agentId, GatewayUser user, string relativePath);

    AgentFileDeleteOperationResult DeleteEntry(string agentId, GatewayUser user, string? relativePath);

    AgentFileCreateFolderOperationResult CreateFolder(string agentId, GatewayUser user, string? relativePath, string folderName);
}

internal sealed record AgentFileListOperationResult(
    AgentFileOperationStatus Status,
    GatewayAgentFileListResponse? Response = null,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);

internal sealed record AgentFileUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    bool Overwrite,
    Stream Content);

internal sealed record AgentFileSearchOperationResult(
    AgentFileOperationStatus Status,
    GatewayAgentFileSearchResponse? Response = null,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);

internal sealed record AgentFileUploadOperationResult(
    AgentFileOperationStatus Status,
    GatewayAgentFileUploadResponse? Response = null,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);

internal sealed record AgentFileDownloadOperationResult(
    AgentFileOperationStatus Status,
    AgentFileDownloadResult? Response = null,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);

internal sealed record AgentFileDownloadResult(
    Stream Content,
    string ContentType,
    string FileName);

internal sealed record AgentFileDeleteOperationResult(
    AgentFileOperationStatus Status,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);

internal sealed record AgentFileCreateFolderOperationResult(
    AgentFileOperationStatus Status,
    GatewayAgentFileCreateFolderResponse? Response = null,
    GatewayAgentFilesDisabledResponse? DisabledResponse = null,
    string? ErrorMessage = null);
