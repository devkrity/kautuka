namespace Kautuka.Manager.RuntimeContainer.Sandbox.Models;

/// <summary>
/// Runtime context for a sandbox container managed by the configured container runtime.
/// </summary>
public sealed record ContainerContext(
    string ScopeType,
    string ScopeId,
    string? UserId,
    string HostWorkspacePath,
    string ContainerWorkspacePath,
    string Image,
    int DefaultCpuLimit,
    string DefaultMemoryLimit,
    int DefaultTimeoutSeconds,
    IReadOnlyList<string> BaseCapabilities,
    IReadOnlyDictionary<string, string> ContainerEnvironment)
{
    public long DesiredStateVersion { get; init; }

    public string SpecFingerprint { get; init; } = string.Empty;
}
