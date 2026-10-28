namespace Kautuka.Manager.RuntimeContainer.Sandbox.Models;

/// <summary>
/// Represents a running sandbox container session managed by Podman.
/// </summary>
public sealed record OciSandboxSession(
    string ContainerName,
    bool Running,
    string ScopeType,
    string ScopeId,
    string? UserId);

