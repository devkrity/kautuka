namespace Kautuka.Gateway.Infrastructure.Catalog;

internal sealed record GatewayHostedCatalogItem(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string Publisher,
    string Version,
    string? ArtifactUrl,
    string? ArtifactSha256,
    string TrustKind,
    bool Recommended,
    bool IsAvailable,
    bool Enabled,
    bool CanInstall,
    string? InstallDisabledReason);

internal sealed record GatewayHostedCatalogSnapshot(
    IReadOnlyList<GatewayHostedCatalogItem> Items,
    string? UserMessage);

internal interface IGatewayHostedCatalogProvider
{
    Task<GatewayHostedCatalogSnapshot> ListRecommendedItemsAsync(GatewayUser user, CancellationToken cancellationToken = default);

    Task<GatewayHostedCatalogItem?> GetItemAsync(string kind, string id, GatewayUser user, CancellationToken cancellationToken = default);
}
