using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public interface IManagerRuntimeAdapter
{
    void ApplyRuntimeSettings(ManagerRuntimeSettings runtimeSettings);

    /// <summary>
    /// Validates that the warmed container-runtime sample is available and the workspace base root is accessible.
    /// Returns true if the runtime is ready to accept container operations.
    /// </summary>
    Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken);

    /// <summary>
    /// Validates that the configured sandbox image is available for container creation.
    /// Returns null if available, or an error string if not.
    /// </summary>
    Task<string?> PrepareSandboxImageAsync(SandboxRuntimeDefinition sandbox, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the sandbox image containers should run. A published kautuka-base
    /// digest replaces the local tag.
    /// </summary>
    string ResolveSandboxImage(string configuredImage);

    Task<string[]> GetManagedContainerNamesAsync(IReadOnlyCollection<string> trackedContainers, CancellationToken cancellationToken);

    Task StopContainersAsync(IReadOnlyCollection<string> containerNames, CancellationToken cancellationToken);

    Task<ManagedContainerReconcileResult> RemoveOrphanedManagedContainersAsync(
        string role,
        IReadOnlyCollection<string> desiredContainerNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a Podman network exists, creating it if absent.
    /// </summary>
    Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken);
}

public sealed record ManagedContainerReconcileResult(bool Succeeded, int RemovedCount, string? FailureClass);
