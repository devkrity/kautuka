using Kautuka.Gateway.Infrastructure.Catalog;

namespace Kautuka.Gateway.Endpoints.Catalog;

internal static partial class GatewayCatalogEndpointMappings
{
    public static WebApplication MapGatewayCatalogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/catalog/recommendations", async (
            IGatewayHostedCatalogProvider catalogProvider,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayHostedCatalogSnapshot snapshot = await catalogProvider
                .ListRecommendedItemsAsync(user, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<GatewayHostedCatalogItem> items = snapshot.Items;

            GatewayCatalogRecommendationItem[] connectorRecommendations =
            [.. items.Where(static item => string.Equals(item.Kind, "connector", StringComparison.OrdinalIgnoreCase)).Select(ToRecommendation)];

            GatewayCatalogRecommendationItem[] skillRecommendations =
            [.. items.Where(static item => string.Equals(item.Kind, "skill", StringComparison.OrdinalIgnoreCase)).Select(ToRecommendation)];

            return Results.Json(
                new GatewayCatalogRecommendationsResponse(
                    connectorRecommendations,
                    skillRecommendations,
                    snapshot.UserMessage),
                KautukaJsonContext.Default.GatewayCatalogRecommendationsResponse);
        });

        app.MapPost("/api/catalog/recommendations/{kind}/{id}/install", async (
            string kind,
            string id,
            IGatewayHostedCatalogInstaller catalogInstaller,
            ILogger<GatewayHostedCatalogInstaller> logger,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                _ = await catalogInstaller.InstallAsync(kind, id, user, requestedScope: null, cancellationToken).ConfigureAwait(false);
                return Results.Json(new GatewaySavedResponse(true), KautukaJsonContext.Default.GatewaySavedResponse);
            }
            catch (KeyNotFoundException ex)
            {
                LogCatalogInstallNotFound(logger, ex, kind, id, user.Id);
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogCatalogInstallForbidden(logger, ex, kind, id, user.Id);
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (HttpRequestException ex)
            {
                LogCatalogInstallArtifactFetchFailed(logger, ex, kind, id, user.Id);
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException ex)
            {
                LogCatalogInstallInvalidOperation(logger, ex, kind, id, user.Id);
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        return app;
    }

    private static GatewayCatalogRecommendationItem ToRecommendation(GatewayHostedCatalogItem item)
        => new(
            item.Id,
            item.Kind,
            item.Title,
            item.Summary,
            item.Publisher,
            item.Version,
            item.IsAvailable,
            item.Enabled,
            item.CanInstall,
            item.TrustKind,
            item.InstallDisabledReason);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Catalog install failed: item not found. Kind={CatalogKind}, Id={CatalogId}, UserId={UserId}")]
    private static partial void LogCatalogInstallNotFound(
        ILogger logger,
        Exception exception,
        string catalogKind,
        string catalogId,
        string userId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Catalog install failed: access denied. Kind={CatalogKind}, Id={CatalogId}, UserId={UserId}")]
    private static partial void LogCatalogInstallForbidden(
        ILogger logger,
        Exception exception,
        string catalogKind,
        string catalogId,
        string userId);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Catalog install failed: artifact fetch error. Kind={CatalogKind}, Id={CatalogId}, UserId={UserId}")]
    private static partial void LogCatalogInstallArtifactFetchFailed(
        ILogger logger,
        Exception exception,
        string catalogKind,
        string catalogId,
        string userId);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Catalog install failed: invalid catalog artifact or metadata. Kind={CatalogKind}, Id={CatalogId}, UserId={UserId}")]
    private static partial void LogCatalogInstallInvalidOperation(
        ILogger logger,
        Exception exception,
        string catalogKind,
        string catalogId,
        string userId);
}
