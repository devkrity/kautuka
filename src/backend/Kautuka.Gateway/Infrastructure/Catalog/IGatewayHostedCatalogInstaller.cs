namespace Kautuka.Gateway.Infrastructure.Catalog;

internal sealed record GatewayHostedCatalogInstallResult(
    string Kind,
    string Id,
    bool Updated,
    bool NetworkApprovalRequired = false);

internal interface IGatewayHostedCatalogInstaller
{
    Task<GatewayHostedCatalogInstallResult> InstallAsync(
        string kind,
        string id,
        GatewayUser user,
        string? requestedScope,
        CancellationToken cancellationToken = default);
}
