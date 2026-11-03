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
    string? RecoverySnapshot = null);
