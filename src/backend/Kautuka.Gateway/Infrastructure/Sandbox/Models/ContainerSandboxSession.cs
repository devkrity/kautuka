namespace Kautuka.Gateway.Infrastructure.Sandbox.Models;

internal sealed record ContainerSandboxSession(
    string ContainerName,
    bool Running,
    string ScopeType,
    string ScopeId,
    string? UserId);
