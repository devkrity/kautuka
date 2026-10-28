namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Performs a bounded, side-effect-free liveness check against the container runtime.
/// Callers read last-known availability; a Manager background loop refreshes the sample.
/// </summary>
public interface IManagerRuntimeHealthProbe
{
    /// <summary>
    /// Returns the last completed sample. Waits only when no sample exists yet (startup).
    /// Does not wait for an in-flight refresh when a sample is already available.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs (or joins) a live probe and stores the result as last-known.
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken);
}
