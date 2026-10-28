using Grpc.Core;

using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed class GrpcWorkspaceRuntimeTool(
    IManagerGrpcService workspaceGrpcService,
    WorkspaceRuntimeContextResolver contextResolver) : IWorkspaceRuntimeTool
{
    private readonly IManagerGrpcService workspaceGrpcService = workspaceGrpcService;
    private readonly WorkspaceRuntimeContextResolver contextResolver = contextResolver;

    public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
        => this.contextResolver.ResolveAgentWorkspace(agentId, userId);

    public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null)
        => RebuildAgentWorkspaceAsync(agentId, userId).GetAwaiter().GetResult();

    public async Task<WorkspaceRuntimeContext> RebuildAgentWorkspaceAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
    {
        WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId, userId);

        try
        {
            WorkspaceContextResponse response = await this.workspaceGrpcService
                .RebuildWorkspaceAsync(ToResolvedRequest(resolvedContext), cancellationToken)
                .ConfigureAwait(false);
            return FromContextResponse(response);
        }
        catch (RpcException)
        {
            // Workspace rebuild should not block agent persistence during setup. If workspace
            // services are temporarily unavailable or return an error, defer rebuild to first use.
            return resolvedContext;
        }
        catch (InvalidOperationException)
        {
            // Workspace rebuild should not block agent persistence during setup. If workspace
            // services are temporarily unavailable or return an error, defer rebuild to first use.
            return resolvedContext;
        }
    }

    public bool DeleteAgentWorkspace(string agentId)
        => DeleteAgentWorkspaceAsync(agentId).GetAwaiter().GetResult();

    public async Task<bool> DeleteAgentWorkspaceAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId);
            WorkspaceDeleteResponse response = await this.workspaceGrpcService
                .DeleteWorkspaceAsync(ToResolvedRequest(resolvedContext), cancellationToken)
                .ConfigureAwait(false);
            return response.Deleted;
        }
        catch (RpcException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    public async Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
    {
        WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId, userId);
        WorkspaceSandboxResponse response = await this.workspaceGrpcService.SpawnSandboxAsync(ToResolvedRequest(resolvedContext), cancellationToken).ConfigureAwait(false);

        return FromSandboxResponse(response);
    }

    public async Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
    {
        WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId, userId);
        WorkspaceSandboxResponse response = await this.workspaceGrpcService.InspectSandboxAsync(ToResolvedRequest(resolvedContext), cancellationToken).ConfigureAwait(false);

        return FromSandboxResponse(response);
    }

    public async Task<WorkspaceCommandResult> RunAgentShellAsync(
        string agentId,
        string command,
        string? userId = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => await RunAgentShellWithEnvironmentAsync(
            agentId,
            command,
            new Dictionary<string, string>(StringComparer.Ordinal),
            userId,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceCommandResult> RunAgentShellWithEnvironmentAsync(
        string agentId,
        string command,
        IReadOnlyDictionary<string, string> environment,
        string? userId = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId, userId);
        var request = new WorkspaceExecRequest
        {
            Context = ToMessage(resolvedContext),
            Command = command,
            TimeoutSeconds = timeoutSeconds ?? 0
        };
        request.Environment.Add(new Dictionary<string, string>(environment, StringComparer.Ordinal));

        WorkspaceExecutionResponse response = await this.workspaceGrpcService.ExecAsync(request, cancellationToken).ConfigureAwait(false);

        return FromExecutionResponse(response);
    }

    public async Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
    {
        WorkspaceRuntimeContext resolvedContext = this.contextResolver.ResolveAgentWorkspace(agentId, userId);
        WorkspaceBoolResponse response = await this.workspaceGrpcService.StopSandboxAsync(ToResolvedRequest(resolvedContext), cancellationToken).ConfigureAwait(false);

        EnsureOk(response.Ok, response.Message, "CloseAgentSandbox");
    }

    private static WorkspaceResolvedRequest ToResolvedRequest(WorkspaceRuntimeContext workspace)
        => new()
        {
            Context = ToMessage(workspace)
        };

    private static WorkspaceContextMessage ToMessage(WorkspaceRuntimeContext workspace)
    {
        var message = new WorkspaceContextMessage
        {
            ScopeType = workspace.ScopeType,
            ScopeId = workspace.ScopeId,
            UserId = workspace.UserId ?? string.Empty,
            HostWorkspacePath = workspace.HostWorkspacePath,
            ContainerWorkspacePath = workspace.ContainerWorkspacePath,
            Image = workspace.Image,
            DefaultTimeoutSeconds = workspace.DefaultTimeoutSeconds,
        };

        message.BaseCapabilities.AddRange(workspace.BaseCapabilities);
        message.ContainerEnvironment.AddRange(workspace.ContainerEnvironment.Select(static pair => new WorkspaceKvPair
        {
            Key = pair.Key,
            Value = pair.Value,
        }));

        return message;
    }

    private static WorkspaceRuntimeContext FromContextResponse(WorkspaceContextResponse response)
    {
        EnsureOk(response.Ok, response.Message, "WorkspaceContext");
        WorkspaceContextMessage payload = response.Context ?? throw new InvalidOperationException("Workspace context payload is missing.");
        return new WorkspaceRuntimeContext(
            payload.ScopeType,
            payload.ScopeId,
            payload.UserId,
            payload.HostWorkspacePath,
            payload.ContainerWorkspacePath,
            string.Empty,
            payload.Image,
            payload.DefaultTimeoutSeconds,
            payload.BaseCapabilities,
            payload.ContainerEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static WorkspaceSandboxSession FromSandboxResponse(WorkspaceSandboxResponse response)
    {
        EnsureOk(response.Ok, response.Message, "WorkspaceSandbox");
        WorkspaceSandboxMessage payload = response.Session ?? throw new InvalidOperationException("Workspace sandbox payload is missing.");
        return FromSession(payload);
    }

    private static WorkspaceSandboxSession FromSession(WorkspaceSandboxMessage payload)
        => new(payload.ContainerName, payload.Running, payload.ScopeType, payload.ScopeId, payload.UserId);

    private static WorkspaceCommandResult FromExecutionResponse(WorkspaceExecutionResponse response)
    {
        EnsureOk(response.Ok, response.Message, "WorkspaceExecution");
        WorkspaceExecutionMessage payload = response.Result ?? throw new InvalidOperationException("Workspace execution payload is missing.");
        return new WorkspaceCommandResult(payload.Status, payload.ExitCode, payload.StdOut, payload.StdErr, payload.ContainerName);
    }

    private static void EnsureOk(bool ok, string message, string operation)
    {
        if (ok)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} failed: {message}");
    }
}
