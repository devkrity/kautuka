namespace Kautuka.NetworkAccess.Acceptance;

/// <summary>
/// Prevents cleanup against the caller's default Podman store unless the runner
/// has verified that Podman honored its private graphroot and runroot.
/// </summary>
internal sealed class PodmanIsolationCleanupGate
{
    public bool IsVerified { get; private set; }

    public void MarkVerified() => IsVerified = true;

    public Task RunIfVerifiedAsync(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return IsVerified ? cleanup() : Task.CompletedTask;
    }
}
