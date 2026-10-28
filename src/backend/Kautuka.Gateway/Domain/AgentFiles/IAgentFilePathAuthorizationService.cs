namespace Kautuka.Gateway.Domain.AgentFiles;

internal interface IAgentFilePathAuthorizationService
{
    AgentFilePathAuthorizationResult Authorize(string agentId, GatewayUser user, string? relativePath);
}

internal enum AgentFileOperationStatus
{
    Success,
    Forbidden,
    InvalidPath,
    NotFound,
    SandboxDisabled,
    Conflict,
}

internal sealed record AgentFilePathAuthorizationResult(
    AgentFileOperationStatus Status,
    GatewayAgent? Agent,
    string RootPath,
    string ResolvedPath,
    string RelativePath,
    string? ErrorMessage = null);
