namespace Kautuka.UpdateService.Models;

public sealed record UpdateStatusSnapshot(
    UpdateWorkflowState State,
    string EffectiveReleaseChannel,
    UpdateEligibilityState EligibilityState,
    UpdateEligibilityReason EligibilityReason,
    UpdateReleaseReference? CurrentRelease,
    UpdateReleaseReference? AvailableRelease,
    UpdateReleaseReference? LastKnownGoodRelease,
    DateTimeOffset? LastDiscoveryAttemptUtc,
    DateTimeOffset? LastDiscoverySuccessUtc,
    string ManagerVersion,
    bool HostReady,
    int ActiveExecCount,
    string? LastError);
