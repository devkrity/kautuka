using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Sandbox;

/// <summary>
/// Manages agent sandbox lifecycle within the container runtime.
/// </summary>
public interface IContainerTool
{
    /// <summary>Creates the host workspace directory for the agent.</summary>
    Task PrimeWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default);

    /// <summary>Recreates the host workspace directory (deletes and recreates).</summary>
    Task<ContainerContext> RebuildWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default);

    /// <summary>Deletes the host workspace directory. Returns false if it did not exist.</summary>
    Task<bool> DeleteWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default);

    /// <summary>Ensures the sandbox container is running, creating or starting it if needed.</summary>
    Task<OciSandboxSession> SpawnSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default);

    /// <summary>Returns drift replacements recorded since the previous call.</summary>
    IReadOnlyList<string> TakeDriftNotes() => [];

    /// <summary>Runs a shell command inside the running container via podman exec.</summary>
    Task<ContainerExecutionResult> RunShellAsync(
        ContainerContext context,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);

    Task<ContainerExecutionResult> RunShellWithEnvironmentAsync(
        ContainerContext context,
        string command,
        IReadOnlyDictionary<string, string> environment,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Count == 0
            ? RunShellAsync(context, command, timeoutSeconds, cancellationToken)
            : throw new NotSupportedException("This container runtime does not support opaque environment injection.");
    }

    /// <summary>Inspects the current state of the container.</summary>
    Task<OciSandboxSession> InspectSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default);

    /// <summary>Stops and deletes the container.</summary>
    Task CloseSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default);
}
