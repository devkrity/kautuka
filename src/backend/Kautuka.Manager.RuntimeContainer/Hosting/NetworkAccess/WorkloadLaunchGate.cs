using Kautuka.NetworkAccessController;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

public interface IWorkloadStartupState
{
    bool IsStartupGateOpen { get; }
}

/// <summary>
/// Shared Manager launch guard for the global topology gate and persisted NAC brakes.
/// </summary>
public sealed class WorkloadLaunchGate(
    IWorkloadStartupState topologyManager,
    NetworkAccessPolicyStore policyStore)
{
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
}
