namespace Kautuka.UpdateService.Models;

public sealed record UpdateServiceContext(
    string RuntimeRoot,
    string InstallRoot,
    string ManagerReleaseId,
    string ManagerVersion,
    bool IsReady,
    int ActiveExecCount,
    bool HostStatusObserved = true,
    bool RuntimeUnitsActive = true,
    bool CurrentPointersConsistent = true,
    bool UpdateServiceRunningCurrent = true,
    bool RecoveryRequired = false,
    string? RecoveryOperationId = null,
    string? RecoveryPhase = null,
    string? RecoverySourceRelease = null,
    string? RecoveryTargetRelease = null,
    string? RecoverySnapshot = null,
    bool? ApplianceBootConfirmed = null,
    long ApplianceRecoveryUpdatedUnixMilliseconds = 0,
    string? ApplianceSnapshotPruneStatus = null,
    string? PreflightRequestId = null,
    string? PreflightSourceVersion = null,
    string? PreflightTargetVersion = null,
    string? PreflightCode = null)
{
    internal bool RuntimeConverged => ApplianceBootConfirmed
        ?? (RuntimeUnitsActive && CurrentPointersConsistent && UpdateServiceRunningCurrent);
}
