namespace Kautuka.UpdateService.Models;

public sealed record UpdateStateRecord(
    int SchemaVersion,
    UpdateWorkflowState State,
    string EffectiveReleaseChannel,
    UpdateEligibilityState EligibilityState,
    UpdateEligibilityReason EligibilityReason,
    UpdateReleaseReference? CurrentRelease,
    UpdateReleaseReference? AvailableRelease,
    UpdateReleaseReference? StagedRelease,
    UpdatePreparedArtifacts? PreparedArtifacts,
    UpdateReleaseReference? LastKnownGoodRelease,
    DateTimeOffset? LastDiscoveryAttemptUtc,
    DateTimeOffset? LastDiscoverySuccessUtc,
    string? LastDiscoveryETag,
    string? LastError,
    DateTimeOffset? StateChangedAtUtc,
    string? SourceRepositoryOwner,
    string? SourceRepositoryName)
{
    public const int CurrentSchemaVersion = 3;

    public static UpdateStateRecord CreateDefault()
        => new(
            SchemaVersion: CurrentSchemaVersion,
            State: UpdateWorkflowState.Current,
            EffectiveReleaseChannel: "stable",
            EligibilityState: UpdateEligibilityState.None,
            EligibilityReason: UpdateEligibilityReason.None,
            CurrentRelease: null,
            AvailableRelease: null,
            StagedRelease: null,
            PreparedArtifacts: null,
            LastKnownGoodRelease: null,
            LastDiscoveryAttemptUtc: null,
            LastDiscoverySuccessUtc: null,
            LastDiscoveryETag: null,
            LastError: null,
            StateChangedAtUtc: null,
            SourceRepositoryOwner: null,
            SourceRepositoryName: null);
}
