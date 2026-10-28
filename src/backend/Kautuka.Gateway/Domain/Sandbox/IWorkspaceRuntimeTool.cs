namespace Kautuka.Gateway.Domain.Sandbox;

internal interface IWorkspaceRuntimeTool
{
    WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null);

    WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null);

    Task<WorkspaceRuntimeContext> RebuildAgentWorkspaceAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
        => Task.FromResult(RebuildAgentWorkspace(agentId, userId));

    bool DeleteAgentWorkspace(string agentId);

    Task<bool> DeleteAgentWorkspaceAsync(string agentId, CancellationToken cancellationToken = default)
        => Task.FromResult(DeleteAgentWorkspace(agentId));

    Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default);

    Task<WorkspaceCommandResult> RunAgentShellAsync(
        string agentId,
        string command,
        string? userId = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceCommandResult> RunAgentShellWithEnvironmentAsync(
        string agentId,
        string command,
        IReadOnlyDictionary<string, string> environment,
        string? userId = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => environment.Count == 0
            ? RunAgentShellAsync(agentId, command, userId, timeoutSeconds, cancellationToken)
            : throw new NotSupportedException("This workspace runtime does not support opaque environment injection.");

    Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default);

    Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default);
}

internal sealed record WorkspaceRuntimeContext(
    string ScopeType,
    string ScopeId,
    string? UserId,
    string HostWorkspacePath,
    string ContainerWorkspacePath,
    string RuntimeHostWorkspacePath,
    string Image,
    int DefaultTimeoutSeconds,
    IReadOnlyList<string> BaseCapabilities,
    IReadOnlyDictionary<string, string> ContainerEnvironment)
{
    /// <summary>Returned when Kautuka.Manager is unreachable. Workspace dirs will be created on first use.</summary>
    internal static readonly WorkspaceRuntimeContext Unavailable = new(
        string.Empty, string.Empty, null,
        string.Empty, string.Empty, string.Empty,
        string.Empty,
        0,
        [],
        new Dictionary<string, string>());
}

internal sealed record WorkspaceSandboxSession(
    string ContainerName,
    bool Running,
    string ScopeType,
    string ScopeId,
    string? UserId);

internal sealed record WorkspaceCommandResult(
    string Status,
    int ExitCode,
    string StdOut,
    string StdErr,
    string ContainerName = "");
