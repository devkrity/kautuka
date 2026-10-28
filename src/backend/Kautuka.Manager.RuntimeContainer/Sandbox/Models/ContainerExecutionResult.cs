namespace Kautuka.Manager.RuntimeContainer.Sandbox.Models;

/// <summary>
/// Result of a command execution inside a Podman-managed container.
/// </summary>
public sealed record ContainerExecutionResult(
    string Status,
    int ExitCode,
    string StdOut,
    string StdErr,
    string? ContainerName = null);

