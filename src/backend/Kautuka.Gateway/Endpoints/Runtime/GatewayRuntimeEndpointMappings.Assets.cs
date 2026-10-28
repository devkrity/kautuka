using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeAssetEndpoints(WebApplication app)
    {
        app.MapGet("/api/sessions/{sessionId}/assets", (
            string sessionId,
            HttpContext context,
            IConversationAssetStore assetStore,
            IGatewaySessionStore sessions,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Session '{sessionId}' not found."));
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string[]? ids = context.Request.Query.TryGetValue("ids", out Microsoft.Extensions.Primitives.StringValues idValues)
                ? [.. idValues
                    .SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(static value => value.Length > 0)]
                : null;
            IReadOnlyList<GatewayConversationAsset> assets = assetStore.ListSessionAssets(sessionId, user.Id, ids is { Length: > 0 } ? ids : null);
            return Results.Json(new GatewayConversationAssetsResponse([.. assets]), KautukaJsonContext.Default.GatewayConversationAssetsResponse);
        });

        app.MapPost("/api/sessions/{sessionId}/assets", async (
            string sessionId,
            HttpContext context,
            IGatewaySessionStore sessions,
            IGatewayCatalogStore catalog,
            IConversationAssetStore assetStore,
            IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Session '{sessionId}' not found."));
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                && (!catalog.TryGetAgent(session.TargetId, out GatewayAgent? agent) || !GatewayOwnershipAccess.CanAccessAgent(user, agent)))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            IFormFile? file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Asset upload requires a file."));
            }

            string origin = form.TryGetValue("origin", out Microsoft.Extensions.Primitives.StringValues originValues) && !string.IsNullOrWhiteSpace(originValues)
                ? originValues.ToString()
                : "userUpload";
            if (string.Equals(origin, "agentOutput", StringComparison.Ordinal))
            {
                return Results.BadRequest(new GatewayErrorResponse("Asset uploads cannot use origin 'agentOutput'."));
            }

            string disposition = form.TryGetValue("disposition", out Microsoft.Extensions.Primitives.StringValues dispositionValues) && !string.IsNullOrWhiteSpace(dispositionValues)
                ? dispositionValues.ToString()
                : "inlinePreview";

            try
            {
                using Stream stream = file.OpenReadStream();
                GatewayConversationAsset asset = await assetStore.CreateUploadAsync(
                    sessionId,
                    user.Id,
                    new AssetUploadCommand(file.FileName, file.ContentType ?? "application/octet-stream", file.Length, origin, disposition, stream),
                    cancellationToken).ConfigureAwait(false);

                return Results.Json(asset, KautukaJsonContext.Default.GatewayConversationAsset);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new GatewayErrorResponse(ex.Message));
            }
        });

        app.MapGet("/api/sessions/{sessionId}/assets/{assetId}", (
            string sessionId,
            string assetId,
            HttpContext context,
            IConversationAssetStore assetStore,
            IGatewaySessionStore sessions,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Session '{sessionId}' not found."));
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!assetStore.TryOpenAsset(sessionId, assetId, user.Id, out AssetDownloadResult? asset) || asset is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Asset '{assetId}' not found."));
            }

            return asset.ForceDownload
                ? Results.File(asset.Content, asset.ContentType, asset.FileName, enableRangeProcessing: true)
                : Results.File(asset.Content, asset.ContentType, enableRangeProcessing: true);
        });

        app.MapGet("/api/sessions/{sessionId}/assets/{assetId}/parsed", async (
            string sessionId,
            string assetId,
            HttpContext context,
            IParsedAssetStore parsedAssetStore,
            IGatewaySessionStore sessions,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Session '{sessionId}' not found."));
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            var parsedAsset = await parsedAssetStore.GetAsync(sessionId, assetId, context.RequestAborted).ConfigureAwait(false);
            if (parsedAsset is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Parsed asset '{assetId}' not found."));
            }

            var response = new GatewayParsedAssetStatusResponse(
                AssetId: parsedAsset.AssetId,
                SessionId: parsedAsset.SessionId,
                Status: ToParsedAssetStatusValue(parsedAsset.Status),
                ParserName: parsedAsset.ParserName,
                ParserVersion: parsedAsset.ParserVersion,
                Warning: parsedAsset.Warning,
                Error: parsedAsset.Error,
                UpdatedAt: parsedAsset.UpdatedAt);

            return Results.Json(response, KautukaJsonContext.Default.GatewayParsedAssetStatusResponse);
        });

        app.MapGet("/api/sessions/{sessionId}/assets/parsed", async (
            string sessionId,
            HttpContext context,
            IParsedAssetStore parsedAssetStore,
            IGatewaySessionStore sessions,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null)
            {
                return Results.NotFound(new GatewayErrorResponse($"Session '{sessionId}' not found."));
            }

            if (!GatewayOwnershipAccess.CanAccessSession(user, session))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            var parsedAssets = await parsedAssetStore.GetBySessionAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            var responses = parsedAssets.Select(parsedAsset => new GatewayParsedAssetStatusResponse(
                AssetId: parsedAsset.AssetId,
                SessionId: parsedAsset.SessionId,
                Status: ToParsedAssetStatusValue(parsedAsset.Status),
                ParserName: parsedAsset.ParserName,
                ParserVersion: parsedAsset.ParserVersion,
                Warning: parsedAsset.Warning,
                Error: parsedAsset.Error,
                UpdatedAt: parsedAsset.UpdatedAt)).ToArray();

            var response = new GatewayParsedAssetStatusesResponse(responses);
            return Results.Json(response, KautukaJsonContext.Default.GatewayParsedAssetStatusesResponse);
        });
    }

    private static string ToParsedAssetStatusValue(ParsedAssetStatus status)
        => status switch
        {
            ParsedAssetStatus.Pending => "pending",
            ParsedAssetStatus.Success => "success",
            ParsedAssetStatus.Failed => "failed",
            _ => "unknown",
        };
}
