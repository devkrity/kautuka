using Grpc.Core;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.UpdateService.Protos;

namespace Kautuka.Gateway.Infrastructure.Catalog;

internal sealed class UpdateServiceHostedCatalogProvider(
    IUpdateServiceGrpcService updateServiceGrpcService,
    IGatewayCatalogStore catalogStore) : IGatewayHostedCatalogProvider
{
    public async Task<GatewayHostedCatalogSnapshot> ListRecommendedItemsAsync(GatewayUser user, CancellationToken cancellationToken = default)
    {
        CatalogSnapshotResponse snapshot = await GetSnapshotAsync(suppressUnavailable: true, cancellationToken).ConfigureAwait(false);

        return new GatewayHostedCatalogSnapshot(
            Items:
            [
                .. snapshot.Entries
                .Where(static entry => entry.Recommended)
                .Select(entry => Project(entry, user, snapshot))
                .Where(static item => item is not null)
                .Select(static item => item!)
            ],
            UserMessage: ResolveCatalogUserMessage(snapshot));
    }

    public async Task<GatewayHostedCatalogItem?> GetItemAsync(string kind, string id, GatewayUser user, CancellationToken cancellationToken = default)
    {
        CatalogSnapshotResponse snapshot = await GetSnapshotAsync(suppressUnavailable: false, cancellationToken).ConfigureAwait(false);

        CatalogEntryMessage? entry = snapshot.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

        return entry is null ? null : Project(entry, user, snapshot);
    }

    private async Task<CatalogSnapshotResponse> GetSnapshotAsync(bool suppressUnavailable, CancellationToken cancellationToken)
    {
        try
        {
            return await updateServiceGrpcService.GetCatalogSnapshotAsync(
                new UpdateServicePingRequest { Message = "gateway-catalog" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException) when (suppressUnavailable)
        {
            return new CatalogSnapshotResponse { Ok = false };
        }
    }

    private GatewayHostedCatalogItem? Project(CatalogEntryMessage entry, GatewayUser user, CatalogSnapshotResponse snapshot)
    {
        if (string.Equals(entry.Kind, "connector", StringComparison.OrdinalIgnoreCase))
        {
            bool isAvailable = catalogStore.TryGetMcpServer(entry.Id, out GatewayMcpServer? server)
                && server is not null
                && GatewayOwnershipAccess.CanAccessConnector(user, server);
            bool enabled = isAvailable && server!.Enabled;
            bool canInstall = !isAvailable && !string.IsNullOrWhiteSpace(entry.ArtifactUrl);

            return new GatewayHostedCatalogItem(
                Id: entry.Id,
                Kind: "connector",
                Title: isAvailable && !string.IsNullOrWhiteSpace(server!.Name) ? server.Name : entry.Title,
                Summary: isAvailable && !string.IsNullOrWhiteSpace(server!.Description) ? server.Description : entry.Summary,
                Publisher: entry.Publisher,
                Version: entry.Version,
                ArtifactUrl: string.IsNullOrWhiteSpace(entry.ArtifactUrl) ? null : entry.ArtifactUrl,
                ArtifactSha256: string.IsNullOrWhiteSpace(entry.ArtifactSha256) ? null : entry.ArtifactSha256,
                TrustKind: entry.TrustKind,
                Recommended: entry.Recommended,
                IsAvailable: isAvailable,
                Enabled: enabled,
                CanInstall: canInstall,
                InstallDisabledReason: ResolveInstallDisabledReason(isAvailable, canInstall, snapshot));
        }

        if (string.Equals(entry.Kind, "skill", StringComparison.OrdinalIgnoreCase))
        {
            bool isAvailable = catalogStore.TryGetSkill(entry.Id, out GatewaySkill? skill)
                && skill is not null
                && GatewayOwnershipAccess.CanAccessSkill(user, skill);
            bool enabled = isAvailable && skill!.Enabled;
            bool canInstall = !isAvailable && !string.IsNullOrWhiteSpace(entry.ArtifactUrl);

            return new GatewayHostedCatalogItem(
                Id: entry.Id,
                Kind: "skill",
                Title: isAvailable && !string.IsNullOrWhiteSpace(skill!.Name) ? skill.Name : entry.Title,
                Summary: isAvailable && !string.IsNullOrWhiteSpace(skill!.Description) ? skill.Description : entry.Summary,
                Publisher: entry.Publisher,
                Version: entry.Version,
                ArtifactUrl: string.IsNullOrWhiteSpace(entry.ArtifactUrl) ? null : entry.ArtifactUrl,
                ArtifactSha256: string.IsNullOrWhiteSpace(entry.ArtifactSha256) ? null : entry.ArtifactSha256,
                TrustKind: entry.TrustKind,
                Recommended: entry.Recommended,
                IsAvailable: isAvailable,
                Enabled: enabled,
                CanInstall: canInstall,
                InstallDisabledReason: ResolveInstallDisabledReason(isAvailable, canInstall, snapshot));
        }

        return null;
    }

    private static string? ResolveInstallDisabledReason(bool isAvailable, bool canInstall, CatalogSnapshotResponse snapshot)
    {
        if (isAvailable || canInstall)
        {
            return null;
        }

        if (!snapshot.Ok || !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            return "Hosted catalog is currently unreachable. Check again later.";
        }

        if (string.IsNullOrWhiteSpace(snapshot.SourceManifestUrl))
        {
            return "Hosted catalog is currently unavailable. Check again later.";
        }

        return "Install is currently unavailable. Check again later.";
    }

    private static string? ResolveCatalogUserMessage(CatalogSnapshotResponse snapshot)
    {
        if (!snapshot.Ok || !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            return "Hosted catalog is currently unreachable. Check again later.";
        }

        return null;
    }
}
