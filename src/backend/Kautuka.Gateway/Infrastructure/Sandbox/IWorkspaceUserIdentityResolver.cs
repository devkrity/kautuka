namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal interface IWorkspaceUserIdentityResolver
{
    string? ResolveWorkspaceUserKey(string? userId);
    string? ResolveAgentWorkspaceUserKey(string agentId);
}
