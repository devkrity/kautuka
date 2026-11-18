using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

public interface IWorkloadStartupState
{
    bool IsStartupGateOpen { get; }
}

/// <summary>Coordinates workload launch leases with global topology incident closure.</summary>
public interface IWorkloadLaunchAdmission
{
    ValueTask<IWorkloadLaunchLease> AcquireLaunchLeaseAsync(CancellationToken cancellationToken);

    void CloseLaunchAdmission();
}

public interface IWorkloadLaunchLease : IAsyncDisposable
{
    CancellationToken CancellationToken { get; }
}

/// <summary>
/// Shared Manager launch guard for the global topology gate and persisted NAC brakes.
/// </summary>
public sealed class WorkloadLaunchGate(
    IWorkloadStartupState topologyManager,
    NetworkAccessPolicyStore policyStore)
{
    private readonly IWorkloadLaunchAdmission? launchAdmission = topologyManager as IWorkloadLaunchAdmission;

    public void EnsureLaunchAllowed(string? userId)
    {
        if (!topologyManager.IsStartupGateOpen)
        {
            throw new InvalidOperationException("Workload launch rejected: NAC startup gate is closed.");
        }

        BrakeState brake = policyStore.GetBrakeSnapshot();
        if (brake.System)
        {
            throw new InvalidOperationException("Workload launch rejected: the system network brake is active.");
        }

        if (brake.IsUserBraked(userId))
        {
            throw new InvalidOperationException($"Workload launch rejected: the user network brake is active for '{userId}'.");
        }
    }

    public bool IsBraked(string? userId)
    {
        BrakeState brake = policyStore.GetBrakeSnapshot();
        return brake.System || brake.IsUserBraked(userId);
    }

    public async ValueTask<IWorkloadLaunchLease> AcquireLaunchLeaseAsync(string? userId, CancellationToken cancellationToken)
    {
        IWorkloadLaunchLease lease = launchAdmission is null
            ? NoopLaunchLease.Instance
            : await launchAdmission.AcquireLaunchLeaseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureLaunchAllowed(userId);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class NoopLaunchLease : IWorkloadLaunchLease
    {
        public static NoopLaunchLease Instance { get; } = new();

        public CancellationToken CancellationToken => CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
