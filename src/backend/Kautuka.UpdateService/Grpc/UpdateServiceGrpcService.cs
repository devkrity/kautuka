using Grpc.Core;

using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.Protos;

using UpdateServiceGrpcContract = Kautuka.UpdateService.Protos.UpdateService;

namespace Kautuka.UpdateService.Grpc;

public sealed class UpdateServiceGrpcService(
    IUpdateCoordinator updateCoordinator,
    IHostedCatalogService hostedCatalogService) : UpdateServiceGrpcContract.UpdateServiceBase
{
    public override Task<UpdateServiceBoolResponse> Ping(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(new UpdateServiceBoolResponse
        {
            Ok = true,
            Message = string.IsNullOrWhiteSpace(request.Message) ? "pong" : request.Message,
        });
    }

    public override async Task<CatalogSnapshotResponse> GetCatalogSnapshot(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        HostedCatalogSnapshot snapshot = await hostedCatalogService.GetSnapshotAsync(context.CancellationToken).ConfigureAwait(false);
        CatalogSnapshotResponse response = new()
        {
            Ok = true,
            Message = string.IsNullOrWhiteSpace(request.Message) ? "catalog-snapshot" : request.Message,
            RefreshedAtUtc = snapshot.RefreshedAtUtc?.ToString("O") ?? string.Empty,
            SourceManifestUrl = snapshot.SourceManifestUrl ?? string.Empty,
            LastError = snapshot.LastError ?? string.Empty,
        };

        response.Entries.AddRange(snapshot.Entries.Select(entry => new CatalogEntryMessage
        {
            Id = entry.Id,
            Kind = entry.Kind,
            Title = entry.Title,
            Summary = entry.Summary,
            Publisher = entry.Publisher,
            Version = entry.Version,
            ArtifactUrl = entry.ArtifactUrl ?? string.Empty,
            ArtifactSha256 = entry.ArtifactSha256 ?? string.Empty,
            TrustKind = entry.TrustKind,
            Recommended = entry.Recommended,
        }));

        return response;
    }

    public override async Task<UpdateStatusResponse> GetUpdateStatus(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        UpdateStatusSnapshot status = await updateCoordinator.GetStatusAsync(context.CancellationToken).ConfigureAwait(false);
        return ToResponse(status, string.IsNullOrWhiteSpace(request.Message) ? "update-status" : request.Message);
    }

    public override async Task<UpdateStatusResponse> SetUpdatePolicy(UpdatePolicyRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            UpdateStatusSnapshot status = await updateCoordinator.ApplyUpdatePolicyAsync(
                request.ReleaseChannel,
                context.CancellationToken).ConfigureAwait(false);
            return ToResponse(status, "update-policy");
        }
        catch (Exception ex)
        {
            return new UpdateStatusResponse
            {
                Ok = false,
                Message = "update-policy-failed",
                LastError = ex.Message,
            };
        }
    }

    public override async Task<UpdateStatusResponse> RefreshUpdateDiscovery(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        await updateCoordinator.RefreshDiscoveryAsync(context.CancellationToken).ConfigureAwait(false);
        UpdateStatusSnapshot status = await updateCoordinator.GetStatusAsync(context.CancellationToken).ConfigureAwait(false);
        return ToResponse(status, string.IsNullOrWhiteSpace(request.Message) ? "update-refresh" : request.Message);
    }

    public override async Task<UpdateStatusResponse> ApplyUpdate(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Once an apply starts, let it finish even if the calling HTTP/gRPC request is canceled.
        UpdateStatusSnapshot status = await updateCoordinator.ApplyPreparedReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        return ToResponse(status, string.IsNullOrWhiteSpace(request.Message) ? "update-apply" : request.Message);
    }

    public override async Task<UpdateStatusResponse> RevertUpdate(UpdateServicePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Rollback is also a machine-local operation that should not be interrupted by a canceled client request.
        UpdateStatusSnapshot status = await updateCoordinator.RevertToLastKnownGoodAsync(CancellationToken.None).ConfigureAwait(false);
        return ToResponse(status, string.IsNullOrWhiteSpace(request.Message) ? "update-revert" : request.Message);
    }

    private static UpdateStatusResponse ToResponse(UpdateStatusSnapshot status, string message)
        => new()
        {
            Ok = string.IsNullOrWhiteSpace(status.LastError),
            Message = message,
            WorkflowState = status.State.ToString(),
            EffectiveReleaseChannel = status.EffectiveReleaseChannel,
            EligibilityState = status.EligibilityState.ToString(),
            EligibilityReason = status.EligibilityReason.ToString(),
            CurrentRelease = ToMessage(status.CurrentRelease),
            AvailableRelease = ToMessage(status.AvailableRelease),
            LastKnownGoodRelease = ToMessage(status.LastKnownGoodRelease),
            LastDiscoveryAttemptUtc = status.LastDiscoveryAttemptUtc?.ToString("O") ?? string.Empty,
            LastDiscoverySuccessUtc = status.LastDiscoverySuccessUtc?.ToString("O") ?? string.Empty,
            ManagerVersion = status.ManagerVersion,
            HostReady = status.HostReady,
            ActiveExecCount = status.ActiveExecCount,
            LastError = status.LastError ?? string.Empty,
        };

    private static UpdateReleaseReferenceMessage ToMessage(UpdateReleaseReference? release)
        => release is null
            ? new UpdateReleaseReferenceMessage()
            : new UpdateReleaseReferenceMessage
            {
                ReleaseId = release.ReleaseId,
                Version = release.Version,
                Channel = release.Channel,
                ReleaseFingerprint = release.ReleaseFingerprint,
                SourceUrl = release.SourceUrl ?? string.Empty,
                PublishedAtUtc = release.PublishedAtUtc?.ToString("O") ?? string.Empty,
                Tag = release.Tag ?? string.Empty,
            };
}
