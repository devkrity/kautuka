using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Kautuka.Telemetry.Security;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class UpdateCoordinator(
    IUpdateStateStore stateStore,
    IUpdateRuntimePolicyStore runtimePolicyStore,
    IReleaseDiscoveryClient discoveryClient,
    IHttpClientFactory httpClientFactory,
    IUpdateServiceIntegration hostIntegration,
    UpdateOperationHistoryStore operationStore,
    IOptions<UpdateOptions> options,
    ILogger<UpdateCoordinator> logger) : IUpdateCoordinator
{
    private readonly IUpdateStateStore stateStore = stateStore;
    private readonly IUpdateRuntimePolicyStore runtimePolicyStore = runtimePolicyStore;
    private readonly IReleaseDiscoveryClient discoveryClient = discoveryClient;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly IUpdateServiceIntegration hostIntegration = hostIntegration;
    private readonly UpdateOperationHistoryStore operationStore = operationStore;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<UpdateCoordinator> logger = logger;
    private readonly SemaphoreSlim discoveryGate = new(1, 1);

    public async ValueTask<UpdateStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        UpdateRuntimePolicy policy = await runtimePolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        (UpdateStateRecord state, UpdateServiceContext hostContext) = await GetResolvedStateAsync(policy.ReleaseChannel, cancellationToken).ConfigureAwait(false);
        return CreateSnapshot(state, hostContext);
    }

    public async ValueTask<UpdateStatusSnapshot> ApplyUpdatePolicyAsync(string releaseChannel, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            await runtimePolicyStore.SetReleaseChannelAsync(releaseChannel, cancellationToken).ConfigureAwait(false);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateRuntimePolicy appliedPolicy = await runtimePolicyStore.SetReleaseChannelAsync(releaseChannel, cancellationToken).ConfigureAwait(false);
            return await RefreshDiscoveryCoreAsync(appliedPolicy, forceReevaluation: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    public async Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (!await discoveryGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Skipping update discovery because another discovery attempt is already in flight.");
            return;
        }

        try
        {
            UpdateRuntimePolicy policy = await runtimePolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            _ = await RefreshDiscoveryCoreAsync(policy, forceReevaluation: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    public async ValueTask<UpdateStatusSnapshot> ApplyPreparedReleaseAsync(CancellationToken cancellationToken = default)
    {
        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateRuntimePolicy policy = await runtimePolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            (UpdateStateRecord state, UpdateServiceContext hostContext) = await GetResolvedStateAsync(policy.ReleaseChannel, cancellationToken).ConfigureAwait(false);
            if (state.State == UpdateWorkflowState.Applying)
            {
                return CreateSnapshot(state, hostContext);
            }

            if (!hostContext.HostStatusObserved)
            {
                UpdateStateRecord unavailable = state with
                {
                    LastError = "Update apply is blocked because Manager host status is unavailable.",
                };

                await stateStore.SaveAsync(unavailable, cancellationToken).ConfigureAwait(false);
                return CreateSnapshot(unavailable, hostContext);
            }

            if (hostContext.ActiveExecCount > 0)
            {
                UpdateStateRecord blocked = state with
                {
                    LastError = "Update apply is blocked while active executions are still running.",
                };

                await stateStore.SaveAsync(blocked, cancellationToken).ConfigureAwait(false);
                return CreateSnapshot(blocked, hostContext);
            }

            UpdateReleaseReference? targetRelease = state.StagedRelease ?? state.AvailableRelease;
            if (targetRelease is null)
            {
                UpdateStateRecord unchanged = state with
                {
                    LastError = "No update is ready to apply.",
                };

                await stateStore.SaveAsync(unchanged, cancellationToken).ConfigureAwait(false);
                return CreateSnapshot(unchanged, hostContext);
            }

            UpdatePreparedArtifacts preparedArtifacts;

            try
            {
                preparedArtifacts = state.PreparedArtifacts is not null
                    && state.StagedRelease is not null
                    && string.Equals(state.StagedRelease.ReleaseId, targetRelease.ReleaseId, StringComparison.Ordinal)
                    ? state.PreparedArtifacts
                    : await PrepareArtifactsAsync(targetRelease, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to prepare update release {ReleaseId} during apply.", targetRelease.ReleaseId);

                UpdateStateRecord failedState = state with
                {
                    AvailableRelease = targetRelease,
                    StagedRelease = null,
                    PreparedArtifacts = null,
                    State = UpdateWorkflowState.Available,
                    StateChangedAtUtc = DateTimeOffset.UtcNow,
                    LastError = ex.Message,
                };

                await stateStore.SaveAsync(failedState, CancellationToken.None).ConfigureAwait(false);
                return CreateSnapshot(failedState, hostContext);
            }

            UpdateStateRecord nextState = state with
            {
                AvailableRelease = state.AvailableRelease is not null
                    && string.Equals(state.AvailableRelease.ReleaseId, targetRelease.ReleaseId, StringComparison.Ordinal)
                    ? state.AvailableRelease
                    : null,
                StagedRelease = targetRelease,
                PreparedArtifacts = preparedArtifacts,
                LastKnownGoodRelease = state.CurrentRelease ?? state.LastKnownGoodRelease,
                State = UpdateWorkflowState.Applying,
                StateChangedAtUtc = DateTimeOffset.UtcNow,
                LastError = null,
            };

            await stateStore.SaveAsync(nextState, CancellationToken.None).ConfigureAwait(false);

            try
            {
                await hostIntegration.ActivatePreparedUpdateAsync(preparedArtifacts, CancellationToken.None).ConfigureAwait(false);
                UpdateServiceContext freshHost = await hostIntegration.GetHostContextAsync(CancellationToken.None).ConfigureAwait(false);
                if (TryFinalizeAppliedRelease(nextState, freshHost, out UpdateStateRecord? finalizedState))
                {
                    await stateStore.SaveAsync(finalizedState, CancellationToken.None).ConfigureAwait(false);
                    await operationStore.AppendAsync(new UpdateOperationRecord(
                        OperationType: "apply",
                        CompletedAtUtc: DateTimeOffset.UtcNow,
                        FromVersion: state.CurrentRelease?.Version ?? state.LastKnownGoodRelease?.Version,
                        ToVersion: preparedArtifacts.TargetReleaseId,
                        Succeeded: true,
                        ErrorMessage: null), CancellationToken.None).ConfigureAwait(false);
                    return CreateSnapshot(finalizedState, freshHost);
                }

                // Helper succeeded; leave Applying so the next status read can finalize once units settle.
                return CreateSnapshot(nextState, freshHost);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to activate update release {ReleaseId}.", targetRelease.ReleaseId);

                UpdateStateRecord failedState = state with
                {
                    AvailableRelease = targetRelease,
                    StagedRelease = null,
                    PreparedArtifacts = null,
                    LastKnownGoodRelease = state.CurrentRelease ?? state.LastKnownGoodRelease,
                    State = UpdateWorkflowState.Available,
                    StateChangedAtUtc = DateTimeOffset.UtcNow,
                    LastError = ex.Message,
                };

                await stateStore.SaveAsync(failedState, CancellationToken.None).ConfigureAwait(false);
                return CreateSnapshot(failedState, hostContext);
            }
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    public async ValueTask<UpdateStatusSnapshot> RevertToLastKnownGoodAsync(CancellationToken cancellationToken = default)
    {
        await discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateRuntimePolicy policy = await runtimePolicyStore.GetAsync(cancellationToken).ConfigureAwait(false);
            (UpdateStateRecord state, UpdateServiceContext hostContext) = await GetResolvedStateAsync(policy.ReleaseChannel, cancellationToken).ConfigureAwait(false);
            if (state.State == UpdateWorkflowState.Applying)
            {
                // GetResolvedStateAsync already attempted finalize/recovery. Do not report a
                // successful revert when the helper was never invoked (live: apply left
                // Applying, revert returned ok with pointers still on the new release).
                UpdateStateRecord busy = state with
                {
                    LastError = string.IsNullOrWhiteSpace(state.LastError)
                        ? "An update is still applying."
                        : state.LastError,
                };
                await stateStore.SaveAsync(busy, cancellationToken).ConfigureAwait(false);
                return CreateSnapshot(busy, hostContext);
            }

            if (state.LastKnownGoodRelease is null)
            {
                UpdateStateRecord unchanged = state with
                {
                    LastError = "No rollback target is available.",
                };

                await stateStore.SaveAsync(unchanged, cancellationToken).ConfigureAwait(false);
                return CreateSnapshot(unchanged, hostContext);
            }

            UpdateStateRecord nextState = state with
            {
                StagedRelease = state.LastKnownGoodRelease,
                LastKnownGoodRelease = state.CurrentRelease ?? state.LastKnownGoodRelease,
                State = UpdateWorkflowState.Applying,
                StateChangedAtUtc = DateTimeOffset.UtcNow,
                LastError = null,
            };

            await stateStore.SaveAsync(nextState, CancellationToken.None).ConfigureAwait(false);

            try
            {
                await hostIntegration.ActivateRollbackAsync(state.LastKnownGoodRelease, CancellationToken.None).ConfigureAwait(false);
                UpdateServiceContext freshHost = await hostIntegration.GetHostContextAsync(CancellationToken.None).ConfigureAwait(false);
                if (TryFinalizeAppliedRelease(nextState, freshHost, out UpdateStateRecord? finalizedState))
                {
                    await stateStore.SaveAsync(finalizedState, CancellationToken.None).ConfigureAwait(false);
                    await operationStore.AppendAsync(new UpdateOperationRecord(
                        OperationType: "rollback",
                        CompletedAtUtc: DateTimeOffset.UtcNow,
                        FromVersion: state.CurrentRelease?.Version,
                        ToVersion: state.LastKnownGoodRelease.Version,
                        Succeeded: true,
                        ErrorMessage: null), CancellationToken.None).ConfigureAwait(false);
                    return CreateSnapshot(finalizedState, freshHost);
                }

                await operationStore.AppendAsync(new UpdateOperationRecord(
                    OperationType: "rollback",
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    FromVersion: state.CurrentRelease?.Version,
                    ToVersion: state.LastKnownGoodRelease.Version,
                    Succeeded: true,
                    ErrorMessage: null), CancellationToken.None).ConfigureAwait(false);
                return CreateSnapshot(nextState, freshHost);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to activate rollback release {ReleaseId}.", state.LastKnownGoodRelease.ReleaseId);

                UpdateStateRecord failedState = state with
                {
                    StagedRelease = null,
                    PreparedArtifacts = null,
                    State = UpdateWorkflowState.Current,
                    StateChangedAtUtc = DateTimeOffset.UtcNow,
                    LastError = ex.Message,
                };

                await stateStore.SaveAsync(failedState, CancellationToken.None).ConfigureAwait(false);
                await operationStore.AppendAsync(new UpdateOperationRecord(
                    OperationType: "rollback",
                    CompletedAtUtc: DateTimeOffset.UtcNow,
                    FromVersion: state.CurrentRelease?.Version,
                    ToVersion: state.LastKnownGoodRelease.Version,
                    Succeeded: false,
                    ErrorMessage: ex.Message), CancellationToken.None).ConfigureAwait(false);
                return CreateSnapshot(failedState, hostContext);
            }
        }
        finally
        {
            discoveryGate.Release();
        }
    }

    public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken = default)
        => hostIntegration.RequestSupervisorReconcileAsync(cancellationToken);

    private async Task<UpdateStatusSnapshot> RefreshDiscoveryCoreAsync(UpdateRuntimePolicy policy, bool forceReevaluation, CancellationToken cancellationToken)
    {
        (UpdateStateRecord state, UpdateServiceContext hostContext) = await GetResolvedStateAsync(policy.ReleaseChannel, cancellationToken).ConfigureAwait(false);
        state = state with
        {
            EffectiveReleaseChannel = policy.ReleaseChannel,
            LastDiscoveryAttemptUtc = DateTimeOffset.UtcNow,
            SourceRepositoryOwner = options.RepositoryOwner,
            SourceRepositoryName = options.RepositoryName,
        };

        if (!IsDiscoveryConfigured(options))
        {
            UpdateStateRecord notConfiguredState = state with
            {
                LastError = "Update service is not fully configured.",
                AvailableRelease = null,
                EligibilityState = UpdateEligibilityState.None,
                EligibilityReason = UpdateEligibilityReason.None,
            };

            await stateStore.SaveAsync(notConfiguredState, cancellationToken).ConfigureAwait(false);
            return CreateSnapshot(notConfiguredState, hostContext);
        }

        bool policyChanged = forceReevaluation
            || !string.Equals(state.EffectiveReleaseChannel, policy.ReleaseChannel, StringComparison.OrdinalIgnoreCase);

        ReleaseDiscoveryResult result;
        try
        {
            result = await discoveryClient.DiscoverAsync(
                new ReleaseDiscoveryRequest(
                    options.RepositoryOwner ?? string.Empty,
                    options.RepositoryName ?? string.Empty,
                    policy.ReleaseChannel,
                    policyChanged ? null : state.LastDiscoveryETag),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Manager update discovery request failed.");
            UpdateStateRecord failedState = state with
            {
                LastError = ex.Message,
                AvailableRelease = null,
                EligibilityState = UpdateEligibilityState.None,
                EligibilityReason = UpdateEligibilityReason.None,
            };
            await stateStore.SaveAsync(failedState, cancellationToken).ConfigureAwait(false);
            return CreateSnapshot(failedState, hostContext);
        }

        UpdateReleaseReference? availableRelease = result.NotModified && !policyChanged
            ? state.AvailableRelease
            : result.AvailableRelease;

        (availableRelease, UpdateEligibilityState eligibilityState, UpdateEligibilityReason eligibilityReason) =
            ResolveEligibility(
                state.CurrentRelease,
                availableRelease,
                policy.ReleaseChannel,
                hostContext.ManagerVersion);

        // Apply enforces the trust policy on the release's URLs; discovery did not, so a
        // release it can never install was still offered. Block it here with the rejection as
        // the error, which keeps it visible without presenting an apply that is certain to
        // fail. Only reached when the release is otherwise eligible, so an already-current
        // appliance still reports AlreadyCurrent rather than a URL complaint.
        string? untrustedSourceUrl = null;
        if (eligibilityState is UpdateEligibilityState.Available
            && availableRelease is not null
            && ReleaseTrustPolicy.TryFindUnapprovedSourceUrl(availableRelease, options, out untrustedSourceUrl))
        {
            logger.LogWarning(
                "Discovered release {ReleaseId} cannot be applied: {Rejection}",
                availableRelease.ReleaseId,
                untrustedSourceUrl);

            availableRelease = null;
            eligibilityState = UpdateEligibilityState.Blocked;
            eligibilityReason = UpdateEligibilityReason.UntrustedSourceUrl;
        }

        UpdateReleaseReference? stagedRelease = state.StagedRelease;
        UpdatePreparedArtifacts? preparedArtifacts = state.PreparedArtifacts;
        if (ShouldInvalidateStagedRelease(stagedRelease, availableRelease, state.CurrentRelease))
        {
            stagedRelease = null;
            preparedArtifacts = null;
        }

        UpdateStateRecord nextState = state with
        {
            EffectiveReleaseChannel = policy.ReleaseChannel,
            LastDiscoveryETag = result.ETag ?? state.LastDiscoveryETag,
            LastDiscoverySuccessUtc = result.FailureReason is null ? DateTimeOffset.UtcNow : state.LastDiscoverySuccessUtc,
            AvailableRelease = result.NotModified && !policyChanged ? availableRelease : availableRelease,
            StagedRelease = stagedRelease,
            PreparedArtifacts = preparedArtifacts,
            LastError = result.FailureReason ?? untrustedSourceUrl,
            EligibilityState = eligibilityState,
            EligibilityReason = eligibilityReason,
            State = ResolveWorkflowState(state, result, availableRelease),
        };

        await stateStore.SaveAsync(nextState, cancellationToken).ConfigureAwait(false);
        return CreateSnapshot(nextState, hostContext);
    }

    private static bool IsDiscoveryConfigured(UpdateOptions options)
    {
        bool hasManifestSource = !string.IsNullOrWhiteSpace(options.ReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.PreviewReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.StableReleaseManifestUrl);

        return hasManifestSource
            || (!string.IsNullOrWhiteSpace(options.RepositoryOwner)
                && !string.IsNullOrWhiteSpace(options.RepositoryName));
    }

    private async ValueTask<(UpdateStateRecord State, UpdateServiceContext HostContext)> GetResolvedStateAsync(string effectiveReleaseChannel, CancellationToken cancellationToken)
    {
        UpdateStateRecord persistedState = await stateStore.GetAsync(cancellationToken).ConfigureAwait(false);
        UpdateServiceContext hostContext = await hostIntegration.GetHostContextAsync(cancellationToken).ConfigureAwait(false);
        UpdateReleaseReference currentRelease = ResolveCurrentRelease(persistedState, hostContext, effectiveReleaseChannel);
        UpdateStateRecord state = persistedState with
        {
            SchemaVersion = UpdateStateRecord.CurrentSchemaVersion,
            EffectiveReleaseChannel = string.IsNullOrWhiteSpace(persistedState.EffectiveReleaseChannel)
                ? effectiveReleaseChannel
                : persistedState.EffectiveReleaseChannel,
            CurrentRelease = currentRelease,
        };
        bool shouldPersistState = persistedState.SchemaVersion != UpdateStateRecord.CurrentSchemaVersion;

        if (TryNormalizeStaleDiscoveryError(state, out UpdateStateRecord discoveryNormalizedState))
        {
            state = discoveryNormalizedState;
            shouldPersistState = true;
        }

        if (TryNormalizeInactiveStagedState(state, currentRelease, out UpdateStateRecord inactiveStagingNormalizedState))
        {
            state = inactiveStagingNormalizedState;
            shouldPersistState = true;
        }

        if (TryFinalizeAppliedRelease(state, hostContext, out UpdateStateRecord? finalizedState))
        {
            await stateStore.SaveAsync(finalizedState, cancellationToken).ConfigureAwait(false);
            await operationStore.AppendAsync(new UpdateOperationRecord(
                OperationType: "apply",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                FromVersion: persistedState.CurrentRelease?.Version ?? persistedState.LastKnownGoodRelease?.Version,
                ToVersion: state.StagedRelease!.Version,
                Succeeded: true,
                ErrorMessage: null), CancellationToken.None).ConfigureAwait(false);
            return (finalizedState, hostContext);
        }

        if (TryRecoverStuckApplyingState(state, hostContext, out UpdateStateRecord reconciledState))
        {
            await stateStore.SaveAsync(reconciledState, cancellationToken).ConfigureAwait(false);
            await operationStore.AppendAsync(new UpdateOperationRecord(
                OperationType: "apply",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                FromVersion: state.CurrentRelease?.Version,
                ToVersion: state.StagedRelease!.Version,
                Succeeded: false,
                ErrorMessage: reconciledState.LastError), CancellationToken.None).ConfigureAwait(false);
            return (reconciledState, hostContext);
        }

        if (!TryNormalizeOutOfBandHostVersion(persistedState, state, currentRelease, hostContext, out UpdateStateRecord normalizedState))
        {
            if (shouldPersistState)
            {
                await stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return (state, hostContext);
        }

        await stateStore.SaveAsync(normalizedState, cancellationToken).ConfigureAwait(false);
        return (normalizedState, hostContext);
    }

    private static bool TryNormalizeOutOfBandHostVersion(
        UpdateStateRecord persistedState,
        UpdateStateRecord state,
        UpdateReleaseReference currentRelease,
        UpdateServiceContext hostContext,
        out UpdateStateRecord normalizedState)
    {
        bool persistedCurrentMatchesHost = persistedState.CurrentRelease is not null
            && string.Equals(persistedState.CurrentRelease.Version, hostContext.ManagerVersion, StringComparison.OrdinalIgnoreCase);
        bool stagedReleaseMatchesHost = persistedState.StagedRelease is not null
            && string.Equals(persistedState.StagedRelease.Version, hostContext.ManagerVersion, StringComparison.OrdinalIgnoreCase);
        bool hasTransientOrRollbackState = state.State != UpdateWorkflowState.Current
            || state.AvailableRelease is not null
            || state.StagedRelease is not null
            || state.PreparedArtifacts is not null
            || state.LastKnownGoodRelease is not null;
        bool persistedCurrentChangedOutOfBand = persistedState.CurrentRelease is not null
            && !persistedCurrentMatchesHost
            && !stagedReleaseMatchesHost;

        if ((persistedCurrentMatchesHost || stagedReleaseMatchesHost)
            || (!hasTransientOrRollbackState && !persistedCurrentChangedOutOfBand))
        {
            normalizedState = state;
            return false;
        }

        UpdateReleaseReference? preservedRollbackTarget = state.LastKnownGoodRelease;
        if (preservedRollbackTarget is null
            && persistedState.CurrentRelease is not null
            && !string.Equals(persistedState.CurrentRelease.Version, currentRelease.Version, StringComparison.OrdinalIgnoreCase))
        {
            preservedRollbackTarget = persistedState.CurrentRelease;
        }

        normalizedState = state with
        {
            State = UpdateWorkflowState.Current,
            CurrentRelease = currentRelease,
            AvailableRelease = null,
            StagedRelease = null,
            PreparedArtifacts = null,
            LastKnownGoodRelease = preservedRollbackTarget,
            EligibilityState = UpdateEligibilityState.None,
            EligibilityReason = UpdateEligibilityReason.None,
            LastError = null,
        };

        return true;
    }

    private bool TryNormalizeStaleDiscoveryError(UpdateStateRecord state, out UpdateStateRecord normalizedState)
    {
        normalizedState = state;

        if (string.IsNullOrWhiteSpace(state.LastError) || !HasManifestDiscoveryConfigured())
        {
            return false;
        }

        if (!state.LastError.StartsWith("GitHub release discovery", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedState = state with
        {
            LastError = null,
            LastDiscoveryETag = null,
            AvailableRelease = null,
            EligibilityState = UpdateEligibilityState.None,
            EligibilityReason = UpdateEligibilityReason.None,
        };

        return true;
    }

    private static bool TryNormalizeInactiveStagedState(
        UpdateStateRecord state,
        UpdateReleaseReference currentRelease,
        out UpdateStateRecord normalizedState)
    {
        normalizedState = state;

        if (state.State == UpdateWorkflowState.Applying)
        {
            return false;
        }

        if (!ShouldInvalidateStagedRelease(state.StagedRelease, state.AvailableRelease, currentRelease)
            && state.PreparedArtifacts is null)
        {
            return false;
        }

        normalizedState = state with
        {
            StagedRelease = null,
            PreparedArtifacts = null,
        };

        return true;
    }

    private static bool ShouldInvalidateStagedRelease(
        UpdateReleaseReference? stagedRelease,
        UpdateReleaseReference? availableRelease,
        UpdateReleaseReference? currentRelease)
    {
        if (stagedRelease is null)
        {
            return false;
        }

        if (availableRelease is not null
            && !string.Equals(stagedRelease.ReleaseId, availableRelease.ReleaseId, StringComparison.Ordinal))
        {
            return true;
        }

        return currentRelease is not null
            && string.Equals(stagedRelease.Version, currentRelease.Version, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasManifestDiscoveryConfigured()
        => !string.IsNullOrWhiteSpace(options.ReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.PreviewReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.StableReleaseManifestUrl);

    private static UpdateStatusSnapshot CreateSnapshot(UpdateStateRecord state, UpdateServiceContext hostContext)
        => new(
            state.State,
            state.EffectiveReleaseChannel,
            state.EligibilityState,
            state.EligibilityReason,
            state.CurrentRelease,
            state.AvailableRelease,
            state.LastKnownGoodRelease,
            state.LastDiscoveryAttemptUtc,
            state.LastDiscoverySuccessUtc,
            hostContext.ManagerVersion,
            hostContext.IsReady,
            hostContext.ActiveExecCount,
            state.LastError);

    private static bool TryFinalizeAppliedRelease(
        UpdateStateRecord state,
        UpdateServiceContext hostContext,
        out UpdateStateRecord finalizedState)
    {
        if (state.State != UpdateWorkflowState.Applying
            || state.StagedRelease is null
            || !string.Equals(state.StagedRelease.Version, hostContext.ManagerVersion, StringComparison.OrdinalIgnoreCase)
            || !hostContext.CurrentPointersConsistent
            || !hostContext.RuntimeUnitsActive)
        {
            finalizedState = state;
            return false;
        }

        finalizedState = state with
        {
            State = UpdateWorkflowState.Current,
            StateChangedAtUtc = DateTimeOffset.UtcNow,
            CurrentRelease = state.StagedRelease,
            AvailableRelease = null,
            StagedRelease = null,
            PreparedArtifacts = null,
            LastError = null,
        };

        return true;
    }

    private bool TryRecoverStuckApplyingState(
        UpdateStateRecord state,
        UpdateServiceContext hostContext,
        out UpdateStateRecord recoveredState)
    {
        if (state.State != UpdateWorkflowState.Applying || state.StagedRelease is null)
        {
            recoveredState = state;
            return false;
        }

        if (string.Equals(state.StagedRelease.Version, hostContext.ManagerVersion, StringComparison.OrdinalIgnoreCase))
        {
            recoveredState = state;
            return false;
        }

        DateTimeOffset? applyingSinceUtc = state.StateChangedAtUtc;
        if (applyingSinceUtc is not null
            && DateTimeOffset.UtcNow - applyingSinceUtc <= GetApplyingRecoveryThreshold())
        {
            recoveredState = state;
            return false;
        }

        string message = $"Update apply for release {state.StagedRelease.Version} did not complete. The current release is still active, so the update was reset and can be retried.";

        logger.LogWarning(
            "Recovering stuck update apply state for target release {ReleaseId} while manager version remains {ManagerVersion}.",
            state.StagedRelease.ReleaseId,
            hostContext.ManagerVersion);

        recoveredState = state with
        {
            State = UpdateWorkflowState.Available,
            StateChangedAtUtc = DateTimeOffset.UtcNow,
            AvailableRelease = state.AvailableRelease ?? state.StagedRelease,
            StagedRelease = null,
            PreparedArtifacts = null,
            LastError = message,
        };

        return true;
    }

    private TimeSpan GetApplyingRecoveryThreshold()
        => TimeSpan.FromTicks(options.HostServiceActivationTimeout.Ticks * 2);

    private static UpdateWorkflowState ResolveWorkflowState(
        UpdateStateRecord currentState,
        ReleaseDiscoveryResult result,
        UpdateReleaseReference? availableRelease)
    {
        if (result.FailureReason is not null)
        {
            return result.IsUnsupported ? UpdateWorkflowState.Unsupported : currentState.State;
        }

        if (result.NotModified)
        {
            return currentState.State;
        }

        if (availableRelease is null)
        {
            return currentState.State is UpdateWorkflowState.Available or UpdateWorkflowState.Unsupported
                ? UpdateWorkflowState.Current
                : currentState.State;
        }

        return UpdateWorkflowState.Available;
    }

    private UpdateReleaseReference ResolveCurrentRelease(UpdateStateRecord state, UpdateServiceContext hostContext, string effectiveReleaseChannel)
    {
        if (state.CurrentRelease is not null
            && string.Equals(state.CurrentRelease.Version, hostContext.ManagerVersion, StringComparison.OrdinalIgnoreCase))
        {
            return state.CurrentRelease;
        }

        return new UpdateReleaseReference(
            ReleaseId: hostContext.ManagerReleaseId,
            Version: hostContext.ManagerVersion,
            Channel: effectiveReleaseChannel,
            ReleaseFingerprint: string.Empty,
            PlatformArtifactUrl: null,
            PlatformReleaseAssetUrl: null,
            PlatformReleaseSignatureAssetUrl: null,
            SourceUrl: null,
            PublishedAtUtc: null,
            Tag: hostContext.ManagerReleaseId);
    }

    private static (UpdateReleaseReference? AvailableRelease, UpdateEligibilityState EligibilityState, UpdateEligibilityReason EligibilityReason) ResolveEligibility(
        UpdateReleaseReference? currentRelease,
        UpdateReleaseReference? candidateRelease,
        string effectiveReleaseChannel,
        string managerVersion)
    {
        UpdateReleaseReference current = currentRelease
            ?? new UpdateReleaseReference(
                ReleaseId: managerVersion,
                Version: managerVersion,
                Channel: effectiveReleaseChannel,
                ReleaseFingerprint: string.Empty,
                PlatformArtifactUrl: null,
                PlatformReleaseAssetUrl: null,
                PlatformReleaseSignatureAssetUrl: null,
                SourceUrl: null,
                PublishedAtUtc: null,
                Tag: managerVersion);

        if (candidateRelease is null)
        {
            return BuildMissingCandidateOutcome(current, effectiveReleaseChannel);
        }

        if (!UpdateReleaseVersionComparer.TryCompare(candidateRelease.Version, current.Version, out int comparison))
        {
            return (null, UpdateEligibilityState.Blocked, UpdateEligibilityReason.ComparisonError);
        }

        if (comparison > 0)
        {
            return (candidateRelease, UpdateEligibilityState.Available, UpdateEligibilityReason.None);
        }

        return BuildMissingCandidateOutcome(current, effectiveReleaseChannel);
    }

    private static (UpdateReleaseReference? AvailableRelease, UpdateEligibilityState EligibilityState, UpdateEligibilityReason EligibilityReason) BuildMissingCandidateOutcome(
        UpdateReleaseReference currentRelease,
        string effectiveReleaseChannel)
    {
        if (string.Equals(effectiveReleaseChannel, "stable", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currentRelease.Channel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            return (null, UpdateEligibilityState.Blocked, UpdateEligibilityReason.NewerStableRequired);
        }

        if (string.Equals(effectiveReleaseChannel, "preview", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(currentRelease.Channel, "preview", StringComparison.OrdinalIgnoreCase))
        {
            return (null, UpdateEligibilityState.Blocked, UpdateEligibilityReason.NoNewerPreview);
        }

        return (null, UpdateEligibilityState.None, UpdateEligibilityReason.AlreadyCurrent);
    }

    private async Task<UpdatePreparedArtifacts> PrepareArtifactsAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
    {
        ReleaseTrustPolicy.EnsureSafeReleaseId(release.ReleaseId);
        string stageRoot = Path.Combine(options.StateRootPath, "updates", "staging", release.ReleaseId);
        string packagePath = Path.Combine(stageRoot, options.PlatformAssetName);
        string extractRoot = Path.Combine(stageRoot, "manager");

        Directory.CreateDirectory(stageRoot);

        bool requireVerification = ReleaseTrustPolicy.RequiresVerification(options);
        if (requireVerification && string.IsNullOrWhiteSpace(release.PlatformReleaseAssetUrl))
        {
            throw new InvalidOperationException(
                $"Release '{release.ReleaseId}' is missing a platform release manifest URL. Fail-closed verification requires a signed descriptor.");
        }

        PlatformReleaseDescriptor? descriptor = null;
        if (!string.IsNullOrWhiteSpace(release.PlatformReleaseAssetUrl))
        {
            ReleaseTrustPolicy.EnsureApprovedSourceUrl(release.PlatformReleaseAssetUrl, options, "Platform release manifest");
            if (!string.IsNullOrWhiteSpace(release.PlatformReleaseSignatureAssetUrl))
            {
                ReleaseTrustPolicy.EnsureApprovedSourceUrl(release.PlatformReleaseSignatureAssetUrl, options, "Platform release signature");
            }

            descriptor = await DownloadPlatformReleaseDescriptorAsync(
                release.PlatformReleaseAssetUrl,
                release.PlatformReleaseSignatureAssetUrl,
                cancellationToken).ConfigureAwait(false);
        }

        PlatformReleaseArtifactDescriptor? platformArtifact = ResolvePlatformArtifact(descriptor);
        if (requireVerification && platformArtifact is null)
        {
            throw new InvalidOperationException(
                $"Release '{release.ReleaseId}' platform release descriptor did not include a downloadable platform artifact.");
        }

        ValidateDescriptorMatchesCandidate(release, descriptor, platformArtifact, requireVerification);

        string artifactUrl = ResolvePlatformArtifactUrl(release, platformArtifact);
        ReleaseTrustPolicy.EnsureApprovedSourceUrl(artifactUrl, options, "Platform release artifact");

        await DownloadPlatformPackageAsync(artifactUrl, packagePath, cancellationToken).ConfigureAwait(false);
        ValidateDownloadedPlatformPackage(packagePath, platformArtifact);

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }

        Directory.CreateDirectory(extractRoot);
        await ExtractManagerPackageAsync(packagePath, extractRoot, cancellationToken).ConfigureAwait(false);

        return new UpdatePreparedArtifacts(
            TargetReleaseId: release.ReleaseId,
            PlatformPackagePath: packagePath,
            PlatformExtractedPath: extractRoot);
    }

    internal static void ValidateDescriptorMatchesCandidate(
        UpdateReleaseReference candidate,
        PlatformReleaseDescriptor? descriptor,
        PlatformReleaseArtifactDescriptor? artifact,
        bool requireVerification)
    {
        if (descriptor is null)
        {
            return;
        }

        static bool Matches(string? actual, string? expected, bool ignoreCase = false)
            => !string.IsNullOrWhiteSpace(actual)
                && !string.IsNullOrWhiteSpace(expected)
                && string.Equals(actual, expected, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (requireVerification
            && (!Matches(descriptor.Version, candidate.Version)
                || !Matches(descriptor.Channel, candidate.Channel, ignoreCase: true)
                || !Matches(descriptor.ReleaseTag, candidate.Tag)
                || (candidate.SourceSha is not null && !Matches(descriptor.SourceSha, candidate.SourceSha))))
        {
            throw new InvalidOperationException(
                $"Signed release manifest identity does not match discovered candidate '{candidate.Tag ?? candidate.ReleaseId}' ({candidate.Channel} {candidate.Version}, source {candidate.SourceSha ?? "unknown"}).");
        }

        if (artifact is null)
        {
            return;
        }

        if (requireVerification
            && (string.IsNullOrWhiteSpace(candidate.PlatformArtifactUrl)
                || string.IsNullOrWhiteSpace(candidate.PlatformReleaseSignatureAssetUrl)))
        {
            throw new InvalidOperationException("Discovered candidate is missing its exact platform artifact or signature URL.");
        }

        string resolvedArtifactUrl = !string.IsNullOrWhiteSpace(artifact.Url)
            ? artifact.Url
            : !string.IsNullOrWhiteSpace(artifact.Path) && !string.IsNullOrWhiteSpace(candidate.PlatformReleaseAssetUrl)
                ? new Uri(new Uri(candidate.PlatformReleaseAssetUrl, UriKind.Absolute), artifact.Path).ToString()
                : string.Empty;
        if (!string.IsNullOrWhiteSpace(candidate.PlatformArtifactUrl)
            && !Matches(resolvedArtifactUrl, candidate.PlatformArtifactUrl))
        {
            throw new InvalidOperationException("Signed release manifest artifact URL does not match the discovered candidate artifact.");
        }

        string? descriptorSignatureUrl = descriptor.Signature?.Url;
        if (string.IsNullOrWhiteSpace(descriptorSignatureUrl)
            && !string.IsNullOrWhiteSpace(descriptor.Signature?.Path)
            && !string.IsNullOrWhiteSpace(candidate.PlatformReleaseAssetUrl))
        {
            descriptorSignatureUrl = new Uri(new Uri(candidate.PlatformReleaseAssetUrl, UriKind.Absolute), descriptor.Signature.Path).ToString();
        }
        if (!string.IsNullOrWhiteSpace(candidate.PlatformReleaseSignatureAssetUrl)
            && !Matches(descriptorSignatureUrl, candidate.PlatformReleaseSignatureAssetUrl))
        {
            throw new InvalidOperationException("Signed release manifest signature URL does not match the discovered candidate signature asset.");
        }
    }

    private async Task DownloadPlatformPackageAsync(string artifactUrl, string destinationPath, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));
        client.DefaultRequestHeaders.Authorization = null;

        using HttpResponseMessage response = await client.GetAsync(new Uri(artifactUrl, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractManagerPackageAsync(string packagePath, string extractRoot, CancellationToken cancellationToken)
    {
        await using FileStream packageStream = File.OpenRead(packagePath);
        await using GZipStream gzipStream = new(packageStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzipStream, extractRoot, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
    }

    private string ResolvePlatformArtifactUrl(
        UpdateReleaseReference release,
        PlatformReleaseArtifactDescriptor? platformArtifact)
    {
        if (!string.IsNullOrWhiteSpace(release.PlatformArtifactUrl))
        {
            return release.PlatformArtifactUrl;
        }

        if (!string.IsNullOrWhiteSpace(platformArtifact?.Url))
        {
            return platformArtifact.Url!;
        }

        if (!string.IsNullOrWhiteSpace(platformArtifact?.Path)
            && !string.IsNullOrWhiteSpace(release.PlatformReleaseAssetUrl)
            && Uri.TryCreate(release.PlatformReleaseAssetUrl, UriKind.Absolute, out Uri? descriptorUri))
        {
            return new Uri(descriptorUri, platformArtifact.Path).ToString();
        }

        throw new InvalidOperationException($"Release '{release.ReleaseId}' does not contain a downloadable platform artifact URL.");
    }

    private PlatformReleaseArtifactDescriptor? ResolvePlatformArtifact(PlatformReleaseDescriptor? descriptor)
    {
        if (descriptor is null || descriptor.Artifacts.Count == 0)
        {
            return null;
        }

        PlatformReleaseArtifactDescriptor? byName = descriptor.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Name, options.PlatformAssetName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        PlatformReleaseArtifactDescriptor? byPlatform = descriptor.Artifacts.FirstOrDefault(static artifact =>
            string.Equals(artifact.Platform, "linux-x64", StringComparison.OrdinalIgnoreCase));
        if (byPlatform is not null)
        {
            return byPlatform;
        }

        return descriptor.Artifacts[0];
    }

    private void ValidateDownloadedPlatformPackage(string packagePath, PlatformReleaseArtifactDescriptor? artifact)
    {
        bool requireVerification = ReleaseTrustPolicy.RequiresVerification(options);
        if (artifact is null)
        {
            if (requireVerification)
            {
                throw new InvalidOperationException(
                    "Platform release artifact metadata was missing. Fail-closed verification requires artifact SHA-256.");
            }

            return;
        }

        ReleaseArtifactHashVerifier.Validate(
            packagePath,
            artifact.Sha256,
            artifact.Size,
            requireHash: requireVerification,
            artifactName: artifact.Name);
    }

    private async Task<PlatformReleaseDescriptor> DownloadPlatformReleaseDescriptorAsync(
        string descriptorUrl,
        string? signatureUrl,
        CancellationToken cancellationToken)
    {
        byte[] manifestBytes = await DownloadBytesAsync(descriptorUrl, cancellationToken).ConfigureAwait(false);
        bool requireVerification = ReleaseTrustPolicy.RequiresVerification(options);

        if (string.IsNullOrWhiteSpace(signatureUrl))
        {
            if (requireVerification)
            {
                throw new InvalidOperationException(
                    "Platform release signature URL was missing. Fail-closed verification requires a detached ECDSA signature.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.ManifestPublicKeyPath))
            {
                throw new InvalidOperationException("Platform release signature was published, but no trusted manifest public key path was configured.");
            }

            string publicKeyPem = await File.ReadAllTextAsync(options.ManifestPublicKeyPath, cancellationToken).ConfigureAwait(false);
            string signatureContent = Encoding.UTF8.GetString(await DownloadBytesAsync(signatureUrl, cancellationToken).ConfigureAwait(false));
            if (!PlatformReleaseSignatureVerifier.TryVerify(manifestBytes, signatureContent, publicKeyPem, out string? verificationError))
            {
                throw new InvalidOperationException(verificationError ?? "Platform release signature verification failed.");
            }
        }

        PlatformReleaseDescriptor? descriptor = JsonSerializer.Deserialize(
            manifestBytes,
            PlatformReleaseDescriptorJsonContext.Default.PlatformReleaseDescriptor);

        return descriptor ?? throw new InvalidOperationException("Platform release descriptor was empty.");
    }

    private async Task<byte[]> DownloadBytesAsync(string assetUrl, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));
        client.DefaultRequestHeaders.Authorization = null;

        using HttpResponseMessage response = await client.GetAsync(new Uri(assetUrl, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
